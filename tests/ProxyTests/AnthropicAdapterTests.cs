using System.Text.Json;

namespace ProxyTests;

public sealed class AnthropicAdapterTests
{
    [Fact]
    public void BuildAnthropicMessagesRequest_BasicPrompt_ConvertsCorrectly()
    {
        string openAiJson = """
        {
          "model": "claude-3-5-sonnet-latest",
          "messages": [
            { "role": "user", "content": "Hello Claude" }
          ],
          "max_tokens": 1024,
          "temperature": 0.7
        }
        """;

        string result = AnthropicAdapter.BuildAnthropicMessagesRequest(openAiJson, "claude-3-5-sonnet-20241022", isStream: false);
        using JsonDocument doc = JsonDocument.Parse(result);
        JsonElement root = doc.RootElement;

        Assert.Equal("claude-3-5-sonnet-20241022", root.GetProperty("model").GetString());
        Assert.Equal(1024, root.GetProperty("max_tokens").GetInt32());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(0.7, root.GetProperty("temperature").GetDouble(), precision: 2);

        JsonElement messages = root.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        JsonElement first = messages[0];
        Assert.Equal("user", first.GetProperty("role").GetString());
        JsonElement content = first.GetProperty("content");
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("Hello Claude", content[0].GetProperty("text").GetString());
    }

    [Fact]
    public void BuildAnthropicMessagesRequest_ExtractsSystemPromptToTopLevel()
    {
        string openAiJson = """
        {
          "model": "test",
          "messages": [
            { "role": "system", "content": "You are a senior C# developer." },
            { "role": "user", "content": "Refactor this method" }
          ]
        }
        """;

        string result = AnthropicAdapter.BuildAnthropicMessagesRequest(openAiJson, "claude-3-5-sonnet-latest", isStream: true);
        using JsonDocument doc = JsonDocument.Parse(result);
        JsonElement root = doc.RootElement;

        Assert.True(root.TryGetProperty("system", out JsonElement sys));
        Assert.Equal("You are a senior C# developer.", sys.GetString());

        JsonElement messages = root.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
    }

    [Fact]
    public void BuildAnthropicMessagesRequest_MultipleSystemMessages_ConcatenatedWithDoubleNewline()
    {
        string openAiJson = """
        {
          "model": "test",
          "messages": [
            { "role": "system", "content": "Rule 1: Be concise." },
            { "role": "system", "content": "Rule 2: Write clean code." },
            { "role": "user", "content": "Hi" }
          ]
        }
        """;

        string result = AnthropicAdapter.BuildAnthropicMessagesRequest(openAiJson, "claude-3-5-sonnet-latest", isStream: false);
        using JsonDocument doc = JsonDocument.Parse(result);
        JsonElement root = doc.RootElement;

        Assert.Equal("Rule 1: Be concise.\n\nRule 2: Write clean code.", root.GetProperty("system").GetString());
    }

    [Fact]
    public void BuildAnthropicMessagesRequest_MergesConsecutiveSameRoleMessages()
    {
        string openAiJson = """
        {
          "model": "test",
          "messages": [
            { "role": "user", "content": "Part 1" },
            { "role": "user", "content": "Part 2" }
          ]
        }
        """;

        string result = AnthropicAdapter.BuildAnthropicMessagesRequest(openAiJson, "claude-3-5-sonnet-latest", isStream: false);
        using JsonDocument doc = JsonDocument.Parse(result);
        JsonElement root = doc.RootElement;

        JsonElement messages = root.GetProperty("messages");
        // Must be merged into 1 message to satisfy Anthropic alternation
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        JsonElement content = messages[0].GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("Part 1", content[0].GetProperty("text").GetString());
        Assert.Equal("Part 2", content[1].GetProperty("text").GetString());
    }

    [Fact]
    public void BuildAnthropicMessagesRequest_PrependsUserMessage_WhenFirstIsAssistant()
    {
        string openAiJson = """
        {
          "model": "test",
          "messages": [
            { "role": "assistant", "content": "Previous assistant response." }
          ]
        }
        """;

        string result = AnthropicAdapter.BuildAnthropicMessagesRequest(openAiJson, "claude-3-5-sonnet-latest", isStream: false);
        using JsonDocument doc = JsonDocument.Parse(result);
        JsonElement root = doc.RootElement;

        JsonElement messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
    }

