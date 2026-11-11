$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Get-EnvMap {
    $map = @{}
    Get-Content .env |
        Where-Object { $_ -match '^[A-Za-z_][A-Za-z0-9_]*=' } |
        ForEach-Object {
            $k, $v = $_ -split '=', 2
            $map[$k] = $v
        }
    return $map
}

function Get-Models($resp) {
    $models = @()

    if ($resp.data) {
        $models = @($resp.data | ForEach-Object {
            if ($_ -is [string]) { $_ }
            elseif ($_.id) { $_.id }
            elseif ($_.model) { $_.model }
            elseif ($_.name) { $_.name }
        })
    }
    elseif ($resp.models) {
        $models = @($resp.models | ForEach-Object {
            if ($_ -is [string]) { $_ }
            elseif ($_.model) { $_.model }
            elseif ($_.name) { $_.name }
            elseif ($_.id) { $_.id }
        })
    }

    return @($models | Where-Object { $_ } | Select-Object -Unique)
}

function Get-ChatText($resp) {
    # OpenAI-like
    if ($resp.choices -and $resp.choices.Count -gt 0) {
        $msg = $resp.choices[0].message
        if ($msg) {
            if (-not [string]::IsNullOrWhiteSpace($msg.content)) { return $msg.content }
            if (-not [string]::IsNullOrWhiteSpace($msg.reasoning_content)) { return $msg.reasoning_content }
            if (-not [string]::IsNullOrWhiteSpace($msg.reasoning)) { return $msg.reasoning }
        }
    }

    # Ollama-like
    if ($resp.message) {
        if (-not [string]::IsNullOrWhiteSpace($resp.message.content)) { return $resp.message.content }
        if (-not [string]::IsNullOrWhiteSpace($resp.message.reasoning)) { return $resp.message.reasoning }
    }

    # Anthropic-like
    if ($resp.content -and $resp.content.Count -gt 0) {
        $sampleThk = ''
        foreach ($b in $resp.content) {
            if ($b.type -eq 'text' -and -not [string]::IsNullOrWhiteSpace($b.text)) { return $b.text }
            if ($b.type -eq 'thinking' -and -not [string]::IsNullOrWhiteSpace($b.thinking)) { $sampleThk = $b.thinking }
        }
        if (-not [string]::IsNullOrWhiteSpace($sampleThk)) { return $sampleThk }
    }

    return ''
}

$envMap = Get-EnvMap

$providers = @(
    @{ name = 'deepseek'; key = $envMap['PROVIDER_DEEPSEEK_API_KEY']; base = $(if ($envMap['PROVIDER_DEEPSEEK_BASE_URL']) { $envMap['PROVIDER_DEEPSEEK_BASE_URL'] } else { 'https://api.deepseek.com' }); list = '/v1/models'; chat = '/v1/chat/completions'; hint = 'deepseek' ; ollama = $false },
    @{ name = 'openai'; key = $envMap['PROVIDER_OPENAI_API_KEY']; base = $(if ($envMap['PROVIDER_OPENAI_BASE_URL']) { $envMap['PROVIDER_OPENAI_BASE_URL'] } else { 'https://api.openai.com' }); list = '/v1/models'; chat = '/v1/chat/completions'; hint = 'gpt' ; ollama = $false },
    @{ name = 'groq'; key = $envMap['PROVIDER_GROQ_API_KEY']; base = $(if ($envMap['PROVIDER_GROQ_BASE_URL']) { $envMap['PROVIDER_GROQ_BASE_URL'] } else { 'https://api.groq.com/openai' }); list = '/v1/models'; chat = '/v1/chat/completions'; hint = 'llama|qwen|mixtral|deepseek|gpt-oss'; ollama = $false },
    @{ name = 'openrouter'; key = $envMap['PROVIDER_OPENROUTER_API_KEY']; base = $(if ($envMap['PROVIDER_OPENROUTER_BASE_URL']) { $envMap['PROVIDER_OPENROUTER_BASE_URL'] } else { 'https://openrouter.ai/api' }); list = '/v1/models'; chat = '/v1/chat/completions'; hint = 'qwen|deepseek|gpt-oss|llama'; ollama = $false },
    @{ name = 'nvidia'; key = $envMap['PROVIDER_NVIDIA_API_KEY']; base = $(if ($envMap['PROVIDER_NVIDIA_BASE_URL']) { $envMap['PROVIDER_NVIDIA_BASE_URL'] } else { 'https://integrate.api.nvidia.com' }); list = '/v1/models'; chat = '/v1/chat/completions'; hint = 'nemotron|qwen|deepseek|gpt-oss'; ollama = $false },
    @{ name = 'moonshot'; key = $envMap['PROVIDER_MOONSHOT_API_KEY']; base = $(if ($envMap['PROVIDER_MOONSHOT_BASE_URL']) { $envMap['PROVIDER_MOONSHOT_BASE_URL'] } else { 'https://api.moonshot.ai' }); list = '/v1/models'; chat = '/v1/chat/completions'; hint = 'kimi'; ollama = $false },
    @{ name = 'ollama-cloud'; key = $(if ($envMap['PROVIDER_OLLAMACLOUD_API_KEY']) { $envMap['PROVIDER_OLLAMACLOUD_API_KEY'] } else { $envMap['PROVIDER_OLLAMA_API_KEY'] }); base = $(if ($envMap['PROVIDER_OLLAMA_BASE_URL']) { $envMap['PROVIDER_OLLAMA_BASE_URL'] } else { 'https://ollama.com' }); list = '/api/tags'; chat = '/api/chat'; hint = 'qwen|deepseek|gpt-oss|llama|kimi|minimax|glm'; ollama = $true },
    @{ name = 'cerebras'; key = $envMap['PROVIDER_CEREBRAS_API_KEY']; base = $(if ($envMap['PROVIDER_CEREBRAS_BASE_URL']) { $envMap['PROVIDER_CEREBRAS_BASE_URL'] } else { 'https://api.cerebras.ai' }); list = '/v1/models'; chat = '/v1/chat/completions'; hint = 'glm|gpt-oss'; ollama = $false },
    @{ name = 'anthropic'; key = $(if ($envMap['PROVIDER_ANTHROPIC_API_KEY']) { $envMap['PROVIDER_ANTHROPIC_API_KEY'] } else { $envMap['ANTHROPIC_API_KEY'] }); base = $(if ($envMap['PROVIDER_ANTHROPIC_BASE_URL']) { $envMap['PROVIDER_ANTHROPIC_BASE_URL'] } else { 'https://api.anthropic.com' }); list = '/v1/models'; chat = '/v1/messages'; hint = 'sonnet|haiku'; ollama = $false; anthropic = $true }
)

