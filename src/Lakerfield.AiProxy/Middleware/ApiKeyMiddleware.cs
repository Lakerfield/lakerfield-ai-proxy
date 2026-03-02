using Lakerfield.AiProxy.Models;
using Microsoft.Extensions.Options;

namespace Lakerfield.AiProxy.Middleware;

/// <summary>
/// Middleware that enforces API key authentication on proxy endpoints when
/// <see cref="AiProxyOptions.ApiKey"/> is configured.
/// Dashboard, health and metrics endpoints are excluded from authentication.
/// </summary>
public class ApiKeyMiddleware
{
    private static readonly HashSet<string> _excludedPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/health",
        "/metrics",
        "/api/metrics",
        "/api/instances",
        "/api/logs",
    };

    private readonly RequestDelegate _next;
    private readonly string _apiKey;

    public ApiKeyMiddleware(RequestDelegate next, IOptions<AiProxyOptions> options)
    {
        _next = next;
        _apiKey = options.Value.ApiKey;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip auth if no API key is configured
        if (string.IsNullOrEmpty(_apiKey))
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "/";

        // Skip auth for excluded paths (dashboard, health, metrics, static files, SignalR)
        if (IsExcluded(path))
        {
            await _next(context);
            return;
        }

        // Check X-Api-Key header first, then Authorization Bearer, then query param
        if (!TryGetProvidedKey(context, out var providedKey) ||
            !string.Equals(providedKey, _apiKey, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":{\"message\":\"Invalid or missing API key\",\"type\":\"authentication_error\",\"code\":\"invalid_api_key\"}}");
            return;
        }

        await _next(context);
    }

    private static bool IsExcluded(string path)
    {
        if (path == "/" || path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase))
            return true;

        // Exclude static file extensions served from wwwroot
        if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var prefix in _excludedPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool TryGetProvidedKey(HttpContext context, out string key)
    {
        // X-Api-Key header
        if (context.Request.Headers.TryGetValue("X-Api-Key", out var headerKey) && !string.IsNullOrEmpty(headerKey))
        {
            key = headerKey.ToString();
            return true;
        }

        // Authorization: Bearer <key>
        if (context.Request.Headers.TryGetValue("Authorization", out var auth) && !string.IsNullOrEmpty(auth))
        {
            var authStr = auth.ToString();
            if (authStr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                key = authStr["Bearer ".Length..].Trim();
                return true;
            }
        }

        // ?api_key= query parameter
        if (context.Request.Query.TryGetValue("api_key", out var queryKey) && !string.IsNullOrEmpty(queryKey))
        {
            key = queryKey.ToString();
            return true;
        }

        key = string.Empty;
        return false;
    }
}
