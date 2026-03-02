namespace Lakerfield.AiProxy.Models;

public class OllamaInstance
{
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public List<string> Models { get; set; } = new();
    public bool IsHealthy { get; set; } = true;
    public int ActiveConnections { get; set; }
    public DateTime LastHealthCheck { get; set; } = DateTime.UtcNow;
}
