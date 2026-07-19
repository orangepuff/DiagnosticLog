using System.Collections.Concurrent;
using Diagnostics.NLog.Buffering;

namespace Diagnostics.Logs.UnitTests.NLog;

public class BoundedBatchWriterTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Enqueue_FlushesOnceBatchSizeIsReached()
    {
        var flushedBatches = new ConcurrentQueue<IReadOnlyList<int>>();
        var flushSignal = new SemaphoreSlim(0);
        var metrics = new WriteMetrics();

        await using var writer = new BoundedBatchWriter<int>(
            maxQueueSize: 1_000,
            batchSize: 3,
            flushInterval: TimeSpan.FromSeconds(30), // long enough that only the count trigger fires
            flush: (batch, _) =>
            {
                flushedBatches.Enqueue(batch);
                flushSignal.Release();
                return Task.CompletedTask;
            },
            writeFallback: _ => throw new InvalidOperationException("Should not fall back on a healthy flush."),
            metrics: metrics);

        writer.Enqueue(1);
        writer.Enqueue(2);
        writer.Enqueue(3);

        var signalled = await flushSignal.WaitAsync(WaitTimeout);
        Assert.True(signalled, "Expected a flush once 3 items were enqueued against a batch size of 3.");

        var flushed = Assert.Single(flushedBatches);
        Assert.Equal([1, 2, 3], flushed);
        Assert.Equal(3, metrics.Enqueued);
        Assert.Equal(3, metrics.Flushed);
        Assert.Equal(0, metrics.Dropped);
    }

    [Fact]
    public async Task Enqueue_FlushesOnTimer_WhenBatchNeverReachesBatchSize()
    {
        var flushedBatches = new ConcurrentQueue<IReadOnlyList<string>>();
        var flushSignal = new SemaphoreSlim(0);
        var metrics = new WriteMetrics();

        await using var writer = new BoundedBatchWriter<string>(
            maxQueueSize: 1_000,
            batchSize: 1_000, // effectively unreachable — only the timer should trigger the flush
            flushInterval: TimeSpan.FromMilliseconds(100),
            flush: (batch, _) =>
            {
                flushedBatches.Enqueue(batch);
                flushSignal.Release();
                return Task.CompletedTask;
            },
            writeFallback: _ => throw new InvalidOperationException("Should not fall back on a healthy flush."),
            metrics: metrics);

        writer.Enqueue("only-item");

        var signalled = await flushSignal.WaitAsync(WaitTimeout);
        Assert.True(signalled, "Expected the periodic timer to flush the lone item.");

        var flushed = Assert.Single(flushedBatches);
        Assert.Equal(["only-item"], flushed);
    }

    [Fact]
    public async Task Enqueue_WhenFlushKeepsFailing_FallsBackAfterOneRetry()
    {
        var attempts = 0;
        var fallbackSignal = new SemaphoreSlim(0);
        IReadOnlyList<int>? fallbackRows = null;
        var metrics = new WriteMetrics();

        await using var writer = new BoundedBatchWriter<int>(
            maxQueueSize: 1_000,
            batchSize: 1,
            flushInterval: TimeSpan.FromSeconds(30),
            flush: (_, _) =>
            {
                Interlocked.Increment(ref attempts);
                throw new InvalidOperationException("Simulated DiagnosticLogs outage.");
            },
            writeFallback: rows =>
            {
                fallbackRows = rows;
                fallbackSignal.Release();
            },
            metrics: metrics);

        writer.Enqueue(42);

        var signalled = await fallbackSignal.WaitAsync(WaitTimeout);
        Assert.True(signalled, "Expected the writer to fall back to the local sink after retrying once.");

        Assert.Equal([42], fallbackRows);
        Assert.Equal(2, attempts); // initial attempt + exactly one retry
        Assert.Equal(2, metrics.FailedBatches);
        Assert.Equal(1, metrics.FallbackWrites);
        Assert.Equal(0, metrics.Flushed);
    }

    [Fact]
    public async Task Enqueue_BeyondQueueCapacity_DropsAndCountsInsteadOfBlocking()
    {
        // A flush that never returns keeps the background reader from ever draining the channel,
        // so publishing faster than capacity deterministically overflows it — never blocking the
        // caller (design doc §7).
        // RunContinuationsAsynchronously: without it, TrySetResult below would run the writer's
        // background-loop continuation synchronously, inline, on this test thread — re-entering
        // the loop's machinery from here instead of dispatching it back to the thread pool.
        var neverCompletes = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var metrics = new WriteMetrics();

        await using var writer = new BoundedBatchWriter<int>(
            maxQueueSize: 2,
            batchSize: 1,
            flushInterval: TimeSpan.FromMilliseconds(10),
            flush: async (_, _) => await neverCompletes.Task,
            writeFallback: _ => { },
            metrics: metrics);

        try
        {
            for (var i = 0; i < 50; i++)
                writer.Enqueue(i);

            Assert.True(metrics.Dropped > 0, "Expected at least one row to be dropped once the bounded queue filled up.");
            Assert.Equal(50, metrics.Enqueued + metrics.Dropped);
        }
        finally
        {
            // Must run even if an assertion above fails — otherwise the writer's background loop
            // stays stuck awaiting this forever and the `await using` disposal below hangs.
            neverCompletes.TrySetResult(null);
        }
    }
}
