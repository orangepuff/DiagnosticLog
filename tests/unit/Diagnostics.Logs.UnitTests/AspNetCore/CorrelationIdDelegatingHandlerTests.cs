using Diagnostics.Abstractions;
using Diagnostics.AspNetCore;

namespace Diagnostics.Logs.UnitTests.AspNetCore;

public class CorrelationIdDelegatingHandlerTests
{
    private sealed class StubInnerHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    [Fact]
    public async Task SendAsync_AddsTheCurrentCorrelationIdHeader()
    {
        var correlationContext = new CorrelationContext();
        var correlationId = Guid.NewGuid();
        correlationContext.SetCorrelationId(correlationId);

        HttpRequestMessage? captured = null;
        var inner = new StubInnerHandler(req =>
        {
            captured = req;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        });

        var handler = new CorrelationIdDelegatingHandler(correlationContext) { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/api");

        await invoker.SendAsync(request, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(correlationId.ToString(), captured!.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single());
    }

    [Fact]
    public async Task SendAsync_ReplacesAnyExistingCorrelationIdHeaderOnTheOutgoingRequest()
    {
        var correlationContext = new CorrelationContext();
        var correlationId = Guid.NewGuid();
        correlationContext.SetCorrelationId(correlationId);

        HttpRequestMessage? captured = null;
        var inner = new StubInnerHandler(req =>
        {
            captured = req;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        });

        var handler = new CorrelationIdDelegatingHandler(correlationContext) { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/api");
        request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, "stale-value");

        await invoker.SendAsync(request, CancellationToken.None);

        var values = captured!.Headers.GetValues(CorrelationIdMiddleware.HeaderName).ToList();
        Assert.Single(values);
        Assert.Equal(correlationId.ToString(), values[0]);
    }

    [Fact]
    public async Task SendAsync_ForwardsTheResponseFromTheInnerHandler()
    {
        var correlationContext = new CorrelationContext();
        var inner = new StubInnerHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        var handler = new CorrelationIdDelegatingHandler(correlationContext) { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/api");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
}
