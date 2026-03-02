using Lakerfield.AiProxy.Hubs;
using Lakerfield.AiProxy.Middleware;
using Lakerfield.AiProxy.Models;
using Lakerfield.AiProxy.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddHostedService<OllamaHealthCheckService>();

// ASP.NET Core
builder.Services.AddControllers();
builder.Services.AddSignalR();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseRouting();
app.MapControllers();
app.MapHub<RequestMonitorHub>("/hubs/monitor");

app.Run();
