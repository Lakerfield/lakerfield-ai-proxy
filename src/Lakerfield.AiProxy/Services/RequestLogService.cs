using System.Text.Json;
using Lakerfield.AiProxy.Models;
using Microsoft.Extensions.Options;

namespace Lakerfield.AiProxy.Services;

/// <summary>
/// Writes request log entries to per-day JSONL files.
/// </summary>
public class RequestLogService
{
    private readonly string _logDirectory;
    private readonly ILogger<RequestLogService> _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
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

            var filePath = entry.ErrorMessage != null
                ? Path.Combine(dir, "errors.jsonl")
                : Path.Combine(dir, "requests.jsonl");

            var line = JsonSerializer.Serialize(entry, _jsonOptions);
            await _fileLock.WaitAsync();
            try
            {
                await File.AppendAllTextAsync(filePath, line + Environment.NewLine);
            }
            finally
            {
                _fileLock.Release();
            }
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
