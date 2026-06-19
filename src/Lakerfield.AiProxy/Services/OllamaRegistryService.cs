using Lakerfield.AiProxy.Models;
using Microsoft.Extensions.Options;

namespace Lakerfield.AiProxy.Services;

public class OllamaRegistryService
{
    private readonly List<OllamaInstance> _instances = new();
    private readonly ILogger<OllamaRegistryService> _logger;
    private readonly object _lock = new();

    public OllamaRegistryService(IOptions<AiProxyOptions> options, ILogger<OllamaRegistryService> logger)
    {
        _logger = logger;
        foreach (var cfg in options.Value.OllamaInstances)
        {
            var instance = new OllamaInstance
            {
                Name = cfg.Name,
                BaseUrl = cfg.BaseUrl,
                Models = cfg.Models.ToList(),
                ConfiguredModels = cfg.Models.ToList(),
                EnabledModels = new HashSet<string>(cfg.Models, StringComparer.OrdinalIgnoreCase),
                AllSeenModels = new HashSet<string>(cfg.Models, StringComparer.OrdinalIgnoreCase),
                IsHealthy = true,
            };
            _instances.Add(instance);
        }
        _logger.LogInformation("OllamaRegistryService initialized with {Count} instances", _instances.Count);
    }

    public IReadOnlyList<OllamaInstance> GetAllInstances()
    {
        lock (_lock) return _instances.ToList();
    }

    public IReadOnlyList<OllamaInstance> GetHealthyInstances()
    {
        lock (_lock) return _instances.Where(i => i.IsHealthy).ToList();
    }

    public IReadOnlyList<OllamaInstance> GetInstancesForModel(string model)
    {
        lock (_lock)
            return _instances
                .Where(i => i.IsHealthy && i.Models.Contains(model, StringComparer.OrdinalIgnoreCase) && i.EnabledModels.Contains(model))
                .ToList();
    }

    public void MarkHealthy(string name, bool healthy)
    {
        lock (_lock)
        {
            var instance = _instances.FirstOrDefault(i => i.Name == name);
            if (instance != null)
            {
                instance.IsHealthy = healthy;
                instance.LastHealthCheck = DateTime.UtcNow;
            }
        }
    }

    public void IncrementConnections(string name)
    {
        lock (_lock)
        {
            var instance = _instances.FirstOrDefault(i => i.Name == name);
            if (instance != null) instance.ActiveConnections++;
        }
    }

    public void DecrementConnections(string name)
    {
        lock (_lock)
        {
            var instance = _instances.FirstOrDefault(i => i.Name == name);
            if (instance != null && instance.ActiveConnections > 0)
                instance.ActiveConnections--;
        }
    }

    public void UpdateModels(string name, List<string> models)
    {
        lock (_lock)
        {
            var instance = _instances.FirstOrDefault(i => i.Name == name);
            if (instance == null) return;

            // If specific models were configured for this instance, use them as a whitelist.
            // Only expose models that the backend reports AND that are in the configured list.
            // When no models are configured, expose everything the backend reports.
            var filteredModels = instance.ConfiguredModels.Count > 0
                ? models.Where(m => instance.ConfiguredModels.Contains(m, StringComparer.OrdinalIgnoreCase))
                : models;
            instance.Models = filteredModels.ToList();

            // Track all models we've ever seen from the backend (within whitelist bounds).
            foreach (var m in filteredModels)
            {
                instance.AllSeenModels.Add(m);
            }

            // Only auto-enable genuinely new models — ones that haven't been seen before.
            // This preserves user-disabled state: if a model was manually disabled it stays
            // disabled even if it disappears from Ollama temporarily and is pulled again later.
            foreach (var m in filteredModels)
            {
                if (!instance.AllSeenModels.Contains(m, StringComparer.OrdinalIgnoreCase))
                {
                    instance.EnabledModels.Add(m);
                    instance.AllSeenModels.Add(m);
                }
            }

            instance.LastHealthCheck = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Toggle whether a model is enabled on a specific instance. Returns the new enabled state.
    /// </summary>
    public bool ToggleInstanceModel(string name, string model)
    {
        lock (_lock)
        {
            var instance = _instances.FirstOrDefault(i => i.Name == name);
            if (instance == null) return false;

            // Model must be present on the instance (configured or dynamically reported) to be toggleable
            if (!instance.Models.Contains(model, StringComparer.OrdinalIgnoreCase))
                return instance.EnabledModels.Contains(model);

            var wasEnabled = instance.EnabledModels.Contains(model);
            if (wasEnabled)
            {
                instance.EnabledModels.Remove(model);
            }
            else
            {
                instance.EnabledModels.Add(model);
            }
            return !wasEnabled;
        }
    }
}
