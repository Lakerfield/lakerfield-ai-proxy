namespace Lakerfield.AiProxy.Models;

public class AiProxyOptions
{
    public const string SectionName = "AiProxy";

    public string LogDirectory { get; set; } = "./logs";
    public string LoadBalancingStrategy { get; set; } = "LeastConnections";
    /// <summary>Maximum number of bytes captured from the request body for logging. Default 10 KB.</summary>
    public int LogMaxBodyBytes { get; set; } = 10_240;
    /// <summary>Number of days to retain per-day log directories. 0 = keep forever.</summary>
    public int LogRetentionDays { get; set; } = 30;
    public List<OllamaInstanceConfig> OllamaInstances { get; set; } = new();

    /// <summary>
    /// Optional API key required on proxy endpoints via X-Api-Key header or ?api_key= query parameter.
    /// Leave empty to disable authentication.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// CORS allowed origins for the dashboard. Use ["*"] to allow all origins (default).
    /// </summary>
    public List<string> CorsAllowedOrigins { get; set; } = new();

    /// <summary>
    /// Maximum number of requests per minute per IP. 0 = disabled (default).
    /// </summary>
    public int RateLimitRequestsPerMinute { get; set; } = 0;
}

public class OllamaInstanceConfig
{
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public List<string> Models { get; set; } = new();
}
