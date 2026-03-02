using Lakerfield.AiProxy.Models;
using Lakerfield.AiProxy.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lakerfield.AiProxy.Tests;

public class OllamaRegistryServiceTests
{
    private static OllamaRegistryService CreateRegistry(params OllamaInstanceConfig[] instances)
    {
        var options = Options.Create(new AiProxyOptions
        {
            OllamaInstances = instances.ToList()
        });
        return new OllamaRegistryService(options, NullLogger<OllamaRegistryService>.Instance);
    }

    [Fact]
    public void Constructor_InitializesInstancesAsHealthy()
    {
        var registry = CreateRegistry(
            new OllamaInstanceConfig { Name = "a", BaseUrl = "http://a:11434", Models = ["llama3"] },
            new OllamaInstanceConfig { Name = "b", BaseUrl = "http://b:11434", Models = ["mistral"] }
        );

        var all = registry.GetAllInstances();
        Assert.Equal(2, all.Count);
        Assert.All(all, i => Assert.True(i.IsHealthy));
    }

    [Fact]
    public void GetHealthyInstances_ReturnsOnlyHealthyOnes()
    {
        var registry = CreateRegistry(
            new OllamaInstanceConfig { Name = "a", BaseUrl = "http://a:11434" },
            new OllamaInstanceConfig { Name = "b", BaseUrl = "http://b:11434" }
        );

        registry.MarkHealthy("a", false);

        var healthy = registry.GetHealthyInstances();
        Assert.Single(healthy);
        Assert.Equal("b", healthy[0].Name);
    }

    [Fact]
    public void GetInstancesForModel_ReturnsMatchingHealthyInstances()
    {
        var registry = CreateRegistry(
            new OllamaInstanceConfig { Name = "a", BaseUrl = "http://a:11434", Models = ["llama3", "mistral"] },
            new OllamaInstanceConfig { Name = "b", BaseUrl = "http://b:11434", Models = ["phi3"] }
        );

        var results = registry.GetInstancesForModel("llama3");
        Assert.Single(results);
        Assert.Equal("a", results[0].Name);
    }

    [Fact]
    public void GetInstancesForModel_IsCaseInsensitive()
    {
        var registry = CreateRegistry(
            new OllamaInstanceConfig { Name = "a", BaseUrl = "http://a:11434", Models = ["LLaMa3"] }
        );

        var results = registry.GetInstancesForModel("llama3");
        Assert.Single(results);
    }

    [Fact]
    public void GetInstancesForModel_ExcludesUnhealthyInstances()
    {
        var registry = CreateRegistry(
            new OllamaInstanceConfig { Name = "a", BaseUrl = "http://a:11434", Models = ["llama3"] }
        );

        registry.MarkHealthy("a", false);

        var results = registry.GetInstancesForModel("llama3");
        Assert.Empty(results);
    }

    [Fact]
    public void MarkHealthy_UpdatesHealthStatus()
    {
        var registry = CreateRegistry(
            new OllamaInstanceConfig { Name = "a", BaseUrl = "http://a:11434" }
        );

        registry.MarkHealthy("a", false);
        Assert.False(registry.GetAllInstances()[0].IsHealthy);

        registry.MarkHealthy("a", true);
        Assert.True(registry.GetAllInstances()[0].IsHealthy);
    }

    [Fact]
    public void IncrementConnections_IncreasesCounter()
    {
        var registry = CreateRegistry(
            new OllamaInstanceConfig { Name = "a", BaseUrl = "http://a:11434" }
        );

        registry.IncrementConnections("a");
        registry.IncrementConnections("a");

        Assert.Equal(2, registry.GetAllInstances()[0].ActiveConnections);
    }

    [Fact]
    public void DecrementConnections_DecreasesCounter()
    {
        var registry = CreateRegistry(
            new OllamaInstanceConfig { Name = "a", BaseUrl = "http://a:11434" }
        );

        registry.IncrementConnections("a");
        registry.IncrementConnections("a");
        registry.DecrementConnections("a");

        Assert.Equal(1, registry.GetAllInstances()[0].ActiveConnections);
    }

    [Fact]
    public void DecrementConnections_DoesNotGoBelowZero()
    {
        var registry = CreateRegistry(
            new OllamaInstanceConfig { Name = "a", BaseUrl = "http://a:11434" }
        );

        registry.DecrementConnections("a");

        Assert.Equal(0, registry.GetAllInstances()[0].ActiveConnections);
    }

    [Fact]
    public void UpdateModels_ReplacesModelList()
    {
        var registry = CreateRegistry(
            new OllamaInstanceConfig { Name = "a", BaseUrl = "http://a:11434", Models = ["llama3"] }
        );

        registry.UpdateModels("a", ["phi3", "mistral"]);

        var instance = registry.GetAllInstances()[0];
        Assert.Equal(2, instance.Models.Count);
        Assert.Contains("phi3", instance.Models);
        Assert.Contains("mistral", instance.Models);
    }

    [Fact]
    public void UnknownInstanceName_DoesNotThrow()
    {
        var registry = CreateRegistry(
            new OllamaInstanceConfig { Name = "a", BaseUrl = "http://a:11434" }
        );

        // Should not throw for unknown names
        registry.MarkHealthy("unknown", false);
        registry.IncrementConnections("unknown");
        registry.DecrementConnections("unknown");
        registry.UpdateModels("unknown", ["x"]);
    }
}
