using System.Security.Claims;
using Diagnostics.Abstractions.Interfaces;
using Diagnostics.AspNetCore;
using Microsoft.AspNetCore.Http;
using Moq;

namespace Diagnostics.Logs.UnitTests.AspNetCore;

public class TransactionMiddlewareTests
{
    private const string RawIncomingItemKey = "Diagnostics.RawIncomingCorrelationId";

    private static DefaultHttpContext CreateContext(string method = "GET", string path = "/pdf-files", string query = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("example.com");
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        return context;
    }

    [Fact]
    public async Task InvokeAsync_OpensATransaction_WithTheDefaultCategoryAndMethodPathMessage()
    {
        var context = CreateContext(method: "POST", path: "/pdf-files");
        var mockScope = new Mock<ITransactionScope>();
        var mockLogger = new Mock<ITransactionLogger>();
        mockLogger
            .Setup(l => l.BeginTransaction(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(mockScope.Object);

        var sut = new TransactionMiddleware(_ => Task.CompletedTask);

        await sut.InvokeAsync(context, mockLogger.Object);

        mockLogger.Verify(l => l.BeginTransaction(TransactionMiddleware.DefaultCategory, "POST /pdf-files"), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_SetsUrlAndBaseUrl_FromTheRequest()
    {
        var context = CreateContext(path: "/pdf-files", query: "?projectId=1");
        var mockScope = new Mock<ITransactionScope>();
        var mockLogger = new Mock<ITransactionLogger>();
        mockLogger.Setup(l => l.BeginTransaction(It.IsAny<string>(), It.IsAny<string>())).Returns(mockScope.Object);

        var sut = new TransactionMiddleware(_ => Task.CompletedTask);

        await sut.InvokeAsync(context, mockLogger.Object);

        mockScope.Verify(s => s.SetUrl("/pdf-files?projectId=1", "https://example.com"), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_UnauthenticatedRequest_NeverCallsSetUser()
    {
        var context = CreateContext();
        var mockScope = new Mock<ITransactionScope>();
        var mockLogger = new Mock<ITransactionLogger>();
        mockLogger.Setup(l => l.BeginTransaction(It.IsAny<string>(), It.IsAny<string>())).Returns(mockScope.Object);

        var sut = new TransactionMiddleware(_ => Task.CompletedTask);

        await sut.InvokeAsync(context, mockLogger.Object);

        mockScope.Verify(s => s.SetUser(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task InvokeAsync_AuthenticatedRequest_SetsUserFromIdentityName()
    {
        var context = CreateContext();
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], authenticationType: "TestAuth");
        context.User = new ClaimsPrincipal(identity);

        var mockScope = new Mock<ITransactionScope>();
        var mockLogger = new Mock<ITransactionLogger>();
        mockLogger.Setup(l => l.BeginTransaction(It.IsAny<string>(), It.IsAny<string>())).Returns(mockScope.Object);

        var sut = new TransactionMiddleware(_ => Task.CompletedTask);

        await sut.InvokeAsync(context, mockLogger.Object);

        mockScope.Verify(s => s.SetUser("alice"), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_RawIncomingCorrelationIdOnItems_IsCopiedToACustomAttribute()
    {
        var context = CreateContext();
        context.Items[RawIncomingItemKey] = "vendor-trace-id-123";

        var mockScope = new Mock<ITransactionScope>();
        var mockLogger = new Mock<ITransactionLogger>();
        mockLogger.Setup(l => l.BeginTransaction(It.IsAny<string>(), It.IsAny<string>())).Returns(mockScope.Object);

        var sut = new TransactionMiddleware(_ => Task.CompletedTask);

        await sut.InvokeAsync(context, mockLogger.Object);

        mockScope.Verify(s => s.SetCustomAttribute("RawIncomingCorrelationId", "vendor-trace-id-123"), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_SetsStatusCodeCustomAttribute_AfterNextRuns()
    {
        var context = CreateContext();
        var mockScope = new Mock<ITransactionScope>();
        var mockLogger = new Mock<ITransactionLogger>();
        mockLogger.Setup(l => l.BeginTransaction(It.IsAny<string>(), It.IsAny<string>())).Returns(mockScope.Object);

        var sut = new TransactionMiddleware(ctx =>
        {
            ctx.Response.StatusCode = 201;
            return Task.CompletedTask;
        });

        await sut.InvokeAsync(context, mockLogger.Object);

        mockScope.Verify(s => s.SetCustomAttribute("StatusCode", 201), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_SetsStatusCodeCustomAttribute_EvenWhenNextThrows()
    {
        var context = CreateContext();
        var mockScope = new Mock<ITransactionScope>();
        var mockLogger = new Mock<ITransactionLogger>();
        mockLogger.Setup(l => l.BeginTransaction(It.IsAny<string>(), It.IsAny<string>())).Returns(mockScope.Object);

        var sut = new TransactionMiddleware(_ => throw new InvalidOperationException("downstream failure"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.InvokeAsync(context, mockLogger.Object));

        // The transaction scope (and its Dispose -> duration/flush) must still close out even on failure.
        mockScope.Verify(s => s.SetCustomAttribute("StatusCode", It.IsAny<object>()), Times.Once);
        mockScope.Verify(s => s.Dispose(), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_AlwaysDisposesTheScope()
    {
        var context = CreateContext();
        var mockScope = new Mock<ITransactionScope>();
        var mockLogger = new Mock<ITransactionLogger>();
        mockLogger.Setup(l => l.BeginTransaction(It.IsAny<string>(), It.IsAny<string>())).Returns(mockScope.Object);

        var sut = new TransactionMiddleware(_ => Task.CompletedTask);

        await sut.InvokeAsync(context, mockLogger.Object);

        mockScope.Verify(s => s.Dispose(), Times.Once);
    }
}
