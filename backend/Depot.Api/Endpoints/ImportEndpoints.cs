using Depot.Application.Imports;
using Depot.Domain.Library;

namespace Depot.Api.Endpoints;

public static class ImportEndpoints
{
    public static void MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/imports");

        // The latest jobs, newest first; ?status= filters (waiting, importing, completed, failed, skipped).
        group.MapGet("", (string? status, ImportJobService service, CancellationToken cancellationToken) =>
            service.ListAsync(QueryValues.Enum<ImportJobStatus>(status, "status"), cancellationToken));

        // What the worker is doing and how many jobs are in each state.
        group.MapGet("/status", (ImportJobService service, CancellationToken cancellationToken) =>
            service.StatusAsync(cancellationToken));

        // 409 when the job's state doesn't allow it, 404 for an unknown job.
        group.MapPost("/{id:int}/retry", (int id, ImportJobService service, CancellationToken cancellationToken) =>
            service.RetryAsync(id, cancellationToken));
        group.MapPost("/{id:int}/skip", (int id, ImportJobService service, CancellationToken cancellationToken) =>
            service.SkipAsync(id, cancellationToken));
    }
}