    [Fact]
    public void BuildAnthropicMessagesRequest_ConvertsToolsAndToolCallsAndResults()
    {
        string openAiJson = """
        {
          "model": "test",
          "messages": [
            { "role": "user", "content": "What is the weather in Paris?" },
            {
              "role": "assistant",
              "content": null,
              "tool_calls": [
                {
                  "id": "call_12345",
                  "type": "function",
                  "function": {
                    "name": "get_weather",
                    "arguments": "{\"city\":\"Paris\"}"
                  }
                }
              ]
            },
            {
              "role": "tool",
              "tool_call_id": "call_12345",
              "content": "22C, Sunny"
            }
          ],
          "tools": [
            {
              "type": "function",
              "function": {
                "name": "get_weather",
                "description": "Get current weather for a city",
                "parameters": {
                  "type": "object",
                  "properties": {
                    "city": { "type": "string" }
                  },
                  "required": ["city"]
                }
              }
            }
          ]
        }
        """;

        string result = AnthropicAdapter.BuildAnthropicMessagesRequest(openAiJson, "claude-3-5-sonnet-latest", isStream: false);
        using JsonDocument doc = JsonDocument.Parse(result);
        JsonElement root = doc.RootElement;

        // Verify tools
        Assert.True(root.TryGetProperty("tools", out JsonElement tools));
        Assert.Equal(1, tools.GetArrayLength());
        JsonElement tool = tools[0];
        Assert.Equal("get_weather", tool.GetProperty("name").GetString());
        Assert.Equal("Get current weather for a city", tool.GetProperty("description").GetString());
        Assert.Equal("object", tool.GetProperty("input_schema").GetProperty("type").GetString());

        // Verify messages
        JsonElement messages = root.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());

        // Message 0: user
        Assert.Equal("user", messages[0].GetProperty("role").GetString());

        // Message 1: assistant tool_use
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        JsonElement asstBlocks = messages[1].GetProperty("content");
        Assert.Equal("tool_use", asstBlocks[0].GetProperty("type").GetString());
        Assert.Equal("call_12345", asstBlocks[0].GetProperty("id").GetString());
        Assert.Equal("get_weather", asstBlocks[0].GetProperty("name").GetString());
        Assert.Equal("Paris", asstBlocks[0].GetProperty("input").GetProperty("city").GetString());

