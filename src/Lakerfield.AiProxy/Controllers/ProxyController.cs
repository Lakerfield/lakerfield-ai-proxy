using System.Text;
using System.Text.Json;
using Lakerfield.AiProxy.Hubs;
using Lakerfield.AiProxy.Models;
using Lakerfield.AiProxy.Services;
using Microsoft.AspNetCore.Mvc;

namespace Lakerfield.AiProxy.Controllers;

[ApiController]
public class ProxyController : ControllerBase
{
    private readonly LoadBalancerService _loadBalancer;
    private readonly OllamaRegistryService _registry;
    private readonly RequestMonitorService _monitor;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ProxyController> _logger;

    public ProxyController(
        LoadBalancerService loadBalancer,
        OllamaRegistryService registry,
        RequestMonitorService monitor,
        IHttpClientFactory httpClientFactory,
        ILogger<ProxyController> logger)
    {
        _loadBalancer = loadBalancer;
        _registry = registry;
        _monitor = monitor;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // GET /v1/models
    [HttpGet("/v1/models")]
    public IActionResult GetModels()
    {
        var instances = _registry.GetHealthyInstances();
        var models = instances
            .SelectMany(i => i.Models)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(m => new
            {
                id = m,
                @object = "model",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                owned_by = "ollama"
            })
            .ToList();

        return Ok(new { @object = "list", data = models });
    }

    // GET /api/tags — Ollama native model list endpoint
    [HttpGet("/api/tags")]
    public IActionResult GetOllamaTags()
    {
        var instances = _registry.GetHealthyInstances();
        var models = instances
            .SelectMany(i => i.Models)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(m => new
            {
                name = m,   // model tag, e.g. "llama3:latest"
                model = m,  // full model identifier (same value, required by Ollama API)
                modified_at = DateTime.UtcNow.ToString("o"),
                size = 0,           // placeholder: actual size not tracked by proxy
                digest = string.Empty, // placeholder: actual digest not tracked by proxy
                details = new { }
            })
            .ToList();

        return Ok(new { models });
    }

    // POST /v1/messages (Anthropic Claude Messages API)
    [HttpPost("/v1/messages")]
    public Task ForwardClaudeMessages() => ForwardAnthropicRequest();

    // POST /v1/chat/completions
    [HttpPost("/v1/chat/completions")]
    public Task ForwardChatCompletions() => ForwardRequest("/v1/chat/completions");

    // POST /v1/completions
    [HttpPost("/v1/completions")]
    public Task ForwardCompletions() => ForwardRequest("/v1/completions");

    // POST /v1/responses (OpenAI Responses API — used by newer models)
    [HttpPost("/v1/responses")]
    public Task ForwardResponses() => ForwardRequest("/v1/responses");

    // POST /api/chat
    [HttpPost("/api/chat")]
    public Task ForwardOllamaChat() => ForwardRequest("/api/chat");

    // POST /api/generate
    [HttpPost("/api/generate")]
    public Task ForwardOllamaGenerate() => ForwardRequest("/api/generate");

    private async Task ForwardRequest(string endpoint)
    {
        var requestId = Guid.NewGuid().ToString();
        HttpContext.Items["RequestId"] = requestId;

        string? bodyJson = null;
        string? model = null;
        bool isStreaming = false;

        if (Request.ContentLength > 0 || Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            using var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
            bodyJson = await reader.ReadToEndAsync();

            try
            {
                using var doc = JsonDocument.Parse(bodyJson);
                if (doc.RootElement.TryGetProperty("model", out var modelEl))
                    model = modelEl.GetString();
                if (doc.RootElement.TryGetProperty("stream", out var streamEl))
                    isStreaming = streamEl.GetBoolean();
            }
            catch { /* ignore parse errors */ }
        }

        HttpContext.Items["Model"] = model;
        HttpContext.Items["Streaming"] = isStreaming;

        await _monitor.BroadcastRequestReceived(new RequestLogEntry
        {
            RequestId = requestId,
            Endpoint = endpoint,
            Model = model,
            Streaming = isStreaming,
        });

        OllamaInstance? instance = _loadBalancer.SelectInstance(model);
        if (instance == null)
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await Response.WriteAsync("No healthy Ollama instances available");
            return;
        }

        int maxRetries = 2; // TODO: make configurable via AiProxyOptions
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            var success = await TryForwardToInstance(instance, endpoint, bodyJson, requestId, isStreaming);
            if (success) return;

            if (attempt < maxRetries)
            {
                _logger.LogWarning("Request to '{Name}' failed, retrying with fallback instance", instance.Name);
                var fallback = _loadBalancer.SelectFallbackInstance(model, instance.Name);
                if (fallback == null) break;
                instance = fallback;
            }
        }

