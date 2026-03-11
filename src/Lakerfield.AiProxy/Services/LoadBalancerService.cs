using Lakerfield.AiProxy.Models;

namespace Lakerfield.AiProxy.Services;

public class LoadBalancerService
{
    private readonly OllamaRegistryService _registry;
    private readonly ILogger<LoadBalancerService> _logger;
    private int _roundRobinIndex = 0;

    public LoadBalancerService(OllamaRegistryService registry, ILogger<LoadBalancerService> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// Select the best instance for a given model using least-connections with round-robin fallback.
    /// </summary>
    public OllamaInstance? SelectInstance(string? model)
    {
        IReadOnlyList<OllamaInstance> candidates;

        if (!string.IsNullOrEmpty(model))
        {
            candidates = _registry.GetInstancesForModel(model);
            if (candidates.Count == 0)
            {
                _logger.LogWarning("No healthy instances found for model '{Model}'", model);
                return null;
            }
        }
        else
        {
            candidates = _registry.GetHealthyInstances();
        }

        if (candidates.Count == 0)
        {
            _logger.LogError("No healthy Ollama instances available");
            return null;
        }

        // Least-connections strategy
        var best = candidates.OrderBy(i => i.ActiveConnections).First();
        _logger.LogDebug("Selected instance '{Name}' for model '{Model}' (active connections: {Connections})",
            best.Name, model, best.ActiveConnections);
        return best;
    }

    /// <summary>
    /// Select next instance using round-robin (excluding the given instance name for retry).
    /// </summary>
    public OllamaInstance? SelectFallbackInstance(string? model, string excludeName)
    {
        IReadOnlyList<OllamaInstance> candidates;

        if (!string.IsNullOrEmpty(model))
        {
            candidates = _registry.GetInstancesForModel(model);
            if (candidates.Count == 0)
                return null;
        }
        else
        {
            candidates = _registry.GetHealthyInstances();
        }

        var fallbacks = candidates.Where(i => i.Name != excludeName).ToList();
        if (fallbacks.Count == 0) return null;

        // Round-robin fallback
        var idx = (uint)Interlocked.Increment(ref _roundRobinIndex) % (uint)fallbacks.Count;
        return fallbacks[(int)idx];
    }
}
