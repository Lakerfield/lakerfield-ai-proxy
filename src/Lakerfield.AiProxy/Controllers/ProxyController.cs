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

    // POST /v1/messages (Anthropic Claude Messages API — Ollama natively supports this since v0.14.0)
    [HttpPost("/v1/messages")]
    public Task ForwardClaudeMessages() => ForwardRequest("/v1/messages");

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
        // Use the requestId and apiKey already set by RequestLoggingMiddleware so log entries match SignalR events
        var requestId = HttpContext.Items["RequestId"] as string ?? Guid.NewGuid().ToString();
        var apiKey = HttpContext.Items["ApiKey"] as string;

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
            ApiKey = apiKey,
            RequestBodySize = bodyJson?.Length,
        });

        OllamaInstance? instance = _loadBalancer.SelectInstance(model);
        if (instance == null)
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await Response.WriteAsync("No healthy Ollama instances available");
            return;
        }

        int maxRetries = 2; // TODO: make configurable via AiProxyOptions
        try
        {
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
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            _logger.LogInformation("Request {RequestId} cancelled by client", requestId);
            return;
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

            var responseHeadersForLog = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in response.Headers)
            {
                if (header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
                Response.Headers[header.Key] = header.Value.ToArray();
                responseHeadersForLog[header.Key] = string.Join(", ", header.Value);
            }
            foreach (var header in response.Content.Headers)
            {
                Response.Headers[header.Key] = header.Value.ToArray();
                responseHeadersForLog[header.Key] = string.Join(", ", header.Value);
            }

            int? inputTokens = null;
            int? outputTokens = null;
            string? responseBodyForLog = null;

            if (!isStreaming)
            {
                // Buffer response to parse token usage before forwarding
                var responseBody = await response.Content.ReadAsStringAsync(HttpContext.RequestAborted);
                TryParseTokenUsage(responseBody, out inputTokens, out outputTokens);
                responseBodyForLog = responseBody;
                await Response.Body.WriteAsync(Encoding.UTF8.GetBytes(responseBody), HttpContext.RequestAborted);
            }
            else
            {
                // Streaming: tee the response body to both the client and a capture buffer for logging
                using var responseStream = await response.Content.ReadAsStreamAsync(HttpContext.RequestAborted);
                using var captureStream = new MemoryStream();
                var readBuffer = new byte[8192];
                int bytesRead;
                while ((bytesRead = await responseStream.ReadAsync(readBuffer, HttpContext.RequestAborted)) > 0)
                {
                    await Response.Body.WriteAsync(readBuffer.AsMemory(0, bytesRead), HttpContext.RequestAborted);
                    await captureStream.WriteAsync(readBuffer.AsMemory(0, bytesRead), HttpContext.RequestAborted);
                }
                if (captureStream.Length > 0)
                {
                    responseBodyForLog = Encoding.UTF8.GetString(captureStream.GetBuffer(), 0, (int)captureStream.Length);
                    // Try to parse token usage from the last JSON line of the streaming response
                    var lines = responseBodyForLog.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines.Reverse())
                    {
                        var span = line.AsSpan();
                        if (span.StartsWith("data: ".AsSpan(), StringComparison.Ordinal))
                            span = span[6..];
                        var trimmed = span.TrimStart();
                        if (!trimmed.IsEmpty && trimmed[0] == '{')
                        {
                            TryParseTokenUsage(trimmed.ToString(), out inputTokens, out outputTokens);
                            if (inputTokens.HasValue || outputTokens.HasValue)
                                break;
                        }
                    }
                }
            }

            HttpContext.Items["InputTokens"] = inputTokens;
            HttpContext.Items["OutputTokens"] = outputTokens;
            HttpContext.Items["ResponseBody"] = responseBodyForLog;
            HttpContext.Items["ResponseHeaders"] = responseHeadersForLog;

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
                ApiKey = HttpContext.Items["ApiKey"] as string,
                RequestBodySize = bodyJson?.Length,
                ResponseBodySize = responseBodyForLog?.Length,
            };
            await _monitor.BroadcastRequestCompleted(logEntry);

            return true;
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected – don't mark instance as unhealthy, propagate so the retry loop stops
            throw;
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

            // Anthropic Messages API format: usage.input_tokens / usage.output_tokens
            // OpenAI-compatible format: usage.prompt_tokens / usage.completion_tokens
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("input_tokens", out var it))
                    inputTokens = it.GetInt32();
                if (usage.TryGetProperty("output_tokens", out var ot))
                    outputTokens = ot.GetInt32();
                if (usage.TryGetProperty("prompt_tokens", out var pt))
                    inputTokens = pt.GetInt32();
                if (usage.TryGetProperty("completion_tokens", out var ct))
                    outputTokens = ct.GetInt32();
            }
        }
        catch { /* ignore parse errors */ }
    }
}
