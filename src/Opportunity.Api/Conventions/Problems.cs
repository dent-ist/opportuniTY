using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Opportunity.Contracts.Api;

namespace Opportunity.Api.Conventions;

/// <summary>RFC 9457 problem responses with ADR-019 §2.4 <c>type</c>, <c>code</c> and <c>traceId</c>.</summary>
public static class Problems
{
    public const string CodeExtension = "code";
    public const string TraceIdExtension = "traceId";

    public static ProblemHttpResult Create(int statusCode, string code, string? detail = null) =>
        TypedResults.Problem(
            statusCode: statusCode,
            detail: detail,
            type: ProblemCodes.TypeFor(code),
            extensions: new Dictionary<string, object?> { [CodeExtension] = code });

    public static ValidationProblem Validation(IDictionary<string, string[]> errors, string? detail = null) =>
        TypedResults.ValidationProblem(
            errors,
            detail: detail,
            type: ProblemCodes.TypeFor(ProblemCodes.Validation),
            extensions: new Dictionary<string, object?> { [CodeExtension] = ProblemCodes.Validation });

    public static ProblemHttpResult NotFound(string? detail = null) =>
        Create(StatusCodes.Status404NotFound, ProblemCodes.NotFound, detail);

    /// <summary>Applied to every problem the app writes, including status-code pages and exception handling.</summary>
    internal static void Customize(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;
        var status = problem.Status ?? context.HttpContext.Response.StatusCode;
        problem.Status = status;

        var code = problem.Extensions.TryGetValue(CodeExtension, out var value) && value is string existing
            ? existing
            : problem is HttpValidationProblemDetails ? ProblemCodes.Validation : CodeFor(status);

        problem.Extensions[CodeExtension] = code;
        problem.Type = ProblemCodes.TypeFor(code);
        problem.Title ??= ReasonPhrases.GetReasonPhrase(status);
        problem.Instance = null;
        problem.Extensions[TraceIdExtension] =
            Activity.Current?.TraceId.ToHexString() ?? context.HttpContext.TraceIdentifier;

        // Never leak exception details, even if a developer exception page is in the pipeline.
        problem.Extensions.Remove("exception");
    }

    internal static string CodeFor(int status) => status switch
    {
        StatusCodes.Status400BadRequest => ProblemCodes.BadRequest,
        StatusCodes.Status401Unauthorized => ProblemCodes.Unauthorized,
        StatusCodes.Status403Forbidden => ProblemCodes.Forbidden,
        StatusCodes.Status404NotFound => ProblemCodes.NotFound,
        StatusCodes.Status405MethodNotAllowed => ProblemCodes.MethodNotAllowed,
        StatusCodes.Status406NotAcceptable => ProblemCodes.NotAcceptable,
        StatusCodes.Status409Conflict => ProblemCodes.Conflict,
        StatusCodes.Status410Gone => ProblemCodes.Gone,
        StatusCodes.Status412PreconditionFailed => ProblemCodes.VersionConflict,
        StatusCodes.Status413PayloadTooLarge => ProblemCodes.PayloadTooLarge,
        StatusCodes.Status415UnsupportedMediaType => ProblemCodes.UnsupportedMediaType,
        StatusCodes.Status422UnprocessableEntity => ProblemCodes.UnprocessableContent,
        StatusCodes.Status428PreconditionRequired => ProblemCodes.PreconditionRequired,
        StatusCodes.Status429TooManyRequests => ProblemCodes.RateLimited,
        StatusCodes.Status503ServiceUnavailable => ProblemCodes.ServiceUnavailable,
        >= 500 => ProblemCodes.Internal,
        _ => ProblemCodes.BadRequest,
    };
}

/// <summary>
/// Turns unhandled exceptions into problem details without exposing messages or stack traces. Server errors are
/// logged here: since .NET 10 the exception handler middleware does not log exceptions an IExceptionHandler handled.
/// </summary>
internal sealed partial class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, detail) = exception switch
        {
            BadHttpRequestException bad => (bad.StatusCode, "The request could not be read."),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred."),
        };
        if (status >= StatusCodes.Status500InternalServerError)
        {
            LogUnhandled(logger, exception, httpContext.Request.Method, httpContext.Request.Path, httpContext.TraceIdentifier);
        }

        httpContext.Response.StatusCode = status;
        return problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status, Detail = detail },
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Path} (trace {TraceIdentifier}).")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, PathString path, string traceIdentifier);
}
