using System.Threading.Channels;

namespace Diagnostics.NLog.Buffering;

/// <summary>
/// Generic bounded, batched, async writer shared by <c>LogsTarget</c> and <c>TransactionsTarget</c>.
/// Never blocks the caller (<see cref="Enqueue"/> is a non-blocking channel write): on overflow it drops the row and increments <see cref="WriteMetrics.Dropped"/> rather than applying backpressure (design doc §7).
/// A background loop flushes whenever the batch reaches <paramref name="batchSize"/> rows or <paramref name="flushInterval"/> elapses, whichever first.
/// On a flush failure it retries once, then writes the batch to the local file fallback so logging never takes the app down.
/// </summary>
public sealed class BoundedBatchWriter<T> : IAsyncDisposable
{
    private readonly Channel<T> _channel;
    private readonly int _maxQueueSize;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;
    private readonly Func<IReadOnlyList<T>, CancellationToken, Task> _flush;
    private readonly Action<IReadOnlyList<T>> _writeFallback;
    private readonly WriteMetrics _metrics;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public BoundedBatchWriter(
        int maxQueueSize,
        int batchSize,
        TimeSpan flushInterval,
        Func<IReadOnlyList<T>, CancellationToken, Task> flush,
        Action<IReadOnlyList<T>> writeFallback,
        WriteMetrics metrics)
    {
        _maxQueueSize = maxQueueSize;
        _batchSize = batchSize;
        _flushInterval = flushInterval;
        _flush = flush;
        _writeFallback = writeFallback;
        _metrics = metrics;

        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(maxQueueSize)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>
    /// Non-blocking. Drops (and counts) the row if the queue is full.
    /// </summary>
    public void Enqueue(T row)
    {
        // BoundedChannelFullMode.DropWrite makes TryWrite return true unconditionally — even when the item is silently discarded because the channel is full (only FullMode.Wait makes TryWrite return false) — so "was it dropped" has to be read from the channel's depth immediately before write rather than from TryWrite's return value.
        var wasFull = _channel.Reader.Count >= _maxQueueSize;
        _channel.Writer.TryWrite(row);

        if (wasFull)
        {
            _metrics.OnDropped();
        }
        else
        {
            _metrics.OnEnqueued();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var batch = new List<T>(_batchSize);
        using var timer = new PeriodicTimer(_flushInterval);
        var reader = _channel.Reader;

        // A pending read that loses the race against the flush timer must survive into the next iteration rather than being abandoned: the channel is configured SingleReader, and issuing a second concurrent ReadAsync while a prior one is still outstanding is undefined behavior (it can silently strand the wakeup on the abandoned task and hang this loop forever).
        Task<T>? pendingRead = null;

        while (!ct.IsCancellationRequested)
        {
            var wait = timer.WaitForNextTickAsync(ct).AsTask();

            while (batch.Count < _batchSize)
            {
                pendingRead ??= reader.ReadAsync(ct).AsTask();
                var completed = await Task.WhenAny(pendingRead, wait).ConfigureAwait(false);

                if (completed == wait)
                {
                    break;
                }

                try
                {
                    batch.Add(await pendingRead.ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                finally
                {
                    pendingRead = null;
                }
            }

            if (batch.Count > 0)
            {
                await FlushAsync(batch, ct).ConfigureAwait(false);
                batch.Clear();
            }
        }

        // Drain whatever is left on shutdown, best-effort.
        while (reader.TryRead(out var leftover))
        {
            batch.Add(leftover);
        }


        if (batch.Count > 0)
        {
            await FlushAsync(batch, CancellationToken.None).ConfigureAwait(false);
        }  
    }

    private async Task FlushAsync(List<T> batch, CancellationToken ct)
    {
        var snapshot = batch.ToArray();
        try
        {
            await _flush(snapshot, ct).ConfigureAwait(false);
            _metrics.OnFlushed(snapshot.Length);
            return;
        }
        catch
        {
            _metrics.OnFailedBatch();
        }

        // One retry before falling back — transient connectivity blips shouldn't hit the file sink.
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None).ConfigureAwait(false);
            await _flush(snapshot, ct).ConfigureAwait(false);
            _metrics.OnFlushed(snapshot.Length);
            return;
        }
        catch
        {
            _metrics.OnFailedBatch();
        }

        try
        {
            _writeFallback(snapshot);
            _metrics.OnFallbackWrite(snapshot.Length);
        }
        catch
        {
            // Logging must never throw into the app — swallow terminal fallback failures too.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch
        {
            // best-effort drain on shutdown
        }
        _cts.Dispose();
    }
}
