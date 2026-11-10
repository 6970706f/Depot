namespace Depot.Application.Status;

// The last Jellyfin status, kept for a few seconds. One instance for the app; saving the
// settings clears it.
public sealed class JellyfinStatusCache(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(10);

    private readonly Lock gate = new();
    private JellyfinStatusView? status;
    private DateTimeOffset expiresAt;

    public JellyfinStatusView? Get()
    {
        lock (gate)
        {
            return status is not null && clock.GetUtcNow() < expiresAt ? status : null;
        }
    }

    public void Set(JellyfinStatusView view)
    {
        lock (gate)
        {
            status = view;
            expiresAt = clock.GetUtcNow() + Lifetime;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            status = null;
        }
    }
}
