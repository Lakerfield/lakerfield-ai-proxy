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

    // POST /v1/chat/completions
    [HttpPost("/v1/chat/completions")]
    public Task ForwardChatCompletions() => ForwardRequest("/v1/chat/completions");

    // POST /v1/completions
    [HttpPost("/v1/completions")]
    public Task ForwardCompletions() => ForwardRequest("/v1/completions");

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

            await response.Content.CopyToAsync(Response.Body, HttpContext.RequestAborted);

            var logEntry = new RequestLogEntry
            {
                RequestId = requestId,
                Endpoint = endpoint,
                Model = HttpContext.Items["Model"] as string,
                RoutedTo = instance.Name,
                StatusCode = (int)response.StatusCode,
                Streaming = isStreaming,
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
}
