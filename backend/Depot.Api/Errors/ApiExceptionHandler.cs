using Depot.Application.Downloads;
using Depot.Application.Imports;
using Depot.Application.Jellyfin;
using Depot.Application.QBittorrent;
using Depot.Domain.Common;
using Depot.Domain.Library;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Depot.Api.Errors;

// Turns the exceptions the inner layers raise on purpose into problem details. Anything else
// falls through to the default handler as a 500.
public sealed class ApiExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var problem = ToProblem(exception);
        if (problem is null)
        {
            return false;
        }

        context.Response.StatusCode = problem.Status!.Value;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    private static ProblemDetails? ToProblem(Exception exception) => exception switch
    {
        DomainValidationException validation =>
            Problems.Create(StatusCodes.Status400BadRequest, "validation", validation.Message, validation.Field),
        // A parameter or body the framework couldn't read, such as malformed JSON.
        BadHttpRequestException badRequest =>
            Problems.Create(badRequest.StatusCode, "bad_request", "The request couldn't be read."),
        DuplicateDownloadException duplicate =>
            WithExtension(Problems.Create(StatusCodes.Status409Conflict, "already_added", duplicate.Message), "hash", duplicate.Hash),
        JobNotFoundException notFound =>
            Problems.Create(StatusCodes.Status404NotFound, "job_not_found", notFound.Message),
        InvalidJobStateException invalidState =>
            Problems.Create(StatusCodes.Status409Conflict, "invalid_job_state", invalidState.Message),
        QBittorrentException qBittorrent =>
            Problems.Create(ToStatusCode(qBittorrent.Failure), ToCode(qBittorrent.Failure), qBittorrent.Message),
        JellyfinException jellyfin =>
            Problems.Create(ToStatusCode(jellyfin.Failure), ToCode(jellyfin.Failure), jellyfin.Message),
        _ => null,
    };

    private static ProblemDetails WithExtension(ProblemDetails problem, string name, object value)
    {
        problem.Extensions[name] = value;
        return problem;
    }

    private static int ToStatusCode(QBittorrentFailure failure) => failure switch
    {
        // Depot has to be set up first.
        QBittorrentFailure.NotConfigured => StatusCodes.Status409Conflict,
        QBittorrentFailure.TorrentNotFound => StatusCodes.Status404NotFound,
        QBittorrentFailure.TorrentRejected => StatusCodes.Status422UnprocessableEntity,
        // The upstream server failed, not Depot nor the request.
        _ => StatusCodes.Status502BadGateway,
    };

    private static int ToStatusCode(JellyfinFailure failure) => failure switch
    {
        JellyfinFailure.NotConfigured => StatusCodes.Status409Conflict,
        JellyfinFailure.ItemNotFound => StatusCodes.Status404NotFound,
        _ => StatusCodes.Status502BadGateway,
    };

    private static string ToCode(JellyfinFailure failure) => failure switch
    {
        JellyfinFailure.NotConfigured => "not_configured",
        JellyfinFailure.Unreachable => "unreachable",
        JellyfinFailure.Timeout => "timeout",
        JellyfinFailure.InvalidApiKey => "invalid_api_key",
        JellyfinFailure.ItemNotFound => "item_not_found",
        _ => "unexpected_response",
    };

    private static string ToCode(QBittorrentFailure failure) => failure switch
    {
        QBittorrentFailure.NotConfigured => "not_configured",
        QBittorrentFailure.Unreachable => "unreachable",
        QBittorrentFailure.Timeout => "timeout",
        QBittorrentFailure.InvalidCredentials => "invalid_credentials",
        QBittorrentFailure.Banned => "banned",
        QBittorrentFailure.TorrentNotFound => "torrent_not_found",
        QBittorrentFailure.TorrentRejected => "torrent_rejected",
        _ => "unexpected_response",
    };
}
