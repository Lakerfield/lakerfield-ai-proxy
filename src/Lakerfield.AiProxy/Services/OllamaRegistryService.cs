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
            _instances.Add(new OllamaInstance
            {
                Name = cfg.Name,
                BaseUrl = cfg.BaseUrl,
                Models = cfg.Models.ToList(),
                ConfiguredModels = cfg.Models.ToList(),
                IsHealthy = true,
            });
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
                .Where(i => i.IsHealthy && i.Models.Contains(model, StringComparer.OrdinalIgnoreCase))
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
            if (instance != null)
            {
                // If specific models were configured for this instance, use them as a whitelist.
                // Only expose models that the backend reports AND that are in the configured list.
                // When no models are configured, expose everything the backend reports.
                instance.Models = instance.ConfiguredModels.Count > 0
                    ? models.Where(m => instance.ConfiguredModels.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList()
                    : models;
                instance.LastHealthCheck = DateTime.UtcNow;
            }
        }
    }
}
