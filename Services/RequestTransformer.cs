using System.Text;
using System.Text.Json;

internal sealed class RequestTransformer
{
    private readonly ModelCatalogService _modelCatalogService;
    private readonly ReasoningCacheService _reasoningCacheService;

    public RequestTransformer(ModelCatalogService modelCatalogService, ReasoningCacheService reasoningCacheService)
    {
        _modelCatalogService = modelCatalogService;
        _reasoningCacheService = reasoningCacheService;
    }

    internal string ApplyExecutionDefaults(string rawBody, string model, string providerName = "")
    {
        ModelExecutionConfig exec = _modelCatalogService.GetExecutionConfigForModel(model);
        if (!exec.Temperature.HasValue && !exec.TopP.HasValue && !exec.MaxTokensPreferred.HasValue && string.IsNullOrWhiteSpace(exec.ReasoningEffort))
        {
            return rawBody;
        }

        // reasoning_effort is only supported by DeepSeek and OpenAI native APIs.
        // Use provider name first; fall back to model-name heuristics only when provider is unknown.
        string m = model.ToLowerInvariant();
        string p = providerName.ToLowerInvariant();
        bool knownProvider = p is "deepseek" or "openai" or "nvidia" or "openrouter" or "groq" or "ollama" or "ollamacloud";
        bool supportsReasoningEffort = p is "deepseek" or "openai"
            || (!knownProvider && (m.Contains("deepseek") || m.Contains("gpt-oss") || m.Contains("/o1") || m.Contains("/o3")));

        // DeepSeek & OpenAI o-series: sending both temperature and top_p simultaneously
        // causes undefined behaviour per official docs. When reasoning_effort is active
        // (i.e. the model is a native reasoner) only emit temperature, not top_p.
        bool isNativeReasoner = supportsReasoningEffort && !string.IsNullOrWhiteSpace(exec.ReasoningEffort);

        // Providers that do NOT support top_k: DeepSeek, OpenAI (including all OpenAI-derived models).
        // Providers that DO support top_k: NVIDIA, Groq, OpenRouter (passthrough).
        bool supportsTopK = p is "nvidia" or "groq" or "openrouter";
        // If provider is unknown, assume top_k is supported (lenient fallback).

        try
        {
            using JsonDocument original = JsonDocument.Parse(rawBody);
            JsonElement root = original.RootElement;
            using MemoryStream ms = new();
            using Utf8JsonWriter writer = new(ms);

            writer.WriteStartObject();

            bool hasTemperature = false;
            bool hasTopP = false;
            bool hasMaxTokens = false;
            bool hasReasoningEffort = false;

            foreach (JsonProperty prop in root.EnumerateObject())
            {
                if (prop.NameEquals("temperature")) hasTemperature = true;
                else if (prop.NameEquals("top_p")) hasTopP = true;
                else if (prop.NameEquals("max_tokens")) hasMaxTokens = true;
                else if (prop.NameEquals("reasoning_effort")) hasReasoningEffort = true;
                else if (prop.NameEquals("top_k") && !supportsTopK)
                {
                    // Skip top_k for providers that don't support it
                    continue;
                }

                prop.WriteTo(writer);
            }

            if (!hasTemperature && exec.Temperature.HasValue)
                writer.WriteNumber("temperature", exec.Temperature.Value);
            // Skip top_p injection for native reasoners to avoid temperature+top_p conflict.
            if (!hasTopP && exec.TopP.HasValue && !isNativeReasoner)
                writer.WriteNumber("top_p", exec.TopP.Value);
            if (!hasMaxTokens && exec.MaxTokensPreferred.HasValue)
                writer.WriteNumber("max_tokens", exec.MaxTokensPreferred.Value);
            // Only inject reasoning_effort for providers/models that natively support it.
            if (!hasReasoningEffort && !string.IsNullOrWhiteSpace(exec.ReasoningEffort) && supportsReasoningEffort)
                writer.WriteString("reasoning_effort", exec.ReasoningEffort);

            writer.WriteEndObject();
            writer.Flush();

            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return rawBody;
        }
    }

    internal string ReplaceModelInRequestBody(string rawBody, string upstreamModel)
    {
        try
        {
            using JsonDocument original = JsonDocument.Parse(rawBody);
            JsonElement root = original.RootElement;
            using MemoryStream ms = new();
            using Utf8JsonWriter writer = new(ms);

            writer.WriteStartObject();
            bool hasModel = false;

            foreach (JsonProperty prop in root.EnumerateObject())
            {
                if (prop.NameEquals("model"))
                {
                    writer.WriteString("model", upstreamModel);
                    hasModel = true;
                    continue;
                }

                prop.WriteTo(writer);
            }

            if (!hasModel)
            {
                writer.WriteString("model", upstreamModel);
            }

            writer.WriteEndObject();
            writer.Flush();

            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return rawBody;
        }
    }

    internal string? ModifyRequest(JsonDocument doc)
    {
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("messages", out JsonElement msgs))
        {
            return null;
        }

        int idx = 0;
        bool modified = false;
        using MemoryStream ms = new();
        using Utf8JsonWriter w = new(ms);

        w.WriteStartObject();
        foreach (JsonProperty prop in root.EnumerateObject())
        {
            if (!prop.NameEquals("messages"))
            {
                prop.WriteTo(w);
                continue;
            }

            w.WritePropertyName("messages");
            w.WriteStartArray();
            foreach (JsonElement msg in msgs.EnumerateArray())
            {
                string? role = msg.TryGetProperty("role", out JsonElement r) ? r.GetString() : null;
                if (role == "assistant")
                {
                    bool hasTc = msg.TryGetProperty("tool_calls", out JsonElement tcArr) && tcArr.GetArrayLength() > 0;
                    string? key = null;

                    if (hasTc)
                    {
                        List<string> ids = [];
                        foreach (JsonElement tc in tcArr.EnumerateArray())
                            if (tc.TryGetProperty("id", out JsonElement idE) && idE.ValueKind == JsonValueKind.String)
                                ids.Add(idE.GetString()!);
                        if (ids.Count > 0) key = $"toolcall:{string.Join("|", ids)}";
                    }
                    else
                    {
                        key = $"assistant:{idx++}";
                    }

                    if (key != null && _reasoningCacheService.TryGet(key, out string? rc))
                    {
                        bool needsInject = !msg.TryGetProperty("reasoning_content", out JsonElement exRc)
                            || exRc.ValueKind != JsonValueKind.String
                            || string.IsNullOrEmpty(exRc.GetString());

                        if (needsInject)
                        {
                            w.WriteStartObject();
                            foreach (JsonProperty mp in msg.EnumerateObject())
                                mp.WriteTo(w);
                            w.WriteString("reasoning_content", rc);
                            w.WriteEndObject();
                            modified = true;
                            continue;
                        }
                    }
                }

                msg.WriteTo(w);
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();
        w.Flush();

        return modified ? Encoding.UTF8.GetString(ms.ToArray()) : null;
    }
}
