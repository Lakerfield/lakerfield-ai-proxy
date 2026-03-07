using System.Text.Json;
using Lakerfield.AiProxy.Models;
using Microsoft.Extensions.Options;

namespace Lakerfield.AiProxy.Services;

/// <summary>
/// Writes each request log entry to its own JSON file at
/// <c>{LogDirectory}/{yyyy-MM-dd}/{requestId}.json</c>.
/// </summary>
public class RequestLogService
{
    private readonly string _logDirectory;
    private readonly ILogger<RequestLogService> _logger;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public RequestLogService(IOptions<AiProxyOptions> options, ILogger<RequestLogService> logger)
    {
        _logDirectory = options.Value.LogDirectory;
        _logger = logger;
        EnsureDirectoryExists(GetTodayDirectory());
    }

    public async Task LogRequestAsync(RequestLogEntry entry)
    {
        try
        {
            var dir = GetTodayDirectory();
            EnsureDirectoryExists(dir);

            var filePath = Path.Combine(dir, $"{entry.RequestId}.json");
            var json = JsonSerializer.Serialize(entry, _jsonOptions);
            await File.WriteAllTextAsync(filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write request log entry");
        }
    }

    private string GetTodayDirectory()
    {
        var dateDir = DateTime.UtcNow.ToString("yyyy-MM-dd");
        return Path.Combine(_logDirectory, dateDir);
    }

    private static void EnsureDirectoryExists(string path)
    {
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);
    }
}
