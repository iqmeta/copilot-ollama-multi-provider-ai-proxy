using System.Text.Json;
using Xunit;

namespace ProxyTests;

/// <summary>
/// Validates that ApplyExecutionDefaults injects the correct parameters
/// for every enabled model/provider declared in config/model-selection/*.json.
/// These tests are fully offline – no live API calls are made.
/// </summary>
public class ParameterValidationTests
{
    /// <summary>
    /// Creates a RequestTransformer with the current real environment variables.
    /// No snapshot or restore is needed since these are purely offline tests.
    /// </summary>
    private static RequestTransformer CreateTransformer()
    {
        ProviderHttpClientFactory httpClientFactory = new();
        ProviderRegistry providerRegistry           = new(httpClientFactory);
        ModelSelectionStore modelSelectionStore     = new();
        ModelCatalogService modelCatalog            = new(providerRegistry, modelSelectionStore);
        ReasoningCacheService cache                 = new();
        return new(modelCatalog, cache);
    }

    private static JsonElement Transform(RequestTransformer sut, string model, string provider = "")
    {
        string raw    = """{"model":"x","messages":[{"role":"user","content":"hi"}]}""";
        string result = sut.ApplyExecutionDefaults(raw, model, provider);
        return JsonDocument.Parse(result).RootElement;
    }

    private static JsonElement TransformWithBody(RequestTransformer sut, string body, string model, string provider = "")
    {
        string result = sut.ApplyExecutionDefaults(body, model, provider);
        return JsonDocument.Parse(result).RootElement;
    }

