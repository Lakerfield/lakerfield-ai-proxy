using System.Text.Json;
using Lakerfield.AiProxy.Models;

namespace Lakerfield.AiProxy.Services;

public class OllamaHealthCheckService : BackgroundService
{
    private readonly OllamaRegistryService _registry;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OllamaHealthCheckService> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(30);

    public OllamaHealthCheckService(
        OllamaRegistryService registry,
        IHttpClientFactory httpClientFactory,
        ILogger<OllamaHealthCheckService> logger)
    {
        _registry = registry;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Ollama health check service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckAllInstancesAsync(stoppingToken);
            try { await Task.Delay(_checkInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task CheckAllInstancesAsync(CancellationToken cancellationToken)
    {
        var instances = _registry.GetAllInstances();
        var tasks = instances.Select(instance => CheckInstanceAsync(instance, cancellationToken));
        await Task.WhenAll(tasks);
    }

    private async Task CheckInstanceAsync(OllamaInstance instance, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("health-check");
            var response = await client.GetAsync($"{instance.BaseUrl.TrimEnd('/')}/api/tags", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var models = ParseModelsFromTagsResponse(content);
                if (models.Count > 0)
                    _registry.UpdateModels(instance.Name, models);

                if (!instance.IsHealthy)
                    _logger.LogInformation("Instance '{Name}' is back online", instance.Name);

                _registry.MarkHealthy(instance.Name, true);
            }
            else
            {
                _logger.LogWarning("Instance '{Name}' health check failed with status {Status}", instance.Name, response.StatusCode);
                _registry.MarkHealthy(instance.Name, false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Instance '{Name}' is unreachable", instance.Name);
            _registry.MarkHealthy(instance.Name, false);
        }
    }

    private static List<string> ParseModelsFromTagsResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("models", out var modelsEl))
            {
                return modelsEl.EnumerateArray()
                    .Where(m => m.TryGetProperty("name", out _))
                    .Select(m => m.GetProperty("name").GetString() ?? string.Empty)
                    .Where(name => !string.IsNullOrEmpty(name))
                    .ToList();
            }
        }
        catch { /* ignore parse errors */ }
        return new List<string>();
    }
}
