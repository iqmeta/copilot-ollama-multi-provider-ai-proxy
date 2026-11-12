using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ProxyTests;

[Collection("Proxy")]
public sealed class AnthropicIntegrationTests : IDisposable
{
    private readonly ProviderEnvScope _envScope;
    private readonly WebApplication _stub;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public AnthropicIntegrationTests()
    {
        _envScope = new ProviderEnvScope();

        // Build in-process Anthropic stub
        WebApplicationBuilder sb = WebApplication.CreateSlimBuilder();
        sb.WebHost.UseUrls("http://127.0.0.1:0");
        _stub = sb.Build();

        _stub.MapGet("/v1/models", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("""
            {
              "data": [
                { "id": "claude-3-5-sonnet-latest", "type": "model" }
              ]
            }
            """);
        });

        _stub.MapPost("/v1/messages", async (HttpContext ctx) =>
        {
            // Verify Anthropic-specific authentication and protocol headers
            Assert.Equal("test-anthropic-key", ctx.Request.Headers["x-api-key"].ToString());
            Assert.Equal("2023-06-01", ctx.Request.Headers["anthropic-version"].ToString());

            using StreamReader reader = new(ctx.Request.Body);
            string body = await reader.ReadToEndAsync();
            using JsonDocument doc = JsonDocument.Parse(body);
            bool isStream = doc.RootElement.TryGetProperty("stream", out JsonElement s) && s.GetBoolean();

            if (!isStream)
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("""
                {
                  "id": "msg_integration_123",
                  "type": "message",
                  "role": "assistant",
                  "content": [
                    { "type": "text", "text": "Anthropic stub response" }
                  ],
                  "model": "claude-3-5-sonnet-latest",
                  "stop_reason": "end_turn",
                  "usage": { "input_tokens": 12, "output_tokens": 18 }
                }
                """);
            }
            else
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/event-stream";
                await ctx.Response.WriteAsync("event: message_start\n");
                await ctx.Response.WriteAsync("data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_stream_456\",\"usage\":{\"input_tokens\":12}}}\n\n");
                await ctx.Response.Body.FlushAsync();

                await ctx.Response.WriteAsync("event: content_block_delta\n");
                await ctx.Response.WriteAsync("data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Streaming from Anthropic\"}}\n\n");
                await ctx.Response.Body.FlushAsync();

                await ctx.Response.WriteAsync("event: message_delta\n");
                await ctx.Response.WriteAsync("data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":18}}\n\n");
                await ctx.Response.Body.FlushAsync();

                await ctx.Response.WriteAsync("event: message_stop\n");
                await ctx.Response.WriteAsync("data: {\"type\":\"message_stop\"}\n\n");
                await ctx.Response.Body.FlushAsync();
            }
        });

        _stub.StartAsync().GetAwaiter().GetResult();
        string stubUrl = _stub.Urls.First();

        Environment.SetEnvironmentVariable("PROVIDER_ANTHROPIC_API_KEY", "test-anthropic-key");
        Environment.SetEnvironmentVariable("PROVIDER_ANTHROPIC_BASE_URL", stubUrl);

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("ASPNETCORE_ENVIRONMENT", "Testing"));
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task V1Chat_NonStreaming_RoutesToAnthropicMessagesAndTranslatesResponse()
    {
        string requestJson = """
        {
          "model": "claude-sonnet-5-5@anthropic",
          "messages": [
            { "role": "system", "content": "You are a helpful assistant." },
            { "role": "user", "content": "Hello!" }
          ],
          "stream": false
        }
        """;

        using StringContent content = new(requestJson, Encoding.UTF8, "application/json");
        using HttpResponseMessage resp = await _client.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("anthropic", resp.Headers.GetValues("X-Proxy-Primary-Provider").First());

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        Assert.Equal("chat.completion", root.GetProperty("object").GetString());
        JsonElement choice = root.GetProperty("choices")[0];
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
        Assert.Equal("assistant", choice.GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("Anthropic stub response", choice.GetProperty("message").GetProperty("content").GetString());

        JsonElement usage = root.GetProperty("usage");
        Assert.Equal(12, usage.GetProperty("prompt_tokens").GetInt64());
        Assert.Equal(18, usage.GetProperty("completion_tokens").GetInt64());
        Assert.Equal(30, usage.GetProperty("total_tokens").GetInt64());
    }

    [Fact]
    public async Task V1Chat_Streaming_RoutesToAnthropicAndEmitsOpenAiSseChunks()
    {
        string requestJson = """
        {
          "model": "claude-sonnet-5-5@anthropic",
          "messages": [
            { "role": "user", "content": "Stream to me" }
          ],
          "stream": true
        }
        """;

        using StringContent content = new(requestJson, Encoding.UTF8, "application/json");
        using HttpResponseMessage resp = await _client.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Streaming from Anthropic", body);
        Assert.Contains("data: [DONE]", body);
    }

    [Fact]
    public async Task ApiChat_NonStreaming_RoutesToAnthropicAndTranslatesToOllamaJson()
    {
        string requestJson = """
        {
          "model": "claude-sonnet-5-5@anthropic",
          "messages": [
            { "role": "user", "content": "Hello Ollama" }
          ],
          "stream": false
        }
        """;

        using StringContent content = new(requestJson, Encoding.UTF8, "application/json");
        using HttpResponseMessage resp = await _client.PostAsync("/api/chat", content);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        Assert.True(root.GetProperty("done").GetBoolean());
        Assert.Equal("stop", root.GetProperty("done_reason").GetString());
        Assert.Equal("Anthropic stub response", root.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(12, root.GetProperty("prompt_eval_count").GetInt64());
        Assert.Equal(18, root.GetProperty("eval_count").GetInt64());
    }

    [Fact]
    public async Task ApiChat_Streaming_RoutesToAnthropicAndEmitsOllamaNdjson()
    {
        string requestJson = """
        {
          "model": "claude-sonnet-5-5@anthropic",
          "messages": [
            { "role": "user", "content": "Stream to Ollama" }
          ],
          "stream": true
        }
        """;

        using StringContent content = new(requestJson, Encoding.UTF8, "application/json");
        using HttpResponseMessage resp = await _client.PostAsync("/api/chat", content);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/x-ndjson", resp.Content.Headers.ContentType?.MediaType);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Streaming from Anthropic", body);
        Assert.Contains("\"done\":true", body);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _stub.StopAsync().GetAwaiter().GetResult();
        _stub.DisposeAsync().GetAwaiter().GetResult();
        _envScope.Dispose();
    }
}

