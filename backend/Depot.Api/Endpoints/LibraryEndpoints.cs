using Depot.Application.Library;

namespace Depot.Api.Endpoints;

public static class LibraryEndpoints
{
    public static void MapLibraryEndpoints(this IEndpointRouteBuilder app)
    {
        // The series folders already in the library, with their seasons, for the add dialog.
        app.MapGet("/api/library/series", (LibraryService service, CancellationToken cancellationToken) =>
            service.ListSeriesAsync(cancellationToken));
    }
}
