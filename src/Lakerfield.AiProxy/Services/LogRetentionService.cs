using Lakerfield.AiProxy.Models;
using Microsoft.Extensions.Options;

namespace Lakerfield.AiProxy.Services;

/// <summary>
/// Background service that deletes per-day log directories older than LogRetentionDays.
/// Runs once at startup and then every 24 hours.
/// </summary>
public class LogRetentionService : BackgroundService
{
    private readonly ILogger<LogRetentionService> _logger;
    private readonly string _logDirectory;
    private readonly int _retentionDays;

    public LogRetentionService(IOptions<AiProxyOptions> options, ILogger<LogRetentionService> logger)
    {
        _logger = logger;
        _logDirectory = options.Value.LogDirectory;
        _retentionDays = options.Value.LogRetentionDays;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run immediately at startup, then every 24 hours
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanOldLogsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during log retention cleanup");
            }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }

    private Task CleanOldLogsAsync()
    {
        if (_retentionDays <= 0) return Task.CompletedTask;
        if (!Directory.Exists(_logDirectory)) return Task.CompletedTask;

        var cutoff = DateTime.UtcNow.Date.AddDays(-_retentionDays);
        var deleted = 0;

        foreach (var dir in Directory.EnumerateDirectories(_logDirectory))
        {
            var name = Path.GetFileName(dir);
            if (DateTime.TryParseExact(name, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var dirDate))
            {
                if (dirDate < cutoff)
                {
                    try
                    {
                        Directory.Delete(dir, recursive: true);
                        deleted++;
                        _logger.LogInformation("Deleted old log directory: {Dir}", dir);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not delete log directory: {Dir}", dir);
                    }
                }
            }
        }

        if (deleted > 0)
            _logger.LogInformation("Log retention: removed {Count} old log directories", deleted);

        return Task.CompletedTask;
    }
}
