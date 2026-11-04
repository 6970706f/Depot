namespace Depot.Domain.Common;

// Raised when input breaks a domain rule. Field names the offending value so the
// client can show the message next to it.
public sealed class DomainValidationException(string field, string message) : Exception(message)
{
    public string Field { get; } = field;
}
