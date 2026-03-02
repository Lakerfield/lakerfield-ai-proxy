namespace Lakerfield.AiProxy.Models;

public class AiProxyOptions
{
    public const string SectionName = "AiProxy";

    public string LogDirectory { get; set; } = "./logs";
    public string LoadBalancingStrategy { get; set; } = "LeastConnections";
    public List<OllamaInstanceConfig> OllamaInstances { get; set; } = new();
}

public class OllamaInstanceConfig
{
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public List<string> Models { get; set; } = new();
}
