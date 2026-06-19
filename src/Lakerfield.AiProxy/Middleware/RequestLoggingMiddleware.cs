using System.Diagnostics;
using System.Text;
using Lakerfield.AiProxy.Models;
using Lakerfield.AiProxy.Services;

namespace Lakerfield.AiProxy.Middleware;

public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RequestLogService _logService;
    private readonly MetricsService _metrics;
    private readonly ActiveRequestStore _activeRequestStore;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(RequestDelegate next, RequestLogService logService, MetricsService metrics, ActiveRequestStore activeRequestStore, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logService = logService;
        _metrics = metrics;
        _activeRequestStore = activeRequestStore;
        _logger = logger;
    }

    internal static bool IsExcludedPath(string path) =>
        path.StartsWith("/api/metrics", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/api/instances", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/api/logs", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/metrics", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = Guid.NewGuid().ToString();
        context.Items["RequestId"] = requestId;

        // Skip metrics recording for internal dashboard/health endpoints
        if (IsExcludedPath(context.Request.Path.Value ?? string.Empty))
        {
            await _next(context);
            return;
        }

        // Capture request headers (excluding pseudo-headers and noisy transport headers)
        var requestHeaders = context.Request.Headers
            .Where(h => !h.Key.StartsWith(':') &&
                        !h.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key, h => h.Value.ToString());

        // Buffer request body so it can be read by both this middleware and the controller
        string? requestBody = null;
        int? requestBodySize = null;
        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
            requestBody = await reader.ReadToEndAsync();
            context.Request.Body.Seek(0, SeekOrigin.Begin);
            requestBodySize = requestBody?.Length;
        }

        var apiKey = ExtractApiKey(context.Request);
        context.Items["ApiKey"] = apiKey;
        var clientIp = ExtractClientIp(context);

        // Register request body/headers in the in-memory store so they are accessible
        // via the body popup API while the response is still streaming.
        _activeRequestStore.Add(requestId, requestBody, requestHeaders);

        var sw = Stopwatch.StartNew();
        string? errorMessage = null;

        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            throw;
        }
        finally
        {
            _activeRequestStore.Remove(requestId);
            sw.Stop();
            var model = context.Items["Model"] as string;
            var routedTo = context.Items["RoutedTo"] as string;

            var entry = new RequestLogEntry
            {
                RequestId = requestId,
                Endpoint = context.Request.Path.Value ?? string.Empty,
                StatusCode = context.Response.StatusCode,
                DurationMs = sw.ElapsedMilliseconds,
                Model = model,
                RoutedTo = routedTo,
                ApiKey = apiKey,
                ClientIp = clientIp,
                Streaming = context.Items["Streaming"] is true,
                InputTokens = context.Items["InputTokens"] as int?,
                OutputTokens = context.Items["OutputTokens"] as int?,
                RequestBody = requestBody,
                RequestBodySize = requestBodySize,
                RequestHeaders = requestHeaders,
                ResponseBody = context.Items["ResponseBody"] as string,
                ResponseBodySize = (context.Items["ResponseBody"] as string)?.Length,
                ResponseHeaders = context.Items["ResponseHeaders"] as Dictionary<string, string>,
                ErrorMessage = errorMessage,
            };

            _metrics.RecordRequest(model, routedTo, sw.ElapsedMilliseconds);

            _ = Task.Run(async () =>
            {
                try { await _logService.LogRequestAsync(entry); }
                catch (Exception ex) { _logger.LogError(ex, "Background log write failed"); }
            });
        }
    }

    internal static string? ExtractApiKey(HttpRequest request)
    {
        string? key = null;

        // X-Api-Key header
        if (request.Headers.TryGetValue("X-Api-Key", out var headerKey) && !string.IsNullOrEmpty(headerKey))
            key = headerKey.ToString();
        // Authorization: Bearer <key>
        else if (request.Headers.TryGetValue("Authorization", out var auth) && !string.IsNullOrEmpty(auth))
        {
            var authStr = auth.ToString();
            if (authStr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                key = authStr["Bearer ".Length..].Trim();
        }
        // ?api_key= query parameter
        else if (request.Query.TryGetValue("api_key", out var queryKey) && !string.IsNullOrEmpty(queryKey))
            key = queryKey.ToString();

        // Truncate to avoid storing full secrets; first 8 chars are sufficient for identification
        return key is { Length: > 8 } ? key[..8] + "…" : key;
    }

    internal static string? ExtractClientIp(HttpContext context)
    {
        // Check X-Forwarded-For first (set by reverse proxies/load balancers)
        if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor) &&
            !string.IsNullOrEmpty(forwardedFor))
        {
            // X-Forwarded-For may contain a comma-separated list; the first entry is the original client
            var firstIp = forwardedFor.ToString().Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(firstIp))
                return firstIp;
        }

        // Fall back to X-Real-IP (used by nginx and similar proxies)
        if (context.Request.Headers.TryGetValue("X-Real-IP", out var realIp) &&
            !string.IsNullOrEmpty(realIp))
            return realIp.ToString();

        // Last resort: direct connection remote IP
        return context.Connection.RemoteIpAddress?.ToString();
    }
}
