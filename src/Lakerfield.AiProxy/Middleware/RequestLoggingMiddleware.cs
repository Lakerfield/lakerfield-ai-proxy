using System.Diagnostics;
using System.Text;
using Lakerfield.AiProxy.Models;
using Lakerfield.AiProxy.Services;
using Microsoft.Extensions.Options;

namespace Lakerfield.AiProxy.Middleware;

public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RequestLogService _logService;
    private readonly MetricsService _metrics;
    private readonly ILogger<RequestLoggingMiddleware> _logger;
    private readonly int _maxBodyBytes;

    public RequestLoggingMiddleware(RequestDelegate next, RequestLogService logService, MetricsService metrics, ILogger<RequestLoggingMiddleware> logger, IOptions<AiProxyOptions> options)
    {
        _next = next;
        _logService = logService;
        _metrics = metrics;
        _logger = logger;
        _maxBodyBytes = options.Value.LogMaxBodyBytes;
    }

    private static bool IsExcludedPath(string path) =>
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
        string? requestBodySample = null;
        int? requestBodySize = null;
        if (_maxBodyBytes > 0 &&
            (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")))
        {
            context.Request.EnableBuffering();
            var buffer = new byte[_maxBodyBytes];
            var read = await context.Request.Body.ReadAsync(buffer.AsMemory(0, _maxBodyBytes));
            if (read > 0)
                requestBodySample = Encoding.UTF8.GetString(buffer, 0, read);
            context.Request.Body.Seek(0, SeekOrigin.Begin);
            // Use declared Content-Length as authoritative size; fall back to bytes actually read
            requestBodySize = context.Request.ContentLength.HasValue
                ? (int)context.Request.ContentLength.Value
                : read > 0 ? read : null;
        }

        var apiKey = ExtractApiKey(context.Request);

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
                Streaming = context.Items["Streaming"] is true,
                InputTokens = context.Items["InputTokens"] as int?,
                OutputTokens = context.Items["OutputTokens"] as int?,
                RequestBody = requestBodySample,
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

    private static string? ExtractApiKey(HttpRequest request)
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
}
