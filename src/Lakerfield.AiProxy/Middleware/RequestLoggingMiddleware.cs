using System.Diagnostics;
using Lakerfield.AiProxy.Models;
using Lakerfield.AiProxy.Services;

namespace Lakerfield.AiProxy.Middleware;

public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RequestLogService _logService;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(RequestDelegate next, RequestLogService logService, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logService = logService;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = Guid.NewGuid().ToString();
        context.Items["RequestId"] = requestId;

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
            var entry = new RequestLogEntry
            {
                RequestId = requestId,
                Endpoint = context.Request.Path.Value ?? string.Empty,
                StatusCode = context.Response.StatusCode,
                DurationMs = sw.ElapsedMilliseconds,
                Model = context.Items["Model"] as string,
                RoutedTo = context.Items["RoutedTo"] as string,
                Streaming = context.Items["Streaming"] is true,
                ErrorMessage = errorMessage,
            };

            _ = Task.Run(async () =>
            {
                try { await _logService.LogRequestAsync(entry); }
                catch (Exception ex) { _logger.LogError(ex, "Background log write failed"); }
            });
        }
    }
}
