using Diagnostics.NLog.Buffering;

namespace Diagnostics.Logs.UnitTests.NLog;

public class WriteMetricsTests
{
    [Fact]
    public void Counters_StartAtZero()
    {
        var metrics = new WriteMetrics();

        Assert.Equal(0, metrics.Enqueued);
        Assert.Equal(0, metrics.Dropped);
        Assert.Equal(0, metrics.Flushed);
        Assert.Equal(0, metrics.FailedBatches);
        Assert.Equal(0, metrics.FallbackWrites);
    }

    [Fact]
    public void OnEnqueued_IncrementsEnqueuedOnly()
    {
        var metrics = new WriteMetrics();

        metrics.OnEnqueued();
        metrics.OnEnqueued();

        Assert.Equal(2, metrics.Enqueued);
        Assert.Equal(0, metrics.Dropped);
    }

    [Fact]
    public void OnDropped_IncrementsDroppedOnly()
    {
        var metrics = new WriteMetrics();

        metrics.OnDropped();

        Assert.Equal(1, metrics.Dropped);
        Assert.Equal(0, metrics.Enqueued);
    }

    [Fact]
    public void OnFlushed_AddsTheGivenCount()
    {
        var metrics = new WriteMetrics();

        metrics.OnFlushed(500);
        metrics.OnFlushed(3);

        Assert.Equal(503, metrics.Flushed);
    }

    [Fact]
    public void OnFailedBatch_IncrementsFailedBatchesOnly()
    {
        var metrics = new WriteMetrics();

        metrics.OnFailedBatch();

        Assert.Equal(1, metrics.FailedBatches);
    }

    [Fact]
    public void OnFallbackWrite_AddsTheGivenCount()
    {
        var metrics = new WriteMetrics();

        metrics.OnFallbackWrite(7);

        Assert.Equal(7, metrics.FallbackWrites);
    }
}
