using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static partial class AnthropicAdapter
{
    [GeneratedRegex("^data:(?<media>[^;]+);base64,(?<data>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DataUriRegex();

    internal sealed class MessageRecord(string role, List<Dictionary<string, object?>> blocks)
    {
        public string Role { get; } = role;
        public List<Dictionary<string, object?>> Blocks { get; } = blocks;
    }

    /// <summary>
    /// Translates an OpenAI /v1/chat/completions JSON request body into an Anthropic /v1/messages JSON request body.
    /// Handles extraction of system messages to top-level "system", conversion of tools, multimodal image parsing,
    /// tool calls / tool results, and strict user/assistant message alternation.
    /// </summary>
    internal static string BuildAnthropicMessagesRequest(string openAiRequestBody, string upstreamModel, bool isStream, int defaultMaxTokens = 8192)
    {
        using JsonDocument doc = JsonDocument.Parse(openAiRequestBody);
        JsonElement root = doc.RootElement;

        StringBuilder systemBuilder = new();
        List<MessageRecord> rawMessages = [];

        if (root.TryGetProperty("messages", out JsonElement messagesEl) && messagesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement msg in messagesEl.EnumerateArray())
            {
                string role = msg.TryGetProperty("role", out JsonElement r) ? r.GetString() ?? "user" : "user";

                if (role.Equals("system", StringComparison.OrdinalIgnoreCase))
                {
                    string sysText = ExtractTextContent(msg);
                    if (!string.IsNullOrWhiteSpace(sysText))
                    {
                        if (systemBuilder.Length > 0)
                            systemBuilder.Append("\n\n");
                        systemBuilder.Append(sysText);
                    }
                    continue;
                }

                if (role.Equals("tool", StringComparison.OrdinalIgnoreCase))
                {
                    // OpenAI tool message -> Anthropic user message with a tool_result content block
                    string toolCallId = msg.TryGetProperty("tool_call_id", out JsonElement tId) ? tId.GetString() ?? "" : "";
                    string toolContent = msg.TryGetProperty("content", out JsonElement tContent)
                        ? (tContent.ValueKind == JsonValueKind.String ? tContent.GetString() ?? "" : tContent.GetRawText())
                        : "";

                    rawMessages.Add(new MessageRecord("user",
                    [
                        new Dictionary<string, object?>
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = toolCallId,
                            ["content"] = toolContent
                        }
                    ]));
                    continue;
                }

                if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
                {
                    List<Dictionary<string, object?>> assistantBlocks = [];
                    string text = ExtractTextContent(msg);
                    if (!string.IsNullOrEmpty(text))
                    {
                        assistantBlocks.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "text",
                            ["text"] = text
                        });
                    }

                    if (msg.TryGetProperty("tool_calls", out JsonElement tcEl) && tcEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement tc in tcEl.EnumerateArray())
                        {
                            string tcId = tc.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() ?? "" : $"call_{Guid.NewGuid():N}";
                            string tcName = "";
                            object tcInput = new Dictionary<string, object?>();

                            if (tc.TryGetProperty("function", out JsonElement fn))
                            {
                                if (fn.TryGetProperty("name", out JsonElement n))
                                    tcName = n.GetString() ?? "";

                                if (fn.TryGetProperty("arguments", out JsonElement a))
                                {
                                    string argsStr = a.ValueKind == JsonValueKind.String ? a.GetString() ?? "{}" : a.GetRawText();
                                    try
                                    {
                                        using JsonDocument argsDoc = JsonDocument.Parse(argsStr);
                                        tcInput = JsonElementToObject(argsDoc.RootElement) ?? new Dictionary<string, object?>();
                                    }
                                    catch
                                    {
                                        tcInput = new Dictionary<string, object?>();
                                    }
                                }
                            }

                            assistantBlocks.Add(new Dictionary<string, object?>
                            {
                                ["type"] = "tool_use",
                                ["id"] = tcId,
                                ["name"] = tcName,
                                ["input"] = tcInput
                            });
                        }
                    }

                    if (assistantBlocks.Count == 0)
                    {
                        assistantBlocks.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = "" });
                    }

                    rawMessages.Add(new MessageRecord("assistant", assistantBlocks));
                    continue;
                }

                // User message
                List<Dictionary<string, object?>> userBlocks = [];
                if (msg.TryGetProperty("content", out JsonElement contentEl))
                {
                    if (contentEl.ValueKind == JsonValueKind.String)
                    {
                        userBlocks.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "text",
                            ["text"] = contentEl.GetString() ?? ""
                        });
                    }
                    else if (contentEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement part in contentEl.EnumerateArray())
                        {
                            if (part.TryGetProperty("type", out JsonElement typeEl))
                            {
                                string partType = typeEl.GetString() ?? "";
                                if (partType == "text" && part.TryGetProperty("text", out JsonElement txtEl))
                                {
                                    userBlocks.Add(new Dictionary<string, object?>
                                    {
                                        ["type"] = "text",
                                        ["text"] = txtEl.GetString() ?? ""
                                    });
                                }
                                else if (partType == "image_url" && part.TryGetProperty("image_url", out JsonElement imgUrlEl))
                                {
                                    string url = imgUrlEl.TryGetProperty("url", out JsonElement u) ? u.GetString() ?? "" : "";
                                    Match m = DataUriRegex().Match(url);
                                    if (m.Success)
                                    {
                                        userBlocks.Add(new Dictionary<string, object?>
                                        {
                                            ["type"] = "image",
                                            ["source"] = new Dictionary<string, object?>
                                            {
                                                ["type"] = "base64",
                                                ["media_type"] = m.Groups["media"].Value,
                                                ["data"] = m.Groups["data"].Value
                                            }
                                        });
                                    }
                                }
                            }
                        }
                    }
                }

                if (userBlocks.Count == 0)
                {
                    userBlocks.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = "" });
                }

                rawMessages.Add(new MessageRecord("user", userBlocks));
            }
        }

        // Merge consecutive messages with the same role (Anthropic requires strict alternation)
        List<MessageRecord> mergedMessages = [];
        foreach (var msg in rawMessages)
        {
            if (mergedMessages.Count > 0 && mergedMessages[^1].Role.Equals(msg.Role, StringComparison.OrdinalIgnoreCase))
            {
                mergedMessages[^1].Blocks.AddRange(msg.Blocks);
            }
            else
            {
                mergedMessages.Add(msg);
            }
        }

        // Anthropic requires the first message to have role 'user'
        if (mergedMessages.Count == 0)
        {
            mergedMessages.Add(new MessageRecord("user", [new Dictionary<string, object?> { ["type"] = "text", ["text"] = "Hello" }]));
        }
        else if (!mergedMessages[0].Role.Equals("user", StringComparison.OrdinalIgnoreCase))
        {
            mergedMessages.Insert(0, new MessageRecord("user", [new Dictionary<string, object?> { ["type"] = "text", ["text"] = "Continue" }]));
        }

        // Determine max_tokens (mandatory in Anthropic)
        int maxTokens = defaultMaxTokens;
        if (root.TryGetProperty("max_tokens", out JsonElement mt) && mt.ValueKind == JsonValueKind.Number && mt.GetInt32() > 0)
        {
            maxTokens = mt.GetInt32();
        }
        else if (root.TryGetProperty("max_completion_tokens", out JsonElement mct) && mct.ValueKind == JsonValueKind.Number && mct.GetInt32() > 0)
        {
            maxTokens = mct.GetInt32();
        }

        // Tools conversion
        List<Dictionary<string, object?>>? anthropicTools = null;
        if (root.TryGetProperty("tools", out JsonElement toolsEl) && toolsEl.ValueKind == JsonValueKind.Array)
        {
            anthropicTools = [];
            foreach (JsonElement t in toolsEl.EnumerateArray())
            {
                if (t.TryGetProperty("function", out JsonElement fn) && fn.ValueKind == JsonValueKind.Object)
                {
                    string name = fn.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
                    string desc = fn.TryGetProperty("description", out JsonElement d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
                    object schema = new Dictionary<string, object?> { ["type"] = "object" };

                    if (fn.TryGetProperty("parameters", out JsonElement p) && p.ValueKind == JsonValueKind.Object)
                    {
                        schema = JsonElementToObject(p) ?? new Dictionary<string, object?> { ["type"] = "object" };
                    }

                    var toolDict = new Dictionary<string, object?>
                    {
                        ["name"] = name,
                        ["input_schema"] = schema
                    };
                    if (!string.IsNullOrEmpty(desc))
                        toolDict["description"] = desc;

                    anthropicTools.Add(toolDict);
                }
            }
        }

        // Build final JSON payload
        Dictionary<string, object?> anthropicPayload = new()
        {
            ["model"] = upstreamModel,
            ["max_tokens"] = maxTokens,
            ["stream"] = isStream,
            ["messages"] = mergedMessages.Select(m => new Dictionary<string, object?>
            {
                ["role"] = m.Role,
                ["content"] = m.Blocks
            }).ToArray()
        };

        if (systemBuilder.Length > 0)
        {
            anthropicPayload["system"] = systemBuilder.ToString().Trim();
        }

        // Anthropic deprecated temperature and top_p for reasoning models (e.g. Fable)
        bool supportsTemperature = !upstreamModel.Contains("fable", StringComparison.OrdinalIgnoreCase);

        if (supportsTemperature && root.TryGetProperty("temperature", out JsonElement tempEl) && tempEl.ValueKind == JsonValueKind.Number)
        {
            anthropicPayload["temperature"] = Math.Clamp(tempEl.GetDouble(), 0.0, 1.0);
        }

        if (supportsTemperature && root.TryGetProperty("top_p", out JsonElement topPEl) && topPEl.ValueKind == JsonValueKind.Number)
        {
            anthropicPayload["top_p"] = topPEl.GetDouble();
        }

        if (root.TryGetProperty("top_k", out JsonElement topKEl) && topKEl.ValueKind == JsonValueKind.Number)
        {
            anthropicPayload["top_k"] = topKEl.GetInt32();
        }

        if (anthropicTools is not null && anthropicTools.Count > 0)
        {
            anthropicPayload["tools"] = anthropicTools;
        }

        return JsonSerializer.Serialize(anthropicPayload, JsonDefaults.SnakeCase);
    }

    /// <summary>
    /// Translates an Anthropic non-streaming /v1/messages response JSON into an OpenAI /v1/chat/completions response JSON.
    /// </summary>
    internal static string ConvertAnthropicToOpenAiCompletion(string anthropicResponseBody, string effectiveModel)
    {
        using JsonDocument doc = JsonDocument.Parse(anthropicResponseBody);
        JsonElement root = doc.RootElement;

        string id = root.TryGetProperty("id", out JsonElement idE) ? idE.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(id))
            id = $"chatcmpl-{Guid.NewGuid():N}";
        else if (!id.StartsWith("chatcmpl-", StringComparison.OrdinalIgnoreCase))
            id = $"chatcmpl-{id}";

        StringBuilder textContent = new();
        List<Dictionary<string, object?>>? toolCalls = null;

        if (root.TryGetProperty("content", out JsonElement contentEl) && contentEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement block in contentEl.EnumerateArray())
            {
                string type = block.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "" : "";
                if (type == "text" && block.TryGetProperty("text", out JsonElement txt))
                {
                    textContent.Append(txt.GetString());
                }
                else if (type == "tool_use")
                {
                    toolCalls ??= [];
                    string tcId = block.TryGetProperty("id", out JsonElement tId) ? tId.GetString() ?? "" : $"call_{Guid.NewGuid():N}";
                    string tcName = block.TryGetProperty("name", out JsonElement tName) ? tName.GetString() ?? "" : "";
                    string tcArgs = "{}";
                    if (block.TryGetProperty("input", out JsonElement inputEl))
                    {
                        tcArgs = inputEl.GetRawText();
                    }

                    toolCalls.Add(new Dictionary<string, object?>
                    {
                        ["id"] = tcId,
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?>
                        {
                            ["name"] = tcName,
                            ["arguments"] = tcArgs
                        }
                    });
                }
            }
        }

        string stopReason = root.TryGetProperty("stop_reason", out JsonElement sr) ? sr.GetString() ?? "stop" : "stop";
        string finishReason = stopReason switch
        {
            "end_turn" => "stop",
            "stop_sequence" => "stop",
            "max_tokens" => "length",
            "tool_use" => "tool_calls",
            _ => "stop"
        };

        if (toolCalls is not null && toolCalls.Count > 0 && finishReason == "stop")
        {
            finishReason = "tool_calls";
        }

        long promptTokens = 0, completionTokens = 0;
        if (root.TryGetProperty("usage", out JsonElement usage))
        {
            if (usage.TryGetProperty("input_tokens", out JsonElement it) && it.ValueKind == JsonValueKind.Number)
                promptTokens = it.GetInt64();
            if (usage.TryGetProperty("output_tokens", out JsonElement ot) && ot.ValueKind == JsonValueKind.Number)
                completionTokens = ot.GetInt64();
        }

        object completion = new
        {
            id,
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = effectiveModel,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = textContent.Length > 0 ? textContent.ToString() : (toolCalls != null ? null : string.Empty),
                        tool_calls = toolCalls
                    },
                    finish_reason = finishReason
                }
            },
            usage = new
            {
                prompt_tokens = promptTokens,
                completion_tokens = completionTokens,
                total_tokens = promptTokens + completionTokens
            }
        };

        return JsonSerializer.Serialize(completion, JsonDefaults.SnakeCase);
    }

    /// <summary>
    /// Translates an Anthropic non-streaming /v1/messages response JSON into an Ollama /api/chat response JSON.
    /// </summary>
    internal static string ConvertAnthropicToOllamaChat(string anthropicResponseBody, string effectiveModel)
    {
        using JsonDocument doc = JsonDocument.Parse(anthropicResponseBody);
        JsonElement root = doc.RootElement;

        StringBuilder textContent = new();
        List<Dictionary<string, object?>>? toolCalls = null;

        if (root.TryGetProperty("content", out JsonElement contentEl) && contentEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement block in contentEl.EnumerateArray())
            {
                string type = block.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "" : "";
                if (type == "text" && block.TryGetProperty("text", out JsonElement txt))
                {
                    textContent.Append(txt.GetString());
                }
                else if (type == "tool_use")
                {
                    toolCalls ??= [];
                    string tcName = block.TryGetProperty("name", out JsonElement tName) ? tName.GetString() ?? "" : "";
                    object tcInput = new Dictionary<string, object?>();
                    if (block.TryGetProperty("input", out JsonElement inputEl))
                    {
                        tcInput = JsonElementToObject(inputEl) ?? new Dictionary<string, object?>();
                    }

                    toolCalls.Add(new Dictionary<string, object?>
                    {
                        ["function"] = new Dictionary<string, object?>
                        {
                            ["name"] = tcName,
                            ["arguments"] = tcInput
                        }
                    });
                }
            }
        }

        string stopReason = root.TryGetProperty("stop_reason", out JsonElement sr) ? sr.GetString() ?? "stop" : "stop";
        string finishReason = stopReason switch
        {
            "end_turn" => "stop",
            "stop_sequence" => "stop",
            "max_tokens" => "length",
            "tool_use" => "tool_calls",
            _ => "stop"
        };

        long promptTokens = 0, completionTokens = 0;
        if (root.TryGetProperty("usage", out JsonElement usage))
        {
            if (usage.TryGetProperty("input_tokens", out JsonElement it) && it.ValueKind == JsonValueKind.Number)
                promptTokens = it.GetInt64();
            if (usage.TryGetProperty("output_tokens", out JsonElement ot) && ot.ValueKind == JsonValueKind.Number)
                completionTokens = ot.GetInt64();
        }

        Dictionary<string, object?> ollamaMsg = new()
        {
            ["role"] = "assistant",
            ["content"] = textContent.ToString()
        };
        if (toolCalls is not null && toolCalls.Count > 0)
        {
            ollamaMsg["tool_calls"] = toolCalls;
        }

        Dictionary<string, object?> ollamaResp = new()
        {
            ["model"] = effectiveModel,
            ["created_at"] = DateTime.UtcNow.ToString("o"),
            ["message"] = ollamaMsg,
            ["done"] = true,
            ["done_reason"] = finishReason,
            ["prompt_eval_count"] = promptTokens,
            ["eval_count"] = completionTokens
        };

        return JsonSerializer.Serialize(ollamaResp, JsonDefaults.SnakeCase);
    }

    private static string ExtractTextContent(JsonElement msg)
    {
        if (!msg.TryGetProperty("content", out JsonElement c))
            return string.Empty;

        if (c.ValueKind == JsonValueKind.String)
            return c.GetString() ?? string.Empty;

        if (c.ValueKind == JsonValueKind.Array)
        {
            StringBuilder sb = new();
            foreach (JsonElement part in c.EnumerateArray())
            {
                if (part.TryGetProperty("type", out JsonElement t) && t.GetString() == "text")
                {
                    if (part.TryGetProperty("text", out JsonElement txt) && txt.ValueKind == JsonValueKind.String)
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(txt.GetString());
                    }
                }
            }
            return sb.ToString();
        }

        return string.Empty;
    }

    private static object? JsonElementToObject(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                Dictionary<string, object?> dict = new(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty prop in element.EnumerateObject())
                {
                    dict[prop.Name] = JsonElementToObject(prop.Value);
                }
                return dict;

            case JsonValueKind.Array:
                List<object?> list = [];
                foreach (JsonElement item in element.EnumerateArray())
                {
                    list.Add(JsonElementToObject(item));
                }
                return list;

            case JsonValueKind.String:
                return element.GetString();

            case JsonValueKind.Number:
                if (element.TryGetInt64(out long l)) return l;
                return element.GetDouble();

            case JsonValueKind.True:
                return true;

            case JsonValueKind.False:
                return false;

            case JsonValueKind.Null:
            default:
                return null;
        }
    }
}

