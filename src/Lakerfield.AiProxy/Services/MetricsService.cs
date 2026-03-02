using System.Collections.Concurrent;

namespace Lakerfield.AiProxy.Services;

/// <summary>
/// In-memory metrics tracker for requests/min, avg latency, and per-model/instance counts.
/// </summary>
public class MetricsService
{
    private readonly object _lock = new();

    // Sliding window entries: (timestamp ticks, durationMs)
    private readonly Queue<(long Ticks, long DurationMs)> _window = new();

    // Per-model cumulative request counts
    private readonly Dictionary<string, int> _modelCounts = new(StringComparer.OrdinalIgnoreCase);

    // Per-instance cumulative request counts
    private readonly Dictionary<string, int> _instanceCounts = new(StringComparer.OrdinalIgnoreCase);

    // Time-series bucket: second-resolution counts for the last 60 seconds (ring buffer)
    private readonly int[] _secondBuckets = new int[60];
    private long _lastBucketSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public void RecordRequest(string? model, string? instanceName, long durationMs)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var nowTicks = now.Ticks;
            _window.Enqueue((nowTicks, durationMs));

            // Purge entries older than 60 seconds
            var cutoff = now.AddSeconds(-60).Ticks;
            while (_window.Count > 0 && _window.Peek().Ticks < cutoff)
                _window.Dequeue();

            // Update per-model count
            if (model != null)
                _modelCounts[model] = (_modelCounts.GetValueOrDefault(model)) + 1;

            // Update per-instance count
            if (instanceName != null)
                _instanceCounts[instanceName] = (_instanceCounts.GetValueOrDefault(instanceName)) + 1;

            // Update time-series second bucket
            var currentSecond = new DateTimeOffset(now).ToUnixTimeSeconds();
            var delta = currentSecond - _lastBucketSecond;
            if (delta > 0)
            {
                // Zero out buckets that have rolled over
                for (long i = 1; i <= Math.Min(delta, 60); i++)
                    _secondBuckets[(_lastBucketSecond + i) % 60] = 0;
                _lastBucketSecond = currentSecond;
            }
            _secondBuckets[currentSecond % 60]++;
        }
    }

    public MetricsSummary GetSummary()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var cutoff60 = now.AddSeconds(-60).Ticks;
            var cutoff1 = now.AddSeconds(-1).Ticks;

            int count60 = 0;
            int count1 = 0;
            long totalDuration = 0;
            int durationCount = 0;

            foreach (var (ticks, duration) in _window)
            {
                if (ticks >= cutoff60)
                {
                    count60++;
                    totalDuration += duration;
                    durationCount++;
                }
                if (ticks >= cutoff1)
                    count1++;
            }

            // Build 60-second time series (bucket per second, ordered oldest→newest)
            var currentSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var timeSeries = new int[60];
            for (int i = 0; i < 60; i++)
            {
                var sec = currentSecond - 59 + i;
                if (sec > _lastBucketSecond || _lastBucketSecond - sec >= 60)
                    timeSeries[i] = 0;
                else
                    timeSeries[i] = _secondBuckets[sec % 60];
            }

            return new MetricsSummary
            {
                RequestsLast60Seconds = count60,
                RequestsPerMinute = count60,
                RequestsLastSecond = count1,
                AvgLatencyMs = durationCount > 0 ? (double)totalDuration / durationCount : 0,
                ModelCounts = new Dictionary<string, int>(_modelCounts),
                InstanceCounts = new Dictionary<string, int>(_instanceCounts),
                RequestsPerSecondSeries = timeSeries,
            };
        }
    }
}

public class MetricsSummary
{
    public int RequestsLast60Seconds { get; set; }
    public int RequestsPerMinute { get; set; }
    public int RequestsLastSecond { get; set; }
    public double AvgLatencyMs { get; set; }
    public Dictionary<string, int> ModelCounts { get; set; } = new();
    public Dictionary<string, int> InstanceCounts { get; set; } = new();
    /// <summary>60 entries, one per second, ordered oldest to newest.</summary>
    public int[] RequestsPerSecondSeries { get; set; } = new int[60];
}