        await _monitor.BroadcastRequestFailed(requestId, "All instances failed");
        if (!Response.HasStarted)
        {
            Response.StatusCode = StatusCodes.Status502BadGateway;
            await Response.WriteAsync("All Ollama instances failed to handle the request");
        }
    }

    private async Task<bool> TryForwardToInstance(OllamaInstance instance, string endpoint, string? bodyJson, string requestId, bool isStreaming)
    {
        _registry.IncrementConnections(instance.Name);
        HttpContext.Items["RoutedTo"] = instance.Name;

        try
        {
            var client = _httpClientFactory.CreateClient("proxy");
            var targetUrl = $"{instance.BaseUrl.TrimEnd('/')}{endpoint}";

            await _monitor.BroadcastRequestForwarded(requestId, instance.Name);

            var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, targetUrl);

            foreach (var header in Request.Headers)
            {
                if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                try { upstreamRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()); }
                catch { /* ignore invalid headers */ }
            }

            if (bodyJson != null)
            {
                upstreamRequest.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
            }

            var response = await client.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);

            Response.StatusCode = (int)response.StatusCode;

            foreach (var header in response.Headers)
            {
                if (header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
                Response.Headers[header.Key] = header.Value.ToArray();
            }
            foreach (var header in response.Content.Headers)
            {
                Response.Headers[header.Key] = header.Value.ToArray();
            }

            int? inputTokens = null;
            int? outputTokens = null;

            if (!isStreaming)
            {
                // Buffer response to parse token usage before forwarding
                var responseBody = await response.Content.ReadAsStringAsync(HttpContext.RequestAborted);
                TryParseTokenUsage(responseBody, out inputTokens, out outputTokens);
                await Response.Body.WriteAsync(Encoding.UTF8.GetBytes(responseBody), HttpContext.RequestAborted);
            }
            else
            {
                await response.Content.CopyToAsync(Response.Body, HttpContext.RequestAborted);
            }

            HttpContext.Items["InputTokens"] = inputTokens;
            HttpContext.Items["OutputTokens"] = outputTokens;

            var logEntry = new RequestLogEntry
            {
                RequestId = requestId,
                Endpoint = endpoint,
                Model = HttpContext.Items["Model"] as string,
                RoutedTo = instance.Name,
                StatusCode = (int)response.StatusCode,
                Streaming = isStreaming,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
            };
            await _monitor.BroadcastRequestCompleted(logEntry);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error forwarding request to instance '{Name}'", instance.Name);
            _registry.MarkHealthy(instance.Name, false);
            return false;
        }
        finally
        {
            _registry.DecrementConnections(instance.Name);
        }
    }

    private async Task ForwardAnthropicRequest()
    {
        var requestId = Guid.NewGuid().ToString();
        HttpContext.Items["RequestId"] = requestId;

        string? bodyJson = null;
        string? model = null;
        bool isStreaming = false;

        if (Request.ContentLength > 0 || Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            using var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
            bodyJson = await reader.ReadToEndAsync();

            try
            {
                using var doc = JsonDocument.Parse(bodyJson);
                if (doc.RootElement.TryGetProperty("model", out var modelEl))
                    model = modelEl.GetString();
                if (doc.RootElement.TryGetProperty("stream", out var streamEl))
                    isStreaming = streamEl.GetBoolean();
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to parse model/stream from Anthropic request body"); }
        }

        HttpContext.Items["Model"] = model;
        HttpContext.Items["Streaming"] = isStreaming;

        await _monitor.BroadcastRequestReceived(new RequestLogEntry
        {
            RequestId = requestId,
            Endpoint = "/v1/messages",
            Model = model,
            Streaming = isStreaming,
        });

        OllamaInstance? instance = _loadBalancer.SelectInstance(model);
        if (instance == null)
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await Response.WriteAsync("No healthy Ollama instances available");
            return;
        }

        string? openAiBodyJson = null;
        if (bodyJson != null)
        {
            try { openAiBodyJson = ConvertAnthropicToOpenAI(bodyJson); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to convert Anthropic request to OpenAI format; forwarding original body");
                openAiBodyJson = bodyJson;
            }
        }

        int maxRetries = 2;
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            var success = await TryForwardAnthropicToInstance(instance, openAiBodyJson, requestId, isStreaming, model ?? "");
            if (success) return;

            if (attempt < maxRetries)
            {
                _logger.LogWarning("Request to '{Name}' failed, retrying with fallback instance", instance.Name);
                var fallback = _loadBalancer.SelectFallbackInstance(model, instance.Name);
                if (fallback == null) break;
                instance = fallback;
            }
        }

        await _monitor.BroadcastRequestFailed(requestId, "All instances failed");
        if (!Response.HasStarted)
        {
            Response.StatusCode = StatusCodes.Status502BadGateway;
            await Response.WriteAsync("All Ollama instances failed to handle the request");
        }
    }

    private async Task<bool> TryForwardAnthropicToInstance(OllamaInstance instance, string? openAiBodyJson, string requestId, bool isStreaming, string model)
    {
        _registry.IncrementConnections(instance.Name);
        HttpContext.Items["RoutedTo"] = instance.Name;

        try
        {
            var client = _httpClientFactory.CreateClient("proxy");
            var targetUrl = $"{instance.BaseUrl.TrimEnd('/')}/v1/chat/completions";

            await _monitor.BroadcastRequestForwarded(requestId, instance.Name);

            var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, targetUrl);

            foreach (var header in Request.Headers)
            {
                if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                if (header.Key.StartsWith("anthropic-", StringComparison.OrdinalIgnoreCase)) continue;
                try { upstreamRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()); }
                catch { /* ignore invalid headers */ }
            }

            if (openAiBodyJson != null)
                upstreamRequest.Content = new StringContent(openAiBodyJson, Encoding.UTF8, "application/json");

            var response = await client.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);

            Response.StatusCode = (int)response.StatusCode;

            int? inputTokens = null;
            int? outputTokens = null;

            if (isStreaming)
            {
                Response.ContentType = "text/event-stream";
                Response.Headers["Cache-Control"] = "no-cache";
                Response.Headers["X-Accel-Buffering"] = "no";

                var responseStream = await response.Content.ReadAsStreamAsync(HttpContext.RequestAborted);
                (inputTokens, outputTokens) = await ConvertAndForwardStreamAsync(responseStream, Response.Body, model, _logger, HttpContext.RequestAborted);
            }
            else
            {
                var openAiResponseBody = await response.Content.ReadAsStringAsync(HttpContext.RequestAborted);
                TryParseTokenUsage(openAiResponseBody, out inputTokens, out outputTokens);

                string anthropicResponseBody;
                try { anthropicResponseBody = ConvertOpenAIToAnthropic(openAiResponseBody, model); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to convert OpenAI response to Anthropic format; returning original body");
                    anthropicResponseBody = openAiResponseBody;
                }

                Response.ContentType = "application/json";
                await Response.Body.WriteAsync(Encoding.UTF8.GetBytes(anthropicResponseBody), HttpContext.RequestAborted);
            }

            HttpContext.Items["InputTokens"] = inputTokens;
            HttpContext.Items["OutputTokens"] = outputTokens;

            var logEntry = new RequestLogEntry
            {
                RequestId = requestId,
                Endpoint = "/v1/messages",
                Model = HttpContext.Items["Model"] as string,
                RoutedTo = instance.Name,
                StatusCode = (int)response.StatusCode,
                Streaming = isStreaming,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
            };
            await _monitor.BroadcastRequestCompleted(logEntry);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error forwarding Anthropic request to instance '{Name}'", instance.Name);
            _registry.MarkHealthy(instance.Name, false);
            return false;
        }
        finally
        {
            _registry.DecrementConnections(instance.Name);
        }
    }

    internal static string ConvertAnthropicToOpenAI(string anthropicJson)
    {
        using var inputDoc = JsonDocument.Parse(anthropicJson);
        var root = inputDoc.RootElement;

        var messages = new List<Dictionary<string, object>>();

        // Extract system prompt
        if (root.TryGetProperty("system", out var systemEl))
        {
            var systemText = ExtractTextContent(systemEl);
            if (!string.IsNullOrEmpty(systemText))
                messages.Add(new Dictionary<string, object> { ["role"] = "system", ["content"] = systemText });
        }

        // Convert messages
        if (root.TryGetProperty("messages", out var messagesEl))
        {
            foreach (var msg in messagesEl.EnumerateArray())
            {
                var role = msg.TryGetProperty("role", out var roleEl) ? roleEl.GetString() ?? "user" : "user";
                var content = msg.TryGetProperty("content", out var contentEl) ? ExtractTextContent(contentEl) : "";
                messages.Add(new Dictionary<string, object> { ["role"] = role, ["content"] = content });
            }
        }

        var openAiRequest = new Dictionary<string, object> { ["messages"] = messages };

        if (root.TryGetProperty("model", out var modelEl))
            openAiRequest["model"] = modelEl.GetString()!;
        if (root.TryGetProperty("max_tokens", out var maxTokensEl))
            openAiRequest["max_tokens"] = maxTokensEl.GetInt32();
        if (root.TryGetProperty("stream", out var streamEl))
            openAiRequest["stream"] = streamEl.GetBoolean();
        if (root.TryGetProperty("temperature", out var tempEl))
            openAiRequest["temperature"] = tempEl.GetDouble();
        if (root.TryGetProperty("top_p", out var topPEl))
            openAiRequest["top_p"] = topPEl.GetDouble();

        return JsonSerializer.Serialize(openAiRequest);
    }

    internal static string ConvertOpenAIToAnthropic(string openAiJson, string model)
    {
        using var doc = JsonDocument.Parse(openAiJson);
        var root = doc.RootElement;

        string responseText = "";
        string stopReason = "end_turn";
        int inputTokens = 0;
        int outputTokens = 0;

        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content))
                responseText = content.GetString() ?? "";
            if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind != JsonValueKind.Null)
            {
                stopReason = fr.GetString() switch
                {
                    "stop" => "end_turn",
                    "length" => "max_tokens",
                    "tool_calls" => "tool_use",
                    _ => "end_turn"
                };
            }
        }

        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var pt))
                inputTokens = pt.GetInt32();
            if (usage.TryGetProperty("completion_tokens", out var ct))
                outputTokens = ct.GetInt32();
        }

        return JsonSerializer.Serialize(new
        {
            id = $"msg_{Guid.NewGuid():N}",
            type = "message",
            role = "assistant",
            content = new[] { new { type = "text", text = responseText } },
            model,
            stop_reason = stopReason,
            stop_sequence = (string?)null,
            usage = new { input_tokens = inputTokens, output_tokens = outputTokens }
        });
    }

    private static async Task<(int? inputTokens, int? outputTokens)> ConvertAndForwardStreamAsync(Stream openAiStream, Stream outputStream, string model, ILogger logger, CancellationToken cancellationToken)
    {
        var messageId = $"msg_{Guid.NewGuid():N}";
        int? inputTokens = null;
        int outputTokens = 0;
        string stopReason = "end_turn";

        await WriteAnthropicSseEvent(outputStream, "message_start", new
        {
            type = "message_start",
            message = new
            {
                id = messageId,
                type = "message",
                role = "assistant",
                content = Array.Empty<object>(),
                model,
                stop_reason = (string?)null,
                stop_sequence = (string?)null,
                usage = new { input_tokens = 0, output_tokens = 0 }
            }
        }, cancellationToken);

        await WriteAnthropicSseEvent(outputStream, "content_block_start", new
        {
            type = "content_block_start",
            index = 0,
            content_block = new { type = "text", text = "" }
        }, cancellationToken);

        await WriteAnthropicSseEvent(outputStream, "ping", new { type = "ping" }, cancellationToken);

        using var reader = new StreamReader(openAiStream, Encoding.UTF8);
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null) break;
            if (!line.StartsWith("data: ")) continue;

            var data = line[6..].Trim();
            if (data == "[DONE]") break;

            try
            {
                using var chunkDoc = JsonDocument.Parse(data);
                var chunk = chunkDoc.RootElement;

                if (chunk.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var contentEl))
                    {
                        var text = contentEl.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            outputTokens++;
                            await WriteAnthropicSseEvent(outputStream, "content_block_delta", new
                            {
                                type = "content_block_delta",
                                index = 0,
                                delta = new { type = "text_delta", text }
                            }, cancellationToken);
                        }
                    }
                    if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind != JsonValueKind.Null)
                    {
                        stopReason = fr.GetString() switch
                        {
                            "stop" => "end_turn",
                            "length" => "max_tokens",
                            "tool_calls" => "tool_use",
                            _ => "end_turn"
                        };
                    }
                }

                if (chunk.TryGetProperty("usage", out var usageEl))
                {
                    if (usageEl.TryGetProperty("prompt_tokens", out var pt))
                        inputTokens = pt.GetInt32();
                    if (usageEl.TryGetProperty("completion_tokens", out var ct))
                        outputTokens = ct.GetInt32();
                }
            }
            catch (Exception ex) { logger.LogDebug(ex, "Failed to parse streaming chunk from OpenAI response"); }
        }

        await WriteAnthropicSseEvent(outputStream, "content_block_stop", new
        {
            type = "content_block_stop",
            index = 0
        }, cancellationToken);

        await WriteAnthropicSseEvent(outputStream, "message_delta", new
        {
            type = "message_delta",
            delta = new { stop_reason = stopReason, stop_sequence = (string?)null },
            usage = new { output_tokens = outputTokens }
        }, cancellationToken);

        await WriteAnthropicSseEvent(outputStream, "message_stop", new { type = "message_stop" }, cancellationToken);

        await outputStream.FlushAsync(cancellationToken);

        return (inputTokens, outputTokens > 0 ? outputTokens : null);
    }

    private static async Task WriteAnthropicSseEvent(Stream stream, string eventName, object data, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(data);
        var bytes = Encoding.UTF8.GetBytes($"event: {eventName}\ndata: {json}\n\n");
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static string ExtractTextContent(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString() ?? "";

        if (element.ValueKind == JsonValueKind.Array)
        {
            return string.Join("\n", element.EnumerateArray()
                .Where(b => b.TryGetProperty("type", out var t) && t.GetString() == "text")
                .Select(b => b.TryGetProperty("text", out var txt) ? txt.GetString() ?? "" : "")
                .Where(t => !string.IsNullOrEmpty(t)));
        }

        return "";
    }

    private static void TryParseTokenUsage(string json, out int? inputTokens, out int? outputTokens)
    {
        inputTokens = null;
        outputTokens = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Ollama native format: prompt_eval_count / eval_count
            if (root.TryGetProperty("prompt_eval_count", out var pec))
                inputTokens = pec.GetInt32();
            if (root.TryGetProperty("eval_count", out var ec))
                outputTokens = ec.GetInt32();

            // OpenAI-compatible format: usage.prompt_tokens / usage.completion_tokens
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt))
                    inputTokens = pt.GetInt32();
                if (usage.TryGetProperty("completion_tokens", out var ct))
                    outputTokens = ct.GetInt32();
            }
        }
        catch { /* ignore parse errors */ }
    }
}
