namespace Depot.Application.Jellyfin;

public enum JellyfinFailure
{
    NotConfigured,
    Unreachable,
    Timeout,
    InvalidApiKey,
    UnexpectedResponse,
    ItemNotFound,
}

// Every failure talking to Jellyfin ends up as this exception. The message is written for the
// person using Depot and never contains the API key.
public sealed class JellyfinException(JellyfinFailure failure, string message) : Exception(message)
{
    public JellyfinFailure Failure { get; } = failure;

    public static JellyfinException NotConfigured() =>
        new(JellyfinFailure.NotConfigured, "Jellyfin isn't set up yet. Add its URL and an API key in Settings.");
}
