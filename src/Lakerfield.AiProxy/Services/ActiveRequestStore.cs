using System.Collections.Concurrent;

namespace Lakerfield.AiProxy.Services;

/// <summary>
/// In-memory store for request body and headers of currently in-flight requests.
/// Allows the body popup to show request details before the log file entry is written
/// (which only happens after the full response has been received).
/// </summary>
public class ActiveRequestStore
{
    private readonly ConcurrentDictionary<string, ActiveRequestEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>Adds or replaces the entry for an active request.</summary>
    public void Add(string requestId, string? requestBody, Dictionary<string, string>? requestHeaders)
    {
        _entries[requestId] = new ActiveRequestEntry(requestBody, requestHeaders);
    }

    /// <summary>Removes the entry when the request completes.</summary>
    public void Remove(string requestId)
    {
        _entries.TryRemove(requestId, out _);
    }

    /// <summary>Returns the stored entry, or <c>null</c> if not found.</summary>
    public ActiveRequestEntry? TryGet(string requestId)
    {
        return _entries.TryGetValue(requestId, out var entry) ? entry : null;
    }
}

public record ActiveRequestEntry(string? RequestBody, Dictionary<string, string>? RequestHeaders);
