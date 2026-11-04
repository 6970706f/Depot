namespace Depot.Application.Organization;

public enum OrganizationRunState
{
    Starting,
    Idle,
    Running,
    // Something outside Depot blocks imports, such as a library folder that can't be written.
    Paused,
    Error,
}

public sealed record OrganizationRunSnapshot(OrganizationRunState State, string? Message, DateTimeOffset? LastRunAt);

// What the organization worker is doing, for the status endpoint. One instance for the app.
public sealed class OrganizationRuntimeStatus
{
    private readonly Lock gate = new();
    private OrganizationRunSnapshot current = new(OrganizationRunState.Starting, null, null);

    public OrganizationRunSnapshot Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    public void Report(OrganizationRunState state, string? message, DateTimeOffset? lastRunAt = null)
    {
        lock (gate)
        {
            current = new OrganizationRunSnapshot(state, message, lastRunAt ?? current.LastRunAt);
        }
    }
}

// Wakes the organization worker before its next scheduled pass, for example right after a
// person retries a job. One instance for the app.
public sealed class OrganizationSignal
{
    private readonly SemaphoreSlim wakeUp = new(0, 1);

    public void RequestPass()
    {
        try
        {
            wakeUp.Release();
        }
        catch (SemaphoreFullException)
        {
            // A pass is already requested.
        }
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        await wakeUp.WaitAsync(timeout, cancellationToken);
}
