using Depot.Domain.Common;

namespace Depot.Api.Endpoints;

// Enum values in query strings and routes are written in lowercase ("movie", "failed"), which
// minimal APIs would only bind case-sensitively, so they're parsed here with a field error.
public static class QueryValues
{
    public static T? Enum<T>(string? value, string field)
        where T : struct, System.Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return System.Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && System.Enum.IsDefined(parsed) && !value.Any(char.IsDigit)
            ? parsed
            : throw new DomainValidationException(field, $"Use one of: {string.Join(", ", System.Enum.GetNames<T>().Select(name => name.ToLowerInvariant()))}.");
    }
}