        // Message 2: user tool_result
        Assert.Equal("user", messages[2].GetProperty("role").GetString());
        JsonElement userBlocks = messages[2].GetProperty("content");
        Assert.Equal("tool_result", userBlocks[0].GetProperty("type").GetString());
        Assert.Equal("call_12345", userBlocks[0].GetProperty("tool_use_id").GetString());
        Assert.Equal("22C, Sunny", userBlocks[0].GetProperty("content").GetString());
    }

    [Fact]
    public void BuildAnthropicMessagesRequest_ConvertsImageDataUri()
    {
        string openAiJson = """
        {
          "model": "test",
          "messages": [
            {
              "role": "user",
              "content": [
                { "type": "text", "text": "Describe this image" },
                { "type": "image_url", "image_url": { "url": "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=" } }
              ]
            }
          ]
        }
        """;

        string result = AnthropicAdapter.BuildAnthropicMessagesRequest(openAiJson, "claude-3-5-sonnet-latest", isStream: false);
        using JsonDocument doc = JsonDocument.Parse(result);
        JsonElement root = doc.RootElement;

        JsonElement content = root.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());

        JsonElement imgBlock = content[1];
        Assert.Equal("image", imgBlock.GetProperty("type").GetString());
        JsonElement source = imgBlock.GetProperty("source");
        Assert.Equal("base64", source.GetProperty("type").GetString());
        Assert.Equal("image/png", source.GetProperty("media_type").GetString());
        Assert.StartsWith("iVBOR", source.GetProperty("data").GetString());
    }

    [Fact]
    public void ConvertAnthropicToOpenAiCompletion_TextResponse_ConvertsCorrectly()
    {
        string anthropicJson = """
        {
          "id": "msg_01ABC123",
          "type": "message",
          "role": "assistant",
          "content": [
            { "type": "text", "text": "Hello from Claude!" }
          ],
          "model": "claude-3-5-sonnet-20241022",
          "stop_reason": "end_turn",
          "usage": {
            "input_tokens": 15,
            "output_tokens": 20
          }
        }
        """;

        string result = AnthropicAdapter.ConvertAnthropicToOpenAiCompletion(anthropicJson, "claude-3-5-sonnet-latest");
        using JsonDocument doc = JsonDocument.Parse(result);
        JsonElement root = doc.RootElement;

        Assert.Equal("chatcmpl-msg_01ABC123", root.GetProperty("id").GetString());
        Assert.Equal("chat.completion", root.GetProperty("object").GetString());
        Assert.Equal("claude-3-5-sonnet-latest", root.GetProperty("model").GetString());

        JsonElement choice = root.GetProperty("choices")[0];
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
        Assert.Equal("assistant", choice.GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("Hello from Claude!", choice.GetProperty("message").GetProperty("content").GetString());

        JsonElement usage = root.GetProperty("usage");
        Assert.Equal(15, usage.GetProperty("prompt_tokens").GetInt64());
        Assert.Equal(20, usage.GetProperty("completion_tokens").GetInt64());
        Assert.Equal(35, usage.GetProperty("total_tokens").GetInt64());
    }

    [Fact]
    public void ConvertAnthropicToOpenAiCompletion_ToolUseResponse_ConvertsCorrectly()
    {
        string anthropicJson = """
        {
          "id": "msg_01XYZ789",
          "type": "message",
          "role": "assistant",
          "content": [
            { "type": "text", "text": "Checking the weather..." },
            {
              "type": "tool_use",
              "id": "toolu_0112233",
              "name": "get_weather",
              "input": { "city": "Madrid" }
            }
          ],
          "model": "claude-3-5-sonnet-20241022",
          "stop_reason": "tool_use",
          "usage": {
            "input_tokens": 30,
            "output_tokens": 40
          }
        }
        """;

        string result = AnthropicAdapter.ConvertAnthropicToOpenAiCompletion(anthropicJson, "claude-3-5-sonnet-latest");
        using JsonDocument doc = JsonDocument.Parse(result);
        JsonElement root = doc.RootElement;

        JsonElement choice = root.GetProperty("choices")[0];
        Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());
        Assert.Equal("Checking the weather...", choice.GetProperty("message").GetProperty("content").GetString());

        JsonElement toolCalls = choice.GetProperty("message").GetProperty("tool_calls");
        Assert.Equal(1, toolCalls.GetArrayLength());
        JsonElement tc = toolCalls[0];
        Assert.Equal("toolu_0112233", tc.GetProperty("id").GetString());
        Assert.Equal("function", tc.GetProperty("type").GetString());
        Assert.Equal("get_weather", tc.GetProperty("function").GetProperty("name").GetString());
        string args = tc.GetProperty("function").GetProperty("arguments").GetString()!;
        Assert.Contains("Madrid", args);
    }

    [Fact]
    public void ConvertAnthropicToOllamaChat_ConvertsCorrectly()
    {
        string anthropicJson = """
        {
          "id": "msg_01Ollama",
          "type": "message",
          "role": "assistant",
          "content": [
            { "type": "text", "text": "Ollama response from Claude" }
          ],
          "model": "claude-3-5-sonnet-20241022",
          "stop_reason": "end_turn",
          "usage": {
            "input_tokens": 10,
            "output_tokens": 15
          }
        }
        """;

        string result = AnthropicAdapter.ConvertAnthropicToOllamaChat(anthropicJson, "claude-3-5-sonnet-latest");
        using JsonDocument doc = JsonDocument.Parse(result);
        JsonElement root = doc.RootElement;

        Assert.Equal("claude-3-5-sonnet-latest", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("done").GetBoolean());
        Assert.Equal("stop", root.GetProperty("done_reason").GetString());
        Assert.Equal("Ollama response from Claude", root.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(10, root.GetProperty("prompt_eval_count").GetInt64());
        Assert.Equal(15, root.GetProperty("eval_count").GetInt64());
    }

    [Theory]
    [InlineData("claude-3-5-sonnet-20241022", true)]
    [InlineData("claude-3-5-haiku-latest", true)]
    [InlineData("claude-3-7-sonnet-latest", true)]
    [InlineData("claude-3-opus-latest", true)]
    [InlineData("claude-sonnet-5-5", false)]
    [InlineData("claude-haiku-5-5", false)]
    [InlineData("claude-sonnet-5", false)]
    [InlineData("claude-haiku-5", false)]
    [InlineData("claude-opus-5-5", false)]
    [InlineData("claude-fable-5-2", false)]
    [InlineData("claude-fable-5", false)]
    [InlineData("claude-haiku-4-5-20251001", false)]
    [InlineData("claude-opus-4-7", false)]
    public void SupportsTemperature_CorrectlyIdentifiesSupportedModels(string model, bool expected)
    {
        Assert.Equal(expected, AnthropicAdapter.SupportsTemperature(model));
    }

    [Fact]
    public void BuildAnthropicMessagesRequest_Claude5AndFable_OmitsTemperatureAndTopPAndTopK()
    {
        string openAiJson = """
        {
          "model": "claude-sonnet-5-5",
          "messages": [
            { "role": "user", "content": "Hello Claude" }
          ],
          "max_tokens": 1024,
          "temperature": 0.7,
          "top_p": 0.9,
          "top_k": 40
        }
        """;

        string result = AnthropicAdapter.BuildAnthropicMessagesRequest(openAiJson, "claude-sonnet-5-5", isStream: false);
        using JsonDocument doc = JsonDocument.Parse(result);
        JsonElement root = doc.RootElement;

        Assert.Equal("claude-sonnet-5-5", root.GetProperty("model").GetString());
        Assert.Equal(1024, root.GetProperty("max_tokens").GetInt32());
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.False(root.TryGetProperty("top_p", out _));
        Assert.False(root.TryGetProperty("top_k", out _));
    }
}

