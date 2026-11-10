using System.Threading.Channels;

namespace Depot.Application.Jellyfin;

// Hands sync runs to the background worker, one at a time. Two syncs at once would write the
// same items and race on removals, so a run can only be queued after reserving the single slot,
// which stays taken until the worker releases it. One instance for the app.
public sealed class JellyfinSyncQueue
{
    private readonly Channel<int> pending = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
    private int reserved;
    private int activeRunId;

    // The run queued or running, if any.
    public int? ActiveRunId => Volatile.Read(ref activeRunId) is var id and > 0 ? id : null;

    public bool IsBusy => Volatile.Read(ref reserved) == 1;

    // False while another run holds the slot.
    public bool TryReserve() => Interlocked.CompareExchange(ref reserved, 1, 0) == 0;

    // Only after TryReserve succeeded.
    public void Enqueue(int runId)
    {
        Volatile.Write(ref activeRunId, runId);
        pending.Writer.TryWrite(runId);
    }

    public void Release()
    {
        Volatile.Write(ref activeRunId, 0);
        Volatile.Write(ref reserved, 0);
    }

    public IAsyncEnumerable<int> ReadAllAsync(CancellationToken cancellationToken) => pending.Reader.ReadAllAsync(cancellationToken);
}