    // ──────────────────────────────────────────────
    // DeepSeek — 3 modelos: v4-pro, v4-flash, coder
    //   v4-pro / v4-flash: reasoning_effort, NO top_p
    //   coder: temperature, top_p, NO reasoning_effort
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("deepseek-v4-pro",              "deepseek", true,  false)]
    [InlineData("deepseek-v4-flash",            "deepseek", true,  false)]
    [InlineData("deepseek-coder-6.7b-instruct", "deepseek", false, true)]
    public void DeepSeek_ReasoningEffortPresenceMatchesModel(
        string model, string provider, bool expectReasoningEffort, bool expectTopP)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, provider);

        if (expectReasoningEffort)
            Assert.True(result.TryGetProperty("reasoning_effort", out _),
                $"{model}: reasoning_effort should be injected for DeepSeek reasoning models");
        else
            Assert.False(result.TryGetProperty("reasoning_effort", out _),
                $"{model}: reasoning_effort should NOT be injected for non-reasoning models");

        if (expectTopP)
            Assert.True(result.TryGetProperty("top_p", out _),
                $"{model}: top_p should be present for non-reasoning models");
        else
            Assert.False(result.TryGetProperty("top_p", out _),
                $"{model}: top_p must NOT be present alongside reasoning_effort (DeepSeek docs)");
    }

    [Theory]
    [InlineData("deepseek-v4-pro")]
    [InlineData("deepseek-v4-flash")]
    public void DeepSeek_ReasoningModels_MaxTokensInjected(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "deepseek");

        Assert.True(result.TryGetProperty("max_tokens", out JsonElement maxTok),
            $"{model}: max_tokens should be injected");
        Assert.True(maxTok.GetInt32() > 0,
            $"{model}: max_tokens must be a positive integer");
    }

    [Fact]
    public void DeepSeek_Coder_HasTemperatureAndTopP()
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, "deepseek-coder-6.7b-instruct", "deepseek");

        Assert.True(result.TryGetProperty("temperature", out _),
            "deepseek-coder: temperature should be injected");
        Assert.True(result.TryGetProperty("top_p", out _),
            "deepseek-coder: top_p should be injected");
    }

    // ──────────────────────────────────────────────
    // NVIDIA NIM — 6 modelos habilitados
    //   Ninguno soporta reasoning_effort
    // ──────────────────────────────────────────────

    public static TheoryData<string> NvidiaModels =>
    [
        "deepseek-ai/deepseek-v4-pro",
        "qwen/qwen3-coder-480b-a35b-instruct",
        "qwen/qwen3.5-397b-a17b",
        "nvidia/nemotron-3-ultra-550b-a55b",
        "nvidia/nemotron-3-super-120b-a12b",
        "nvidia/llama-3.1-nemotron-70b-instruct",
        "openai/gpt-oss-120b",
        "nvidia/llama-3.3-nemotron-super-49b-v1.5"
    ];

    [Theory]
    [MemberData(nameof(NvidiaModels))]
    public void Nvidia_Models_NoReasoningEffortLeakage(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "nvidia");

        Assert.False(result.TryGetProperty("reasoning_effort", out _),
            $"NVIDIA/{model}: reasoning_effort must NOT be sent (not supported by NVIDIA NIM API)");
    }

    [Theory]
    [MemberData(nameof(NvidiaModels))]
    public void Nvidia_Models_MaxTokensInjected(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "nvidia");

        Assert.True(result.TryGetProperty("max_tokens", out JsonElement maxTok),
            $"NVIDIA/{model}: max_tokens should be injected from config");
        Assert.True(maxTok.GetInt32() > 0,
            $"NVIDIA/{model}: max_tokens must be positive");
    }

    // ──────────────────────────────────────────────
    // OpenAI — 4 modelos
    //   gpt-5 / gpt-5-mini: reasoning_effort, NO top_p
    //   gpt-4.1 / gpt-4o: top_p, NO reasoning_effort
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("gpt-5")]
    [InlineData("gpt-5-mini")]
    [InlineData("gpt-4.1")]
    [InlineData("gpt-4o")]
    public void OpenAI_Models_MaxTokensInjected(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "openai");

        Assert.True(result.TryGetProperty("max_tokens", out JsonElement maxTok),
            $"OpenAI/{model}: max_tokens should be injected");
        Assert.True(maxTok.GetInt32() > 0,
            $"OpenAI/{model}: max_tokens must be positive");
    }

    [Theory]
    [InlineData("gpt-5")]
    [InlineData("gpt-5-mini")]
    public void OpenAI_ReasoningCapableModels_ReasoningEffortInjected(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "openai");

        Assert.True(result.TryGetProperty("reasoning_effort", out _),
            $"OpenAI/{model}: reasoning_effort should be injected (supported by OpenAI API)");
    }

    [Theory]
    [InlineData("gpt-4.1")]
    [InlineData("gpt-4o")]
    public void OpenAI_NonReasoningModels_NoReasoningEffort(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "openai");

        Assert.False(result.TryGetProperty("reasoning_effort", out _),
            $"OpenAI/{model}: reasoning_effort must NOT be injected (not a reasoning model)");
    }

    [Theory]
    [InlineData("gpt-5")]
    [InlineData("gpt-5-mini")]
    public void OpenAI_ReasoningModels_NoTopPAlongReasoningEffort(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "openai");

        bool hasRE = result.TryGetProperty("reasoning_effort", out _);
        bool hasTP = result.TryGetProperty("top_p", out _);

        if (hasRE)
            Assert.False(hasTP,
                $"OpenAI/{model}: top_p must NOT be sent when reasoning_effort is active");
    }

    // ──────────────────────────────────────────────
    // Groq — 2 modelos habilitados
    //   Ninguno soporta reasoning_effort
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("llama-3.3-70b-versatile")]
    [InlineData("qwen/qwen3-32b")]
    public void Groq_Models_NoReasoningEffortLeakage(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "groq");

        Assert.False(result.TryGetProperty("reasoning_effort", out _),
            $"Groq/{model}: reasoning_effort must NOT be sent (not supported by Groq API)");
    }

    [Theory]
    [InlineData("llama-3.3-70b-versatile")]
    [InlineData("qwen/qwen3-32b")]
    public void Groq_Models_MaxTokensInjected(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "groq");

        Assert.True(result.TryGetProperty("max_tokens", out JsonElement maxTok),
            $"Groq/{model}: max_tokens should be injected from config");
        Assert.True(maxTok.GetInt32() > 0,
            $"Groq/{model}: max_tokens must be positive");
    }

    // ──────────────────────────────────────────────
    // Ollama Cloud — 2 modelos habilitados
    //   Ninguno soporta reasoning_effort
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("gemma3:4b")]
    [InlineData("nemotron-3-super")]
    public void OllamaCloud_Models_MaxTokensInjected(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "ollamacloud");

        Assert.True(result.TryGetProperty("max_tokens", out JsonElement maxTok),
            $"OllamaCloud/{model}: max_tokens should be injected from config");
        Assert.True(maxTok.GetInt32() > 0,
            $"OllamaCloud/{model}: max_tokens must be positive");
    }

    [Theory]
    [InlineData("gemma3:4b")]
    [InlineData("nemotron-3-super")]
    public void OllamaCloud_Models_NoReasoningEffortLeakage(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "ollamacloud");

        Assert.False(result.TryGetProperty("reasoning_effort", out _),
            $"OllamaCloud/{model}: reasoning_effort must NOT be sent (not supported by Ollama Cloud API)");
    }

    // ──────────────────────────────────────────────
    // OpenRouter — 2 modelos free habilitados
    //   No inyecta reasoning_effort (passthrough)
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("nvidia/nemotron-3-super-120b-a12b:free")]
    [InlineData("qwen/qwen3-coder:free")]
    public void OpenRouter_Models_NoReasoningEffortLeakage(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "openrouter");

        Assert.False(result.TryGetProperty("reasoning_effort", out _),
            $"OpenRouter/{model}: reasoning_effort must NOT be sent");
    }

    // ──────────────────────────────────────────────
    // Moonshot / Kimi — 5 modelos habilitados
    //   Ninguno soporta reasoning_effort
    //   kimi-k2.6, moonshot-v1-128k/auto/32k/8k
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("kimi-k2.6")]
    [InlineData("moonshot-v1-128k")]
    [InlineData("moonshot-v1-auto")]
    [InlineData("moonshot-v1-32k")]
    [InlineData("moonshot-v1-8k")]
    public void Moonshot_Models_MaxTokensInjected(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "moonshot");

        Assert.True(result.TryGetProperty("max_tokens", out JsonElement maxTok),
            $"Moonshot/{model}: max_tokens should be injected from config");
        Assert.True(maxTok.GetInt32() > 0,
            $"Moonshot/{model}: max_tokens must be positive");
    }

    [Theory]
    [InlineData("kimi-k2.6")]
    [InlineData("moonshot-v1-128k")]
    [InlineData("moonshot-v1-auto")]
    [InlineData("moonshot-v1-32k")]
    [InlineData("moonshot-v1-8k")]
    public void Moonshot_Models_NoReasoningEffortLeakage(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "moonshot");

        Assert.False(result.TryGetProperty("reasoning_effort", out _),
            $"Moonshot/{model}: reasoning_effort must NOT be sent (not supported by Moonshot/Kimi API)");
    }

    [Theory]
    [InlineData("kimi-k2.6")]
    [InlineData("moonshot-v1-128k")]
    public void Moonshot_Models_TemperatureInjected(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "moonshot");

        Assert.True(result.TryGetProperty("temperature", out JsonElement temp),
            $"Moonshot/{model}: temperature should be injected from config");
        Assert.True(temp.GetDouble() > 0,
            $"Moonshot/{model}: temperature must be a positive value");
    }

    [Theory]
    [InlineData("kimi-k2.6")]
    [InlineData("moonshot-v1-128k")]
    [InlineData("moonshot-v1-8k")]
    public void Moonshot_Models_TopPInjected(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "moonshot");

        Assert.True(result.TryGetProperty("top_p", out JsonElement topP),
            $"Moonshot/{model}: top_p should be injected from config");
        Assert.True(topP.GetDouble() > 0,
            $"Moonshot/{model}: top_p must be a positive value");
    }

    // ──────────────────────────────────────────────
    // top_k filtering
    //   Filtered (NO soportan): DeepSeek, OpenAI, Moonshot
    //   Preserved (SÍ soportan): NVIDIA, Groq, OpenRouter
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("deepseek-v4-pro",   "deepseek")]
    [InlineData("gpt-5",             "openai")]
    [InlineData("deepseek-coder-6.7b-instruct", "deepseek")]
    [InlineData("kimi-k2.6",         "moonshot")]
    public void TopK_IsFiltered_ForNonSupportingProviders(string model, string provider)
    {
        RequestTransformer sut = CreateTransformer();
        string body   = """{"model":"x","messages":[{"role":"user","content":"hi"}],"top_k":50}""";
        JsonElement result = TransformWithBody(sut, body, model, provider);

        Assert.False(result.TryGetProperty("top_k", out _),
            $"{provider}/{model}: top_k must be filtered out (not supported by {provider})");
    }

    [Theory]
    [InlineData("qwen/qwen3-coder-480b-a35b-instruct", "nvidia")]
    [InlineData("llama-3.3-70b-versatile",              "groq")]
    [InlineData("nvidia/nemotron-3-super-120b-a12b",    "openrouter")]
    public void TopK_IsPreserved_ForSupportingProviders(string model, string provider)
    {
        RequestTransformer sut = CreateTransformer();
        string body   = """{"model":"x","messages":[{"role":"user","content":"hi"}],"top_k":50}""";
        JsonElement result = TransformWithBody(sut, body, model, provider);

        Assert.True(result.TryGetProperty("top_k", out JsonElement topK),
            $"{provider}/{model}: top_k should be preserved (supported by {provider})");
        Assert.Equal(50, topK.GetInt32());
    }

    // ──────────────────────────────────────────────
    // Client-supplied values are never overridden
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("deepseek-v4-pro",   "deepseek")]
    [InlineData("deepseek-v4-flash", "deepseek")]
    [InlineData("gpt-5",             "openai")]
    [InlineData("llama-3.3-70b-versatile", "groq")]
    [InlineData("kimi-k2.6",         "moonshot")]
    public void ClientSupplied_MaxTokens_IsNotOverridden(string model, string provider)
    {
        RequestTransformer sut = CreateTransformer();
        string body   = """{"model":"x","messages":[],"max_tokens":99}""";
        JsonElement result = TransformWithBody(sut, body, model, provider);

        Assert.True(result.TryGetProperty("max_tokens", out JsonElement maxTok),
            $"{provider}/{model}: max_tokens field must be present");
        Assert.Equal(99, maxTok.GetInt32());
    }

    [Theory]
    [InlineData("deepseek-v4-pro",   "deepseek")]
    [InlineData("gpt-5",             "openai")]
    public void ClientSupplied_ReasoningEffort_IsNotOverridden(string model, string provider)
    {
        RequestTransformer sut = CreateTransformer();
        string body   = """{"model":"x","messages":[],"reasoning_effort":"low"}""";
        JsonElement result = TransformWithBody(sut, body, model, provider);

        Assert.True(result.TryGetProperty("reasoning_effort", out JsonElement re),
            $"{provider}/{model}: reasoning_effort must be present");
        Assert.Equal("low", re.GetString());
    }

    [Theory]
    [InlineData("deepseek-coder-6.7b-instruct", "deepseek")]
    [InlineData("qwen/qwen3.5-397b-a17b",        "nvidia")]
    [InlineData("llama-3.3-70b-versatile",        "groq")]
    [InlineData("kimi-k2.6",                      "moonshot")]
    public void ClientSupplied_Temperature_IsNotOverridden(string model, string provider)
    {
        RequestTransformer sut = CreateTransformer();
        string body   = """{"model":"x","messages":[],"temperature":0.99}""";
        JsonElement result = TransformWithBody(sut, body, model, provider);

        Assert.True(result.TryGetProperty("temperature", out JsonElement temp),
            $"{provider}/{model}: temperature must be present");
        Assert.Equal(0.99, temp.GetDouble(), precision: 5);
    }

    [Theory]
    [InlineData("nvidia/nemotron-3-super-120b-a12b", "nvidia")]
    [InlineData("llama-3.3-70b-versatile",            "groq")]
    public void ClientSupplied_TopK_IsNotOverridden_ForSupportingProviders(string model, string provider)
    {
        RequestTransformer sut = CreateTransformer();
        string body   = """{"model":"x","messages":[],"top_k":42}""";
        JsonElement result = TransformWithBody(sut, body, model, provider);

        Assert.True(result.TryGetProperty("top_k", out JsonElement topK),
            $"{provider}/{model}: top_k must be present");
        Assert.Equal(42, topK.GetInt32());
    }

    // ──────────────────────────────────────────────
    // Context-window config completeness
    //   Verifica que TODOS los modelos habilitados
    //   tengan context_length y max_output_tokens
    // ──────────────────────────────────────────────

    [Theory]
    // DeepSeek (3)
    [InlineData("deepseek-v4-pro",              1_048_576, 384_000)]
    [InlineData("deepseek-v4-flash",            1_048_576, 131_072)]
    [InlineData("deepseek-coder-6.7b-instruct",   128_000,   8_192)]
    // NVIDIA NIM (6)
    [InlineData("deepseek-ai/deepseek-v4-pro",  1_048_576, 384_000)]
    [InlineData("qwen/qwen3-coder-480b-a35b-instruct", 1_048_576, 65_536)]
    [InlineData("qwen/qwen3.5-397b-a17b",          262_144,  16_384)]
    [InlineData("nvidia/nemotron-3-super-120b-a12b", 1_000_000, 262_144)]
    [InlineData("openai/gpt-oss-120b",             131_072, 131_072)]
    [InlineData("nvidia/llama-3.3-nemotron-super-49b-v1.5", 131_072, 16_384)]
    // OpenAI (4)
    [InlineData("gpt-5",      400_000, 128_000)]
    [InlineData("gpt-5-mini", 400_000, 128_000)]
    [InlineData("gpt-4.1",  1_048_576,  32_768)]
    [InlineData("gpt-4o",     128_000,   8_192)]
    // Groq (2)
    [InlineData("llama-3.3-70b-versatile",   131_072, 32_768)]
    [InlineData("qwen/qwen3-32b",            131_072, 16_384)]
    // Moonshot/Kimi (5)
    [InlineData("kimi-k2.6",          262_144, 262_144)]
    [InlineData("moonshot-v1-128k",   128_000,  32_768)]
    [InlineData("moonshot-v1-auto",   128_000,  32_768)]
    [InlineData("moonshot-v1-32k",     32_768,   8_192)]
    [InlineData("moonshot-v1-8k",       8_192,   4_096)]
    public void AllModels_HaveCorrectContextWindowConfig(
        string model, int expectedContextLength, int minMaxOutput)
    {
        ModelSelectionStore store  = new();
        ModelExecutionConfig exec  = store.GetExecutionConfigForModel(model, new Dictionary<string, ProviderInfo>());

        Assert.True(exec.ContextLength.HasValue,
            $"{model}: context_length must be configured");
        Assert.Equal(expectedContextLength, exec.ContextLength!.Value);

        if (minMaxOutput > 0)
        {
            Assert.True(exec.MaxOutputTokens.HasValue,
                $"{model}: max_output_tokens must be configured");
            Assert.True(exec.MaxOutputTokens!.Value >= minMaxOutput,
                $"{model}: max_output_tokens {exec.MaxOutputTokens} < expected {minMaxOutput}");
        }
    }

    // ──────────────────────────────────────────────
    // DeepSeek reasoning_effort value is valid
    //   API docs: "high" y "max"; proxy usa "low"/"medium"
    //   mapeados a "high" y "xhigh" a "max"
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("deepseek-v4-pro")]
    [InlineData("deepseek-v4-flash")]
    public void DeepSeek_ReasoningEffort_IsValidValue(string model)
    {
        ModelSelectionStore store  = new();
        ModelExecutionConfig exec  = store.GetExecutionConfigForModel(model, new Dictionary<string, ProviderInfo>());

        Assert.False(string.IsNullOrWhiteSpace(exec.ReasoningEffort),
            $"{model}: reasoning_effort should be configured in deepseek.json");

        string[] valid = ["low", "medium", "high", "default"];
        Assert.Contains(exec.ReasoningEffort, valid);
    }

    // ──────────────────────────────────────────────
    // Temperature sanity bounds [0.0, 2.0]
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("deepseek-v4-pro",   "deepseek")]
    [InlineData("deepseek-v4-flash", "deepseek")]
    [InlineData("deepseek-coder-6.7b-instruct", "deepseek")]
    [InlineData("deepseek-ai/deepseek-v4-pro",  "nvidia")]
    [InlineData("qwen/qwen3-coder-480b-a35b-instruct", "nvidia")]
    [InlineData("qwen/qwen3.5-397b-a17b",        "nvidia")]
    [InlineData("nvidia/nemotron-3-super-120b-a12b", "nvidia")]
    [InlineData("openai/gpt-oss-120b",           "nvidia")]
    [InlineData("nvidia/llama-3.3-nemotron-super-49b-v1.5", "nvidia")]
    [InlineData("gpt-5",        "openai")]
    [InlineData("gpt-5-mini",   "openai")]
    [InlineData("gpt-4.1",      "openai")]
    [InlineData("gpt-4o",       "openai")]
    [InlineData("llama-3.3-70b-versatile", "groq")]
    [InlineData("qwen/qwen3-32b",           "groq")]
    [InlineData("nvidia/nemotron-3-super-120b-a12b:free", "openrouter")]
    [InlineData("qwen/qwen3-coder:free",    "openrouter")]
    [InlineData("kimi-k2.6",    "moonshot")]
    [InlineData("moonshot-v1-128k", "moonshot")]
    [InlineData("moonshot-v1-32k",  "moonshot")]
    [InlineData("moonshot-v1-8k",   "moonshot")]
    public void ConfiguredTemperature_IsWithinValidRange(string model, string provider)
    {
        _ = provider;
        ModelSelectionStore  store = new();
        ModelExecutionConfig exec  = store.GetExecutionConfigForModel(model, new Dictionary<string, ProviderInfo>());

        if (!exec.Temperature.HasValue) return;

        double t = exec.Temperature.Value;
        Assert.True(t is >= 0.0 and <= 2.0,
            $"{model}: temperature {t} is outside [0, 2.0]");
    }

    // ──────────────────────────────────────────────
    // All provider config files exist and have models
    // ──────────────────────────────────────────────

    [Fact]
    public void AllProviderConfigFiles_HaveAtLeastOneModel()
    {
        ModelSelectionStore store = new();
        var providers = store.ProviderModelSelections;

        Assert.True(providers.Count >= 6,
            $"Expected at least 6 provider configs (deepseek, openai, nvidia, groq, openrouter, moonshot), got {providers.Count}");

        string[] expected = ["deepseek", "openai", "nvidia", "groq", "openrouter", "moonshot"];
        foreach (string name in expected)
        {
            Assert.True(providers.ContainsKey(name),
                $"Missing provider config: {name}");
            Assert.NotEmpty(providers[name]);
        }
    }

    // ──────────────────────────────────────────────
    // Enabled model count per provider
    //   Verifica que los modelos correctos están enabled
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("deepseek", 3)]
    [InlineData("openai", 4)]
    [InlineData("nvidia", 8)]
    [InlineData("groq", 4)]
    [InlineData("openrouter", 2)]
    [InlineData("moonshot", 5)]
    [InlineData("ollamacloud", 2)]
    public void EnabledModelCount_IsCorrect(string providerName, int expectedEnabled)
    {
        ModelSelectionStore store = new();
        var entries = store.GetProviderModelSelections(providerName);
        int enabled = entries.Count(e => e.Enabled);
        Assert.Equal(expectedEnabled, enabled);
    }
}