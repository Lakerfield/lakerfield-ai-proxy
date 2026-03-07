using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lakerfield.AiProxy.Models;
using Lakerfield.AiProxy.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Lakerfield.AiProxy.Controllers;

[ApiController]
[Route("api")]
public class LogsController : ControllerBase
{
    private static readonly Regex DatePattern = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);

    private readonly MetricsService _metrics;
    private readonly OllamaRegistryService _registry;
    private readonly ActiveRequestStore _activeRequestStore;
    private readonly string _logDirectory;
    private readonly ILogger<LogsController> _logger;
    private static readonly JsonSerializerOptions _readOptions = new() { PropertyNameCaseInsensitive = true };

    public LogsController(MetricsService metrics, OllamaRegistryService registry, ActiveRequestStore activeRequestStore, IOptions<AiProxyOptions> options, ILogger<LogsController> logger)
    {
        _metrics = metrics;
        _registry = registry;
        _activeRequestStore = activeRequestStore;
        _logDirectory = options.Value.LogDirectory;
        _logger = logger;
    }

    // GET /api/metrics
    [HttpGet("metrics")]
    public IActionResult GetMetrics()
    {
        return Ok(_metrics.GetSummary());
    }

    // GET /metrics  — Prometheus text format
    [HttpGet("/metrics")]
    public IActionResult GetPrometheusMetrics()
    {
        var summary = _metrics.GetSummary();
        var instances = _registry.GetAllInstances();

        var sb = new StringBuilder();

        sb.AppendLine("# HELP aiproxy_requests_total Total number of requests in the last 60 seconds");
        sb.AppendLine("# TYPE aiproxy_requests_total gauge");
        sb.AppendLine($"aiproxy_requests_total {summary.RequestsLast60Seconds}");

        sb.AppendLine("# HELP aiproxy_requests_per_minute Requests per minute (60-second window)");
        sb.AppendLine("# TYPE aiproxy_requests_per_minute gauge");
        sb.AppendLine($"aiproxy_requests_per_minute {summary.RequestsPerMinute}");

        sb.AppendLine("# HELP aiproxy_avg_latency_ms Average request latency in milliseconds");
        sb.AppendLine("# TYPE aiproxy_avg_latency_ms gauge");
        sb.AppendLine($"aiproxy_avg_latency_ms {summary.AvgLatencyMs:F2}");

        sb.AppendLine("# HELP aiproxy_model_requests_total Cumulative requests per model");
        sb.AppendLine("# TYPE aiproxy_model_requests_total counter");
        foreach (var (model, count) in summary.ModelCounts)
            sb.AppendLine($"aiproxy_model_requests_total{{model=\"{EscapeLabel(model)}\"}} {count}");

        sb.AppendLine("# HELP aiproxy_instance_requests_total Cumulative requests per instance");
        sb.AppendLine("# TYPE aiproxy_instance_requests_total counter");
        foreach (var (instance, count) in summary.InstanceCounts)
            sb.AppendLine($"aiproxy_instance_requests_total{{instance=\"{EscapeLabel(instance)}\"}} {count}");

        sb.AppendLine("# HELP aiproxy_instance_healthy Whether the Ollama instance is healthy (1=healthy, 0=unhealthy)");
        sb.AppendLine("# TYPE aiproxy_instance_healthy gauge");
        foreach (var inst in instances)
            sb.AppendLine($"aiproxy_instance_healthy{{instance=\"{EscapeLabel(inst.Name)}\"}} {(inst.IsHealthy ? 1 : 0)}");

        sb.AppendLine("# HELP aiproxy_instance_active_connections Active connections per instance");
        sb.AppendLine("# TYPE aiproxy_instance_active_connections gauge");
        foreach (var inst in instances)
            sb.AppendLine($"aiproxy_instance_active_connections{{instance=\"{EscapeLabel(inst.Name)}\"}} {inst.ActiveConnections}");

        return Content(sb.ToString(), "text/plain; version=0.0.4; charset=utf-8");
    }

    // GET /api/instances
    [HttpGet("instances")]
    public IActionResult GetInstances()
    {
        var instances = _registry.GetAllInstances().Select(i => new
        {
            name = i.Name,
            baseUrl = i.BaseUrl,
            isHealthy = i.IsHealthy,
            activeConnections = i.ActiveConnections,
            models = i.Models,
            lastHealthCheck = i.LastHealthCheck,
        });
        return Ok(instances);
    }

    // GET /api/logs?date=yyyy-MM-dd&type=requests|errors&limit=100
    [HttpGet("logs")]
    public async Task<IActionResult> GetLogs(
        [FromQuery] string? date = null,
        [FromQuery] string type = "requests",
        [FromQuery] int limit = 100)
    {
        var dateStr = date ?? DateTime.UtcNow.ToString("yyyy-MM-dd");

        if (!DatePattern.IsMatch(dateStr))
            return BadRequest("Invalid date format. Use yyyy-MM-dd.");

        if (type != "requests" && type != "errors")
            return BadRequest("Invalid type. Use 'requests' or 'errors'.");

        limit = Math.Clamp(limit, 1, 1000);

        var dir = Path.Combine(_logDirectory, dateStr);
        if (!Directory.Exists(dir))
            return Ok(Array.Empty<RequestLogEntry>());

        // Sort files by last-write time descending so the most recent entries come first.
        // For "requests": cap file iteration upfront since every file is a candidate.
        // For "errors": scan all files because we must filter by ErrorMessage (errors are rare).
        var files = new DirectoryInfo(dir)
            .GetFiles("*.json")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .AsEnumerable();
        var candidates = type == "requests" ? files.Take(limit) : files;

        var entries = new List<RequestLogEntry>();

        foreach (var fi in candidates)
        {
            try
            {
                var json = await System.IO.File.ReadAllTextAsync(fi.FullName);
                var entry = JsonSerializer.Deserialize<RequestLogEntry>(json, _readOptions);
                if (entry == null) continue;

                // For "errors" filter: only include entries that have an error message
                if (type == "errors" && entry.ErrorMessage == null) continue;

                // Strip body content from list responses — full bodies are available via GetLogBody
                entry.RequestBody = null;
                entry.ResponseBody = null;

                entries.Add(entry);
                if (entries.Count >= limit) break;
            }
            catch { /* skip malformed files */ }
        }

        return Ok(entries);
    }

    // GET /api/logs/body/{requestId}?type=request|response
    [HttpGet("logs/body/{requestId}")]
    public async Task<IActionResult> GetLogBody(string requestId, [FromQuery] string type = "request")
    {
        if (type != "request" && type != "response")
            return BadRequest("Invalid type. Use 'request' or 'response'.");

        // For request bodies: check the in-memory store first so that in-flight (streaming)
        // requests can show their request body before the log file entry is written.
        if (type == "request")
        {
            var active = _activeRequestStore.TryGet(requestId);
            if (active != null)
                return Ok(new { body = active.RequestBody, headers = active.RequestHeaders });
        }

        // Look up the individual request file (today and yesterday)
        foreach (var daysAgo in new[] { 0, 1 })
        {
            var dateStr = DateTime.UtcNow.AddDays(-daysAgo).ToString("yyyy-MM-dd");
            var filePath = Path.Combine(_logDirectory, dateStr, $"{requestId}.json");
            if (!System.IO.File.Exists(filePath)) continue;

            try
            {
                var json = await System.IO.File.ReadAllTextAsync(filePath);
                var entry = JsonSerializer.Deserialize<RequestLogEntry>(json, _readOptions);
                if (entry != null)
                {
                    var body = type == "response" ? entry.ResponseBody : entry.RequestBody;
                    var headers = type == "response" ? entry.ResponseHeaders : entry.RequestHeaders;
                    return Ok(new { body, headers });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read log file {File}", filePath);
            }
        }

        return NotFound(new { body = (string?)null, headers = (Dictionary<string, string>?)null });
    }

    private static string EscapeLabel(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}
