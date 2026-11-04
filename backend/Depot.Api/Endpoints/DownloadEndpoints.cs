using Depot.Api.Security;
using Depot.Application.Downloads;
using Depot.Domain.Common;
using Depot.Domain.Downloads;
using Microsoft.AspNetCore.Mvc;

namespace Depot.Api.Endpoints;

public static class DownloadEndpoints
{
    // Room for the multipart framing and the library fields on top of the file itself.
    private const int UploadOverheadBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapDownloadEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/downloads");

        // 200 with [] when qBittorrent has no torrents; a problem details error when it can't be reached.
        group.MapGet("/", (DownloadService service, CancellationToken cancellationToken) =>
            service.ListAsync(cancellationToken));

        // 200 with the download in its new state; 404 when the hash isn't in qBittorrent.
        group.MapPost("/{hash}/pause", (string hash, DownloadService service, CancellationToken cancellationToken) =>
            service.PauseAsync(hash, cancellationToken));

        group.MapPost("/{hash}/resume", (string hash, DownloadService service, CancellationToken cancellationToken) =>
            service.ResumeAsync(hash, cancellationToken));

        // deleteFiles is required. 200 with what was done; 400 without deleteFiles; 404 for an unknown hash.
        group.MapDelete("/{hash}", (string hash, bool? deleteFiles, DownloadService service, CancellationToken cancellationToken) =>
            service.RemoveAsync(hash, deleteFiles, cancellationToken));

        // 201 when qBittorrent accepted the link, 400 for a malformed link, 409 for a duplicate.
        group.MapPost("/magnet", async (AddMagnetInput input, DownloadService service, CancellationToken cancellationToken) =>
            Results.Created((string?)null, await service.AddMagnetAsync(input, cancellationToken)))
            .RequireRateLimiting(RateLimits.Expensive);

        // multipart/form-data with "file" and the library choice ("mediaType", "title", "season"),
        // as in /magnet. Same answers as /magnet, plus 413 when the whole request is larger than
        // the upload limit.
        group.MapPost("/torrent", async (
                IFormFile? file,
                [FromForm] string? mediaType,
                [FromForm] string? title,
                [FromForm] string? season,
                DownloadService service,
                CancellationToken cancellationToken) =>
            {
                var content = file is null ? null : await ReadAllAsync(file, cancellationToken);
                var input = new AddTorrentFileInput(file?.FileName, content, mediaType, title, ParseSeason(season));
                return Results.Created((string?)null, await service.AddTorrentFileAsync(input, cancellationToken));
            })
            // Requests need the access token in a header, which a form posted from another site
            // can't carry, so there is no form to protect with an antiforgery token.
            .DisableAntiforgery()
            .RequireRateLimiting(RateLimits.Expensive)
            .WithMetadata(new RequestSizeLimitAttribute(TorrentFile.MaxSizeBytes + UploadOverheadBytes));

        return routes;
    }

    // Read as text so a bad value gets the same field error as in the JSON endpoint.
    private static int? ParseSeason(string? season)
    {
        if (string.IsNullOrWhiteSpace(season))
        {
            return null;
        }

        return int.TryParse(season, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new DomainValidationException("season", "Enter the season as a whole number.");
    }

    private static async Task<byte[]> ReadAllAsync(IFormFile file, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}
