namespace Depot.Application.Imports;

public enum ImportRunState
{
    Idle,
    Running,
    // The folders aren't ready; nothing is imported until they are.
    Paused,
    Error,
}

public sealed record ImportRuntimeView(ImportRunState State, string? Message, DateTimeOffset? LastRunAt);

// What the import worker is doing, for the Imports page.
public sealed class ImportRuntime
{
    private ImportRuntimeView current = new(ImportRunState.Idle, null, null);

    public ImportRuntimeView Current => current;

    public void Report(ImportRunState state, string? message, DateTimeOffset? lastRunAt = null) =>
        current = new ImportRuntimeView(state, message, lastRunAt ?? current.LastRunAt);
}

// Wakes the import worker before its next scheduled pass, such as after a retry or new settings.
public sealed class ImportSignal : Depot.Application.Library.ILibrarySettingsListener
{
    private readonly SemaphoreSlim signal = new(0, 1);

    public void RequestPass()
    {
        try
        {
            signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A pass is already requested.
        }
    }

    public void LibrarySettingsSaved() => RequestPass();

    // True when a pass was requested, false when the time ran out.
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => signal.WaitAsync(timeout, cancellationToken);
}
