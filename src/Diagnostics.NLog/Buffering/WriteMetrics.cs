namespace Diagnostics.NLog.Buffering;

/// <summary>
/// Counters for one sink's write pipeline. Exposed so overflow/failure is a visible metric rather than a silent drop (design doc §7). Values are process-lifetime cumulative counts.
/// </summary>
public sealed class WriteMetrics
{
    private long _enqueued;
    private long _dropped;
    private long _flushed;
    private long _failedBatches;
    private long _fallbackWrites;

    public long Enqueued => Interlocked.Read(ref _enqueued);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long Flushed => Interlocked.Read(ref _flushed);
    public long FailedBatches => Interlocked.Read(ref _failedBatches);
    public long FallbackWrites => Interlocked.Read(ref _fallbackWrites);

    public void OnEnqueued() => Interlocked.Increment(ref _enqueued);
    public void OnDropped() => Interlocked.Increment(ref _dropped);
    public void OnFlushed(int count) => Interlocked.Add(ref _flushed, count);
    public void OnFailedBatch() => Interlocked.Increment(ref _failedBatches);
    public void OnFallbackWrite(int count) => Interlocked.Add(ref _fallbackWrites, count);
}
