namespace Depot.Application.Jellyfin;

public enum JellyfinFailure
{
    NotConfigured,
    Unreachable,
    Timeout,
    InvalidApiKey,
    // The URL leads to an address Depot never connects to, such as a cloud metadata service.
    AddressNotAllowed,
    UnexpectedResponse,
}

// Every failure talking to Jellyfin ends up as this exception. The message is written for the
// person using Depot and never contains the API key.
public sealed class JellyfinException(JellyfinFailure failure, string message) : Exception(message)
{
    public JellyfinFailure Failure { get; } = failure;

    // Failures that every following request would hit too, such as the server being down.
    public bool AffectsEveryRequest => Failure is not JellyfinFailure.UnexpectedResponse;
}
