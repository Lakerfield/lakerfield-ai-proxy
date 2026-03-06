using Lakerfield.AiProxy.Middleware;
using Microsoft.AspNetCore.Http;

namespace Lakerfield.AiProxy.Tests;

public class RequestLoggingMiddlewareTests
{
    [Fact]
    public void ExtractApiKey_ReturnsNull_WhenNoAuthHeaders()
    {
        var context = new DefaultHttpContext();

        var key = RequestLoggingMiddleware.ExtractApiKey(context.Request);

        Assert.Null(key);
    }

    [Fact]
    public void ExtractApiKey_ExtractsFromXApiKeyHeader()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Api-Key"] = "testcli";

        var key = RequestLoggingMiddleware.ExtractApiKey(context.Request);

        Assert.Equal("testcli", key);
    }

    [Fact]
    public void ExtractApiKey_ExtractsFromAuthorizationBearerHeader()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Authorization"] = "Bearer testcli";

        var key = RequestLoggingMiddleware.ExtractApiKey(context.Request);

        Assert.Equal("testcli", key);
    }

    [Fact]
    public void ExtractApiKey_ExtractsFromAuthorizationBearerHeader_CaseInsensitive()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Authorization"] = "bearer testcli";

        var key = RequestLoggingMiddleware.ExtractApiKey(context.Request);

        Assert.Equal("testcli", key);
    }

    [Fact]
    public void ExtractApiKey_PrefersXApiKeyOverAuthorization()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Api-Key"] = "mykey";
        context.Request.Headers["Authorization"] = "Bearer frombearer";

        var key = RequestLoggingMiddleware.ExtractApiKey(context.Request);

        Assert.Equal("mykey", key);
    }

    [Fact]
    public void ExtractApiKey_TruncatesLongKey()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Authorization"] = "Bearer sk-verylongsecretkey";

        var key = RequestLoggingMiddleware.ExtractApiKey(context.Request);

        Assert.Equal("sk-veryl…", key);
    }

    [Fact]
    public void ExtractApiKey_DoesNotTruncateShortKey()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Authorization"] = "Bearer testcli";

        var key = RequestLoggingMiddleware.ExtractApiKey(context.Request);

        // "testcli" is 7 chars, not > 8, so no truncation
        Assert.Equal("testcli", key);
    }
}
