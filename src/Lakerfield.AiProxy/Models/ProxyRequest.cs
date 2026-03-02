namespace Lakerfield.AiProxy.Models;

public class ProxyRequest
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString();
    public string Endpoint { get; set; } = string.Empty;
    public string? Model { get; set; }
    public string? RoutedTo { get; set; }
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public bool IsStreaming { get; set; }
}
