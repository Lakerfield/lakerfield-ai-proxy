using Lakerfield.AiProxy.Models;
using Lakerfield.AiProxy.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lakerfield.AiProxy.Tests;

public class LoadBalancerServiceTests
{
    private static (OllamaRegistryService registry, LoadBalancerService lb) Create(
        params OllamaInstanceConfig[] instances)
    {
        var options = Options.Create(new AiProxyOptions { OllamaInstances = instances.ToList() });
        var registry = new OllamaRegistryService(options, NullLogger<OllamaRegistryService>.Instance);
        var lb = new LoadBalancerService(registry, NullLogger<LoadBalancerService>.Instance);
        return (registry, lb);
    }

    [Fact]
    public void SelectInstance_ReturnsNull_WhenNoHealthyInstances()
    {
        var (registry, lb) = Create(
            new OllamaInstanceConfig { Name = "a", BaseUrl = "http://a:11434" }
        );
        registry.MarkHealthy("a", false);

        var result = lb.SelectInstance(null);
        Assert.Null(result);
    }

    [Fact]
    public void SelectInstance_ReturnsSingleHealthyInstance()
    {
        var (_, lb) = Create(
            new OllamaInstanceConfig { Name = "a", BaseUrl = "http://a:11434" }
        );

        var result = lb.SelectInstance(null);
        Assert.NotNull(result);
        Assert.Equal("a", result.Name);
    }

    [Fact]
    public void SelectInstance_PrefersInstanceWithModel()
    {
        var (_, lb) = Create(
            new OllamaInstanceConfig { Name = "a", BaseUrl = "http://a:11434", Models = ["llama3"] },
            new OllamaInstanceConfig { Name = "b", BaseUrl = "http://b:11434", Models = ["mistral"] }
        );

        var result = lb.SelectInstance("mistral");
        Assert.NotNull(result);
        Assert.Equal("b", result.Name);
    }

    [Fact]
    public void SelectInstance_FallsBackToAnyHealthy_WhenModelNotFound()
    {
        var (_, lb) = Create(
            new OllamaInstanceConfig { Name = "a", BaseUrl = "http://a:11434", Models = ["llama3"] }
        );

        // "unknownmodel" not on any instance; should fall back to any healthy instance
        var result = lb.SelectInstance("unknownmodel");
        Assert.NotNull(result);
        Assert.Equal("a", result.Name);
    }

    [Fact]
    public void SelectInstance_ChoosesLeastConnections()
    {
        var (registry, lb) = Create(
            new OllamaInstanceConfig { Name = "a", BaseUrl = "http://a:11434" },
            new OllamaInstanceConfig { Name = "b", BaseUrl = "http://b:11434" }
        );

        // Give "a" more connections
        registry.IncrementConnections("a");
        registry.IncrementConnections("a");

        var result = lb.SelectInstance(null);
        Assert.NotNull(result);
        Assert.Equal("b", result.Name);
    }

    [Fact]
    public void SelectFallbackInstance_ExcludesGivenInstance()
    {
        var (_, lb) = Create(
            new OllamaInstanceConfig { Name = "a", BaseUrl = "http://a:11434" },
            new OllamaInstanceConfig { Name = "b", BaseUrl = "http://b:11434" }
        );

        var result = lb.SelectFallbackInstance(null, "a");
        Assert.NotNull(result);
        Assert.Equal("b", result.Name);
    }

    [Fact]
    public void SelectFallbackInstance_ReturnsNull_WhenNoOtherInstances()
    {
        var (_, lb) = Create(
            new OllamaInstanceConfig { Name = "a", BaseUrl = "http://a:11434" }
        );

        var result = lb.SelectFallbackInstance(null, "a");
        Assert.Null(result);
    }

    [Fact]
    public void SelectFallbackInstance_ReturnsNull_WhenOnlyExcludedIsHealthy()
    {
        var (registry, lb) = Create(
            new OllamaInstanceConfig { Name = "a", BaseUrl = "http://a:11434" },
            new OllamaInstanceConfig { Name = "b", BaseUrl = "http://b:11434" }
        );

        // Make b unhealthy
        registry.MarkHealthy("b", false);

        // Fallback excluding "a" has no candidates
        var result = lb.SelectFallbackInstance(null, "a");
        Assert.Null(result);
    }

    [Fact]
    public void SelectFallbackInstance_UsesRoundRobin_WithMultipleCandidates()
    {
        var (_, lb) = Create(
            new OllamaInstanceConfig { Name = "a", BaseUrl = "http://a:11434" },
            new OllamaInstanceConfig { Name = "b", BaseUrl = "http://b:11434" },
            new OllamaInstanceConfig { Name = "c", BaseUrl = "http://c:11434" }
        );

        // Exclude "a" — candidates are b and c; round-robin should distribute
        var results = Enumerable.Range(0, 10)
            .Select(_ => lb.SelectFallbackInstance(null, "a")?.Name)
            .ToList();

        Assert.All(results, r => Assert.NotNull(r));
        Assert.All(results, r => Assert.NotEqual("a", r));
        // Both b and c should appear at least once over 10 calls
        Assert.Contains("b", results);
        Assert.Contains("c", results);
    }
}
