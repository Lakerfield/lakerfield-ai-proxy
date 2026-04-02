namespace Lakerfield.AiProxy.Models;

public class RequestLogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string RequestId { get; set; } = Guid.NewGuid().ToString();
    public string Endpoint { get; set; } = string.Empty;
    public string? Model { get; set; }
    public string? RoutedTo { get; set; }
    public string? ApiKey { get; set; }
    public string? ClientIp { get; set; }
    public long DurationMs { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int StatusCode { get; set; }
    public bool Streaming { get; set; }
    public int? RequestBodySize { get; set; }
    public int? ResponseBodySize { get; set; }
    public string? RequestBody { get; set; }
    public string? ResponseBody { get; set; }
    public Dictionary<string, string>? RequestHeaders { get; set; }
    public Dictionary<string, string>? ResponseHeaders { get; set; }
    public string? ErrorMessage { get; set; }
}
