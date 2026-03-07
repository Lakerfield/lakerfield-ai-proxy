using System.Text.Json;
using Lakerfield.AiProxy.Models;
using Lakerfield.AiProxy.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lakerfield.AiProxy.Tests;

public class RequestLogServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RequestLogService _service;

    public RequestLogServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RequestLogServiceTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);

        var options = Options.Create(new AiProxyOptions { LogDirectory = _tempDir });
        _service = new RequestLogService(options, NullLogger<RequestLogService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task LogRequestAsync_WritesIndividualJsonFile()
    {
        var entry = MakeEntry();

        await _service.LogRequestAsync(entry);

        var dateDir = Path.Combine(_tempDir, DateTime.UtcNow.ToString("yyyy-MM-dd"));
        var file = Path.Combine(dateDir, $"{entry.RequestId}.json");
        Assert.True(File.Exists(file), $"Expected file: {file}");
    }

    [Fact]
    public async Task LogRequestAsync_FileContainsFullEntry()
    {
        var entry = MakeEntry();
        entry.RequestBody = "full request body content";
        entry.ResponseBody = "full response body content";

        await _service.LogRequestAsync(entry);

        var dateDir = Path.Combine(_tempDir, DateTime.UtcNow.ToString("yyyy-MM-dd"));
        var file = Path.Combine(dateDir, $"{entry.RequestId}.json");
        var json = await File.ReadAllTextAsync(file);

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var read = JsonSerializer.Deserialize<RequestLogEntry>(json, opts)!;

        Assert.Equal(entry.RequestId, read.RequestId);
        Assert.Equal(entry.RequestBody, read.RequestBody);
        Assert.Equal(entry.ResponseBody, read.ResponseBody);
        Assert.Equal(entry.Endpoint, read.Endpoint);
        Assert.Equal(entry.Model, read.Model);
    }

    [Fact]
    public async Task LogRequestAsync_EachRequestWritesToSeparateFile()
    {
        var entry1 = MakeEntry("request-1", "/v1/chat/completions");
        var entry2 = MakeEntry("request-2", "/v1/messages");

        await _service.LogRequestAsync(entry1);
        await _service.LogRequestAsync(entry2);

        var dateDir = Path.Combine(_tempDir, DateTime.UtcNow.ToString("yyyy-MM-dd"));
        var files = Directory.GetFiles(dateDir, "*.json");

        Assert.Equal(2, files.Length);
        Assert.Contains(Path.Combine(dateDir, $"{entry1.RequestId}.json"), files);
        Assert.Contains(Path.Combine(dateDir, $"{entry2.RequestId}.json"), files);
    }

    [Fact]
    public async Task LogRequestAsync_NoCrossContamination_BetweenRequests()
    {
        var entry1 = MakeEntry("req-a");
        entry1.RequestBody = "body for req-a";

        var entry2 = MakeEntry("req-b");
        entry2.RequestBody = "body for req-b";

        await _service.LogRequestAsync(entry1);
        await _service.LogRequestAsync(entry2);

        var dateDir = Path.Combine(_tempDir, DateTime.UtcNow.ToString("yyyy-MM-dd"));
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var jsonA = await File.ReadAllTextAsync(Path.Combine(dateDir, $"{entry1.RequestId}.json"));
        var jsonB = await File.ReadAllTextAsync(Path.Combine(dateDir, $"{entry2.RequestId}.json"));

        var readA = JsonSerializer.Deserialize<RequestLogEntry>(jsonA, opts)!;
        var readB = JsonSerializer.Deserialize<RequestLogEntry>(jsonB, opts)!;

        Assert.Equal("body for req-a", readA.RequestBody);
        Assert.Equal("body for req-b", readB.RequestBody);
    }

    [Fact]
    public async Task LogRequestAsync_WritesValidJson()
    {
        var entry = MakeEntry();

        await _service.LogRequestAsync(entry);

        var dateDir = Path.Combine(_tempDir, DateTime.UtcNow.ToString("yyyy-MM-dd"));
        var json = await File.ReadAllTextAsync(Path.Combine(dateDir, $"{entry.RequestId}.json"));

        // Should be valid JSON — no exception means it parses fine
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(entry.RequestId, doc.RootElement.GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task LogRequestAsync_WritesIndentedJson()
    {
        var entry = MakeEntry();

        await _service.LogRequestAsync(entry);

        var dateDir = Path.Combine(_tempDir, DateTime.UtcNow.ToString("yyyy-MM-dd"));
        var json = await File.ReadAllTextAsync(Path.Combine(dateDir, $"{entry.RequestId}.json"));

        // Indented JSON has newlines
        Assert.Contains(Environment.NewLine, json);
    }

    [Fact]
    public async Task LogRequestAsync_ConcurrentWritesDontInterfere()
    {
        var entries = Enumerable.Range(0, 20).Select(i => MakeEntry($"concurrent-{i}")).ToList();

        // Write all concurrently
        await Task.WhenAll(entries.Select(e => _service.LogRequestAsync(e)));

        var dateDir = Path.Combine(_tempDir, DateTime.UtcNow.ToString("yyyy-MM-dd"));
        var files = Directory.GetFiles(dateDir, "*.json");

        Assert.Equal(20, files.Length);
    }

    private static RequestLogEntry MakeEntry(string? requestId = null, string endpoint = "/v1/chat/completions") =>
        new()
        {
            RequestId = requestId ?? Guid.NewGuid().ToString(),
            Endpoint = endpoint,
            Model = "llama3",
            StatusCode = 200,
            DurationMs = 1234,
        };
}
