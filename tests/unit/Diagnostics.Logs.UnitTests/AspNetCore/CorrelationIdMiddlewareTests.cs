using Diagnostics.Abstractions;
using Diagnostics.AspNetCore;
using Microsoft.AspNetCore.Http;

namespace Diagnostics.Logs.UnitTests.AspNetCore;

public class CorrelationIdMiddlewareTests
{
    private const string RawIncomingItemKey = "Diagnostics.RawIncomingCorrelationId";

    [Fact]
    public async Task InvokeAsync_ValidGuidHeader_UsesItAsTheCorrelationId()
    {
        var incoming = Guid.NewGuid();
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = incoming.ToString();

        var correlationContext = new CorrelationContext();
        Guid observed = default;
        var sut = new CorrelationIdMiddleware(_ =>
        {
            // AsyncLocal state set inside InvokeAsync only flows to its child call chain
            // (i.e. `next`), not back out to whoever awaited InvokeAsync — so it must be
            // observed from here, not after the call returns.
            observed = correlationContext.CorrelationId;
            return Task.CompletedTask;
        });

        await sut.InvokeAsync(context, correlationContext);

        Assert.Equal(incoming, observed);
        Assert.Equal(incoming.ToString(), context.Response.Headers[CorrelationIdMiddleware.HeaderName]);
        Assert.False(context.Items.ContainsKey(RawIncomingItemKey));
    }

    [Fact]
    public async Task InvokeAsync_NoHeader_GeneratesACorrelationIdAndEchoesIt()
    {
        var context = new DefaultHttpContext();
        var correlationContext = new CorrelationContext();
        Guid observed = default;
        var sut = new CorrelationIdMiddleware(_ =>
        {
            observed = correlationContext.CorrelationId;
            return Task.CompletedTask;
        });

        await sut.InvokeAsync(context, correlationContext);

        Assert.NotEqual(Guid.Empty, observed);
        Assert.Equal(observed.ToString(), context.Response.Headers[CorrelationIdMiddleware.HeaderName]);
        Assert.False(context.Items.ContainsKey(RawIncomingItemKey));
    }

    [Fact]
    public async Task InvokeAsync_NonGuidHeader_GeneratesNewIdAndPreservesRawValueOnItems()
    {
        const string rawValue = "not-a-guid-from-some-other-system";
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = rawValue;

        var correlationContext = new CorrelationContext();
        Guid observed = default;
        var sut = new CorrelationIdMiddleware(_ =>
        {
            observed = correlationContext.CorrelationId;
            return Task.CompletedTask;
        });

        await sut.InvokeAsync(context, correlationContext);

        Assert.NotEqual(Guid.Empty, observed);
        Assert.Equal(rawValue, context.Items[RawIncomingItemKey]);
        Assert.Equal(observed.ToString(), context.Response.Headers[CorrelationIdMiddleware.HeaderName]);
    }

    [Fact]
    public async Task InvokeAsync_CallsNext()
    {
        var context = new DefaultHttpContext();
        var correlationContext = new CorrelationContext();
        var nextCallCount = 0;
        var sut = new CorrelationIdMiddleware(_ =>
        {
            nextCallCount++;
            return Task.CompletedTask;
        });

        await sut.InvokeAsync(context, correlationContext);

        Assert.Equal(1, nextCallCount);
    }
}
