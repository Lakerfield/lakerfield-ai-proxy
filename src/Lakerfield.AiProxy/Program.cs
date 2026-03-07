using System.Threading.RateLimiting;
using Lakerfield.AiProxy.Hubs;
using Lakerfield.AiProxy.Middleware;
using Lakerfield.AiProxy.Models;
using Lakerfield.AiProxy.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Kestrel: allow long-running AI inference requests without timing out
builder.WebHost.ConfigureKestrel(options =>
{
    // Disable data-rate checks so Kestrel doesn't drop connections while Ollama is generating
    options.Limits.MinResponseDataRate = null;
    options.Limits.MinRequestBodyDataRate = null;
    // Keep HTTP/1.1 connections alive for the duration of long inference requests
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
});

// Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/app-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

// Configuration
builder.Services.Configure<AiProxyOptions>(builder.Configuration.GetSection(AiProxyOptions.SectionName));

// HTTP clients
builder.Services.AddHttpClient("proxy", client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
});
builder.Services.AddHttpClient("health-check", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

// Services
builder.Services.AddSingleton<OllamaRegistryService>();
builder.Services.AddSingleton<LoadBalancerService>();
builder.Services.AddSingleton<RequestLogService>();
builder.Services.AddSingleton<RequestMonitorService>();
builder.Services.AddSingleton<MetricsService>();
builder.Services.AddSingleton<ActiveRequestStore>();
builder.Services.AddHostedService<OllamaHealthCheckService>();
builder.Services.AddHostedService<LogRetentionService>();

// ASP.NET Core
builder.Services.AddControllers();
builder.Services.AddSignalR();

// Health checks
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy("Proxy is running"));

// CORS
// Read configuration once up front for service-registration-time decisions (CORS, rate limiting).
// The same section is also bound via Configure<AiProxyOptions> above so controllers and
// services receive the canonical IOptions<AiProxyOptions> from DI.
var aiProxyConfig = builder.Configuration.GetSection(AiProxyOptions.SectionName).Get<AiProxyOptions>() ?? new AiProxyOptions();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (aiProxyConfig.CorsAllowedOrigins.Count == 0 || aiProxyConfig.CorsAllowedOrigins.Contains("*"))
        {
            // AllowAnyOrigin() is incompatible with AllowCredentials() which SignalR requires.
            // Use SetIsOriginAllowed instead so that all origins are accepted while still
            // supporting credentials for SignalR's SSE / long-polling transports.
            policy.SetIsOriginAllowed(_ => true)
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
        }
        else
        {
            policy.WithOrigins(aiProxyConfig.CorsAllowedOrigins.ToArray())
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
        }
    });
});

// Rate limiting (per IP, fixed window)
if (aiProxyConfig.RateLimitRequestsPerMinute > 0)
{
    builder.Services.AddRateLimiter(options =>
    {
        options.AddPolicy("proxy", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = aiProxyConfig.RateLimitRequestsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0,
                }));
        options.OnRejected = async (ctx, _) =>
        {
            ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            ctx.HttpContext.Response.Headers["Retry-After"] = "60";
            ctx.HttpContext.Response.ContentType = "application/json";
            await ctx.HttpContext.Response.WriteAsync(
                "{\"error\":{\"message\":\"Rate limit exceeded\",\"type\":\"rate_limit_error\",\"code\":\"rate_limit_exceeded\"}}");
        };
    });
}

var app = builder.Build();

// Configuration validation
ConfigurationValidation.Validate(aiProxyConfig, app.Logger);

app.UseCors();

if (aiProxyConfig.RateLimitRequestsPerMinute > 0)
    app.UseRateLimiter();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseMiddleware<ApiKeyMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseRouting();
app.MapControllers();
app.MapHub<RequestMonitorHub>("/hubs/monitor");
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (ctx, report) =>
    {
        ctx.Response.ContentType = "application/json";
        var status = report.Status == HealthStatus.Healthy ? "healthy" : "unhealthy";
        await ctx.Response.WriteAsync($"{{\"status\":\"{status}\"}}");
    }
});

app.Run();
