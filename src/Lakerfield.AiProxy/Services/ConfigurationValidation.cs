using Lakerfield.AiProxy.Models;

namespace Lakerfield.AiProxy.Services;

/// <summary>
/// Validates <see cref="AiProxyOptions"/> at startup and logs warnings for detected issues.
/// </summary>
public static class ConfigurationValidation
{
    public static void Validate(AiProxyOptions options, ILogger logger)
    {
        if (options.OllamaInstances.Count == 0)
        {
            logger.LogWarning("No Ollama instances configured. Add at least one entry under AiProxy:OllamaInstances in appsettings.json.");
        }

        foreach (var instance in options.OllamaInstances)
        {
            if (string.IsNullOrWhiteSpace(instance.Name))
                logger.LogWarning("An Ollama instance has an empty Name. Please set a unique name.");

            if (string.IsNullOrWhiteSpace(instance.BaseUrl))
            {
                logger.LogWarning("Ollama instance '{Name}' has an empty BaseUrl.", instance.Name);
            }
            else if (!Uri.TryCreate(instance.BaseUrl, UriKind.Absolute, out var uri) ||
                     (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                logger.LogWarning(
                    "Ollama instance '{Name}' has an invalid BaseUrl '{BaseUrl}'. Expected format: http://host:port",
                    instance.Name, instance.BaseUrl);
            }

            if (instance.Models.Count == 0)
                logger.LogWarning("Ollama instance '{Name}' has no models configured. It will only receive requests that don't specify a model.", instance.Name);
        }

        if (options.LogRetentionDays < 0)
            logger.LogWarning("LogRetentionDays is negative ({Value}). It will be treated as 0 (keep forever).", options.LogRetentionDays);

        if (options.RateLimitRequestsPerMinute < 0)
            logger.LogWarning("RateLimitRequestsPerMinute is negative ({Value}). Rate limiting will be disabled.", options.RateLimitRequestsPerMinute);
    }
}
