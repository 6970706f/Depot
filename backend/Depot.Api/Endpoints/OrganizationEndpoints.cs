using Depot.Api.Security;
using Depot.Application.Organization;
using Depot.Domain.Common;
using Depot.Domain.Organization;

namespace Depot.Api.Endpoints;

public static class OrganizationEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/organization");

        // The settings come with a check of every folder, so problems show before any import.
        group.MapGet("/settings", (OrganizationSettingsService service, CancellationToken cancellationToken) =>
            service.GetAsync(cancellationToken));

        group.MapPut("/settings", (OrganizationSettingsInput input, OrganizationSettingsService service, CancellationToken cancellationToken) =>
            service.SaveAsync(input, cancellationToken))
            .RequireRateLimiting(RateLimits.Expensive);

        // Checks folders of settings that aren't saved yet.
        group.MapPost("/settings/test", (OrganizationSettingsInput input, OrganizationSettingsService service, CancellationToken cancellationToken) =>
            service.TestAsync(input, cancellationToken))
            .RequireRateLimiting(RateLimits.Expensive);

        group.MapGet("/status", (OrganizationJobService service, CancellationToken cancellationToken) =>
            service.GetStatusAsync(cancellationToken));

        // ?status=failed filters; without it every job is listed, newest first.
        group.MapGet("/jobs", (string? status, OrganizationJobService service, CancellationToken cancellationToken) =>
            service.ListAsync(ParseStatus(status), cancellationToken));

        group.MapGet("/jobs/{id:int}", (int id, OrganizationJobService service, CancellationToken cancellationToken) =>
            service.GetAsync(id, cancellationToken));

        group.MapPost("/jobs/{id:int}/retry", (int id, OrganizationJobService service, CancellationToken cancellationToken) =>
            service.RetryAsync(id, cancellationToken));

        group.MapPost("/jobs/{id:int}/skip", (int id, OrganizationJobService service, CancellationToken cancellationToken) =>
            service.SkipAsync(id, cancellationToken));

        // The series already in the library, with their seasons, for the add download dialog.
        routes.MapGet("/api/library/series", (LibraryService service, CancellationToken cancellationToken) =>
            service.ListSeriesAsync(cancellationToken));

        return routes;
    }

    private static OrganizationJobStatus? ParseStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        return Enum.TryParse<OrganizationJobStatus>(status, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new DomainValidationException(
                "status", $"Use one of: {string.Join(", ", Enum.GetNames<OrganizationJobStatus>().Select(name => char.ToLowerInvariant(name[0]) + name[1..]))}.");
    }
}
