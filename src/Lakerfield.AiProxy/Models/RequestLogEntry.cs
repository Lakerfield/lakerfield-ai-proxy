namespace Lakerfield.AiProxy.Models;

public class RequestLogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string RequestId { get; set; } = Guid.NewGuid().ToString();
    public string Endpoint { get; set; } = string.Empty;
    public string? Model { get; set; }
    public string? RoutedTo { get; set; }
    public long DurationMs { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int StatusCode { get; set; }
    public bool Streaming { get; set; }
    public string? RequestBody { get; set; }
    public string? ErrorMessage { get; set; }
}
