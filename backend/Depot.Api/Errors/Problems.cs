using Microsoft.AspNetCore.Mvc;

namespace Depot.Api.Errors;

// Every error the API returns is problem details with a machine-readable "code" next to the
// readable "detail"; validation errors also carry "errors": { field: [message] }.
public static class Problems
{
    public static ProblemDetails Create(int status, string code, string detail, string? field = null)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = TitleFor(status),
            Detail = detail,
            Extensions = { ["code"] = code },
        };
        if (field is not null)
        {
            problem.Extensions["errors"] = new Dictionary<string, string[]> { [field] = [detail] };
        }

        return problem;
    }

    public static IResult Result(int status, string code, string detail, string? field = null) =>
        Results.Problem(Create(status, code, detail, field));

    public static Task WriteAsync(HttpContext context, int status, string code, string detail, string? field = null)
    {
        context.Response.StatusCode = status;
        return context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = Create(status, code, detail, field),
        }).AsTask();
    }

    private static string TitleFor(int status) => status switch
    {
        400 => "The request isn't valid.",
        401 or 403 => "The request wasn't allowed.",
        404 => "Not found.",
        409 => "Conflict.",
        422 => "The request couldn't be processed.",
        502 => "An external service failed.",
        _ => "Something went wrong.",
    };
}
