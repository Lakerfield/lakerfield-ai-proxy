using Lakerfield.AiProxy.Services;

namespace Lakerfield.AiProxy.Tests;

public class ActiveRequestStoreTests
{
    [Fact]
    public void TryGet_ReturnsNull_WhenRequestNotAdded()
    {
        var store = new ActiveRequestStore();

        var result = store.TryGet("nonexistent-id");

        Assert.Null(result);
    }

    [Fact]
    public void Add_ThenTryGet_ReturnsEntry()
    {
        var store = new ActiveRequestStore();
        var headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" };

        store.Add("req-1", "{\"model\":\"llama3\"}", headers);
        var result = store.TryGet("req-1");

        Assert.NotNull(result);
        Assert.Equal("{\"model\":\"llama3\"}", result.RequestBody);
        Assert.Equal("application/json", result.RequestHeaders?["Content-Type"]);
    }

    [Fact]
    public void Remove_RemovesEntry()
    {
        var store = new ActiveRequestStore();
        store.Add("req-1", "body", null);

        store.Remove("req-1");
        var result = store.TryGet("req-1");

        Assert.Null(result);
    }

    [Fact]
    public void Remove_DoesNotThrow_WhenRequestNotPresent()
    {
        var store = new ActiveRequestStore();

        // Should not throw
        store.Remove("nonexistent-id");
    }

    [Fact]
    public void Add_Overwrites_ExistingEntry()
    {
        var store = new ActiveRequestStore();
        store.Add("req-1", "first", null);
        store.Add("req-1", "second", null);

        var result = store.TryGet("req-1");

        Assert.NotNull(result);
        Assert.Equal("second", result.RequestBody);
    }

    [Fact]
    public void Store_HandlesNullBodyAndHeaders()
    {
        var store = new ActiveRequestStore();
        store.Add("req-1", null, null);

        var result = store.TryGet("req-1");

        Assert.NotNull(result);
        Assert.Null(result.RequestBody);
        Assert.Null(result.RequestHeaders);
    }

    [Fact]
    public void Store_HandlesMultipleConcurrentEntries()
    {
        var store = new ActiveRequestStore();
        store.Add("req-1", "body-1", null);
        store.Add("req-2", "body-2", null);
        store.Add("req-3", "body-3", null);

        Assert.Equal("body-1", store.TryGet("req-1")?.RequestBody);
        Assert.Equal("body-2", store.TryGet("req-2")?.RequestBody);
        Assert.Equal("body-3", store.TryGet("req-3")?.RequestBody);

        store.Remove("req-2");

        Assert.NotNull(store.TryGet("req-1"));
        Assert.Null(store.TryGet("req-2"));
        Assert.NotNull(store.TryGet("req-3"));
    }
}
