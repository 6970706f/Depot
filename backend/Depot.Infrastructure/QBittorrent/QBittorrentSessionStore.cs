using System.Security.Cryptography;
using System.Text;
using Depot.Application.QBittorrent;
using Depot.Domain.Settings;

namespace Depot.Infrastructure.QBittorrent;

// Keeps one qBittorrent session for the whole app (registered as a singleton), so each
// request doesn't log in again. Changing the saved settings starts a new session.
//
// A login refused for the credentials is not tried again right away: the interface polls every
// couple of seconds, and qBittorrent bans an address after a few failed logins. Until the wait
// is over, every request fails at once with the same error, without queueing for a login.
// Saving the settings again (or changing them) ends the wait.
public sealed class QBittorrentSessionStore(TimeProvider clock)
{
    public static readonly TimeSpan FirstLoginBackoff = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxLoginBackoff = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim gate = new(1, 1);
    private string? settingsFingerprint;
    private string? sessionCookie;

    // The last refused login: for which settings, why, and when the next try is allowed.
    private string? failedFingerprint;
    private QBittorrentException? failure;
    private DateTimeOffset retryAt;
    private TimeSpan backoff = FirstLoginBackoff;

    public async Task<string?> GetSessionCookieAsync(
        QBittorrentSettings settings,
        Func<QBittorrentSettings, CancellationToken, Task<string?>> logIn,
        CancellationToken cancellationToken)
    {
        var fingerprint = Fingerprint(settings);
        ThrowIfWaiting(fingerprint);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (settingsFingerprint != fingerprint)
            {
                // Another request may have just failed to log in with these settings.
                ThrowIfWaiting(fingerprint);

                // Cleared first so a failed login never leaves the previous session in place.
                settingsFingerprint = null;
                try
                {
                    sessionCookie = await logIn(settings, cancellationToken);
                }
                catch (QBittorrentException exception)
                    when (exception.Failure is QBittorrentFailure.InvalidCredentials or QBittorrentFailure.Banned)
                {
                    RecordRefusal(fingerprint, exception);
                    throw;
                }

                settingsFingerprint = fingerprint;
                failedFingerprint = null;
                backoff = FirstLoginBackoff;
            }

            return sessionCookie;
        }
        finally
        {
            gate.Release();
        }
    }

    // Forgets the session if it's still the one that was rejected. Another request may
    // already have replaced it, and that newer session must be kept.
    public async Task InvalidateAsync(string? rejectedCookie)
    {
        await gate.WaitAsync();
        try
        {
            if (sessionCookie == rejectedCookie)
            {
                settingsFingerprint = null;
                sessionCookie = null;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    // Called when the settings are saved, which is how a person says "try again now".
    public async Task ResetAsync()
    {
        await gate.WaitAsync();
        try
        {
            settingsFingerprint = null;
            sessionCookie = null;
            failedFingerprint = null;
            backoff = FirstLoginBackoff;
        }
        finally
        {
            gate.Release();
        }
    }

    private void ThrowIfWaiting(string fingerprint)
    {
        // Read without the gate on the fast path; a stale read only means one more check inside it.
        var refused = failure;
        if (failedFingerprint != fingerprint || refused is null)
        {
            return;
        }

        var wait = retryAt - clock.GetUtcNow();
        if (wait > TimeSpan.Zero)
        {
            throw new QBittorrentException(
                refused.Failure,
                $"{refused.Message} Depot will try again in {Math.Ceiling(wait.TotalSeconds):0} seconds, or right away when you save the settings.");
        }
    }

    private void RecordRefusal(string fingerprint, QBittorrentException exception)
    {
        // Each refusal in a row doubles the wait, so a wrong password left in place costs
        // qBittorrent a handful of attempts an hour rather than one every poll.
        if (failedFingerprint == fingerprint)
        {
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxLoginBackoff.Ticks));
        }
        else
        {
            backoff = FirstLoginBackoff;
        }

        failedFingerprint = fingerprint;
        failure = exception;
        retryAt = clock.GetUtcNow() + backoff;
    }

    // A hash, so the store doesn't keep another copy of the password.
    private static string Fingerprint(QBittorrentSettings settings) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{settings.Url}\n{settings.Username}\n{settings.Password}")));
}
