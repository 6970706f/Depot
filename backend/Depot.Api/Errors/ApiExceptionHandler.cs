using Depot.Application.Downloads;
using Depot.Application.Organization;
using Depot.Application.QBittorrent;
using Depot.Domain.Common;
using Depot.Domain.Organization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Depot.Api.Errors;

// Turns the exceptions that the inner layers raise on purpose into problem details.
// Anything else falls through to the default handler as a 500.
public sealed class ApiExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails? problem = exception switch
        {
            DomainValidationException validation => new ValidationProblemDetails(
                new Dictionary<string, string[]> { [validation.Field] = [validation.Message] })
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "The request has invalid values.",
            },
            DuplicateDownloadException duplicate => new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "The download already exists.",
                Detail = duplicate.Message,
                Extensions = { ["code"] = "already_added", ["hash"] = duplicate.Hash },
            },
            // A parameter or body the framework couldn't read, such as deleteFiles=maybe or
            // malformed JSON. In Development the framework throws these instead of answering.
            BadHttpRequestException badRequest => new ProblemDetails
            {
                Status = badRequest.StatusCode,
                Title = "The request couldn't be read.",
                Detail = badRequest.Message,
                Extensions = { ["code"] = "bad_request" },
            },
            JobNotFoundException notFound => new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "The organization job doesn't exist.",
                Detail = notFound.Message,
                Extensions = { ["code"] = "job_not_found" },
            },
            // The job's state doesn't allow the action, such as skipping a finished import.
            InvalidJobStateException invalidState => new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "The job can't do that now.",
                Detail = invalidState.Message,
                Extensions = { ["code"] = "invalid_job_state" },
            },
            JobConcurrencyException changed => new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "The job changed in the meantime.",
                Detail = changed.Message,
                Extensions = { ["code"] = "job_changed" },
            },
            QBittorrentException qBittorrent => new ProblemDetails
            {
                Status = ToStatusCode(qBittorrent.Failure),
                Title = "qBittorrent request failed.",
                Detail = qBittorrent.Message,
                Extensions = { ["code"] = ToCode(qBittorrent.Failure) },
            },
            _ => null,
        };

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

    private static int ToStatusCode(QBittorrentFailure failure) => failure switch
    {
        // Depot has to be set up first.
        QBittorrentFailure.NotConfigured => StatusCodes.Status409Conflict,
        QBittorrentFailure.TorrentNotFound => StatusCodes.Status404NotFound,
        QBittorrentFailure.TorrentRejected => StatusCodes.Status422UnprocessableEntity,
        // The upstream server failed, not Depot nor the request: 502 Bad Gateway.
        _ => StatusCodes.Status502BadGateway,
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
