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

        var filePath = Path.Combine(_logDirectory, dateStr, $"{type}.jsonl");
        if (!System.IO.File.Exists(filePath))
            return Ok(Array.Empty<RequestLogEntry>());

        var entries = new List<RequestLogEntry>();
        try
        {
            var lines = await System.IO.File.ReadAllLinesAsync(filePath);
            // Return last `limit` entries (most recent)
            foreach (var line in lines.TakeLast(limit))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<RequestLogEntry>(line, _readOptions);
                    if (entry != null) entries.Add(entry);
                }
                catch { /* skip malformed lines */ }
            }
        }
        catch (Exception)
        {
            return StatusCode(500, "Failed to read log file");
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

        // Search today's and yesterday's log files
        foreach (var daysAgo in new[] { 0, 1 })
        {
            var dateStr = DateTime.UtcNow.AddDays(-daysAgo).ToString("yyyy-MM-dd");
            foreach (var fileName in new[] { "requests.jsonl", "errors.jsonl" })
            {
                var filePath = Path.Combine(_logDirectory, dateStr, fileName);
                if (!System.IO.File.Exists(filePath)) continue;

                try
                {
                    await foreach (var line in System.IO.File.ReadLinesAsync(filePath))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            var entry = JsonSerializer.Deserialize<RequestLogEntry>(line, _readOptions);
                            if (entry?.RequestId == requestId)
                            {
                                var body = type == "response" ? entry.ResponseBody : entry.RequestBody;
                                var headers = type == "response" ? entry.ResponseHeaders : entry.RequestHeaders;
                                return Ok(new { body, headers });
                            }
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogDebug(ex, "Skipping malformed log line in {File}", filePath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read log file {File}", filePath);
                }
            }
        }

        return NotFound(new { body = (string?)null, headers = (Dictionary<string, string>?)null });
    }

    private static string EscapeLabel(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}
