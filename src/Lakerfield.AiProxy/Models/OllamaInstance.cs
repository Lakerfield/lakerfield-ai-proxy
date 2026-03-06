namespace Lakerfield.AiProxy.Models;

public class OllamaInstance
{
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public List<string> Models { get; set; } = new();
    /// <summary>
    /// The models explicitly configured for this instance. When non-empty, acts as a whitelist:
    /// only models that appear in both this list and the backend's reported models are exposed.
    /// </summary>
    public List<string> ConfiguredModels { get; set; } = new();
    public bool IsHealthy { get; set; } = true;
    public int ActiveConnections { get; set; }
    public DateTime LastHealthCheck { get; set; } = DateTime.UtcNow;
}