$results = @()

foreach ($p in $providers) {
    if ([string]::IsNullOrWhiteSpace($p.key)) {
        $results += [pscustomobject]@{
            provider = $p.name
            list_status = 'missing_key'
            models_count = 0
            chat_status = 'not_tested'
            chat_model = ''
            latency_ms = 0
            sample_text = ''
            error = 'missing key'
        }
        continue
    }

    $headers = if ($p.anthropic) {
        $anthropicHeaders = @{ 'x-api-key' = $p.key; 'anthropic-version' = '2023-06-01' }
        $ws = if ($envMap['PROVIDER_ANTHROPIC_WORKSPACE_ID']) { $envMap['PROVIDER_ANTHROPIC_WORKSPACE_ID'] } else { $envMap['ANTHROPIC_WORKSPACE_ID'] }
        if ($ws) { $anthropicHeaders['anthropic-workspace-id'] = $ws }
        $anthropicHeaders
    } else {
        @{ Authorization = "Bearer $($p.key)" }
    }
    $listStatus = 'error'
    $chatStatus = 'not_tested'
    $models = @()
    $chatModel = ''
    $latency = 0
    $sampleText = ''
    $errorText = ''

    try {
        $listResp = Invoke-RestMethod -Uri ($p.base + $p.list) -Method Get -Headers $headers -TimeoutSec 40
        $models = Get-Models $listResp
        $listStatus = 'ok'
    }
    catch {
        try { $listStatus = 'http_' + $_.Exception.Response.StatusCode.value__ } catch { $listStatus = 'error' }
        $errorText = $_.Exception.Message
    }

    if ($listStatus -eq 'ok' -and $models.Count -gt 0) {
        $chatModel = $models | Where-Object { $_ -match $p.hint } | Select-Object -First 1
        if (-not $chatModel) { $chatModel = $models[0] }

        try {
            if ($p.ollama) {
                $body = @{
                    model = $chatModel
                    stream = $false
                    messages = @(@{ role = 'user'; content = 'Reply exactly OK' })
                } | ConvertTo-Json -Depth 6
            }
            elseif ($p.anthropic) {
                $body = @{
                    model = $chatModel
                    stream = $false
                    max_tokens = 64
                    messages = @(@{ role = 'user'; content = 'Reply exactly OK' })
                } | ConvertTo-Json -Depth 6
            }
            else {
                $body = @{
                    model = $chatModel
                    stream = $false
                    max_tokens = 32
                    temperature = 0.2
                    messages = @(@{ role = 'user'; content = 'Reply exactly OK' })
                } | ConvertTo-Json -Depth 6
            }

            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $chatResp = Invoke-RestMethod -Uri ($p.base + $p.chat) -Method Post -Headers ($headers + @{ 'Content-Type' = 'application/json' }) -Body $body -TimeoutSec 55
            $sw.Stop()

            $latency = [int]$sw.Elapsed.TotalMilliseconds
            $sampleText = Get-ChatText $chatResp

            if ([string]::IsNullOrWhiteSpace($sampleText)) {
                $chatStatus = 'empty_response'
            }
            else {
                $chatStatus = 'ok'
                if ($sampleText.Length -gt 120) { $sampleText = $sampleText.Substring(0, 120) }
            }
        }
        catch {
            try { $chatStatus = 'http_' + $_.Exception.Response.StatusCode.value__ } catch { $chatStatus = 'error' }

            if ([string]::IsNullOrWhiteSpace($errorText)) {
                try {
                    $sr = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())
                    $errorText = $sr.ReadToEnd()
                    $sr.Close()
                }
                catch {
                    $errorText = $_.Exception.Message
                }
            }
        }
    }

    $results += [pscustomobject]@{
        provider = $p.name
        list_status = $listStatus
        models_count = $models.Count
        chat_status = $chatStatus
        chat_model = $chatModel
        latency_ms = $latency
        sample_text = $sampleText
        error = $errorText
    }
}

$report = [pscustomobject]@{
    generated_at_utc = (Get-Date).ToUniversalTime().ToString('o')
    summary = [pscustomobject]@{
        total = $results.Count
        list_ok = ($results | Where-Object { $_.list_status -eq 'ok' }).Count
        chat_ok = ($results | Where-Object { $_.chat_status -eq 'ok' }).Count
    }
    providers = $results
}

$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$outPath = "docs/testing/provider-connectivity-$stamp.json"
$report | ConvertTo-Json -Depth 8 | Set-Content -Path $outPath -Encoding UTF8

Write-Host "Report: $outPath"
$results | Sort-Object provider | Format-Table provider, list_status, models_count, chat_status, chat_model, latency_ms -AutoSize