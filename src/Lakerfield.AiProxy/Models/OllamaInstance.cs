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
    /// <summary>
    /// Subset of ConfiguredModels that are currently enabled for routing.
    /// When a model is disabled here, it stays in ConfiguredModels but won't be routed to.
    /// </summary>
    public HashSet<string> EnabledModels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// All models that have been reported by the backend and are within
    /// ConfiguredModels (if a whitelist is set). Used so that models which
    /// were manually disabled stay disabled even if they briefly disappear
    /// from the Ollama model list and reappear later.
    /// </summary>
    public HashSet<string> AllSeenModels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsHealthy { get; set; } = true;
    public int ActiveConnections { get; set; }
    public DateTime LastHealthCheck { get; set; } = DateTime.UtcNow;
}
