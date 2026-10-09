using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Workspaces;
using Opportunity.Contracts.Api;
using Opportunity.Data.Workspaces;
using Opportunity.Security.Authorization;

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
        StatusCodes.Status423Locked => ProblemCodes.PreservationLocked,
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
/// A delete the database refused because the workspace is under a preservation lock (SQLSTATE O0423, V0048) becomes
/// 423 <c>preservation-locked</c> with a <c>Workspace.DeletionBlocked</c> audit event, whichever endpoint ran it, so a
/// delete path added later answers the same way without code of its own (E20-T01, ADR-014 §2.3).
/// </summary>
internal sealed partial class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (PreservationLockViolation.TryGet(exception, out var locked))
        {
            await AuditBlockedAsync(httpContext, locked!, cancellationToken).ConfigureAwait(false);
            httpContext.Response.StatusCode = StatusCodes.Status423Locked;
            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status423Locked,
                Detail = "This workspace is under a legal hold (preservation lock). Nothing in it can be deleted or purged until every hold is released.",
            };
            problem.Extensions[Problems.CodeExtension] = ProblemCodes.PreservationLocked;
            return await problemDetails.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                Exception = exception,
                ProblemDetails = problem,
            }).ConfigureAwait(false);
        }

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
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status, Detail = detail },
        }).ConfigureAwait(false);
    }

    /// <summary>The refused attempt, in its own transaction (the refused one rolled back). IDs and the route only (ADR-013 §7).</summary>
    private async Task AuditBlockedAsync(HttpContext httpContext, PreservationLockedException locked, CancellationToken cancellationToken)
    {
        var workspaceId = locked.WorkspaceId != Guid.Empty ? locked.WorkspaceId : httpContext.GetWorkspaceAccess()?.WorkspaceId;
        if (workspaceId is null || httpContext.RequestServices.GetService<IAuditEventWriter>() is not { } audit)
        {
            return;
        }

        var principal = httpContext.ToSecurityPrincipal();
        var endpoint = httpContext.Features.Get<IExceptionHandlerFeature>()?.Endpoint ?? httpContext.GetEndpoint();
        var route = (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? httpContext.Request.Path.Value ?? string.Empty;
        try
        {
            await audit.WriteAsync(new AuditEvent
            {
                WorkspaceId = workspaceId,
                OccurredAt = DateTimeOffset.UtcNow,
                Category = AuditTaxonomy.Workspace.Category,
                Action = AuditTaxonomy.Workspace.DeletionBlocked,
                ActorType = principal.UserId == Guid.Empty ? AuditActorType.Service : AuditActorType.User,
                ActorId = principal.UserId == Guid.Empty ? "anonymous" : principal.UserId.ToString(),
                ActorDisplay = principal.DisplayName.Length > AuditEventRules.MaxActorDisplayLength
                    ? principal.DisplayName[..AuditEventRules.MaxActorDisplayLength]
                    : principal.DisplayName,
                ClientIp = principal.ClientIp,
                UserAgent = principal.UserAgent,
                ResourceType = "Workspace",
                ResourceId = workspaceId.Value.ToString(),
                Outcome = AuditOutcome.Denied,
                ReasonCode = "LegalHold",
                CorrelationId = principal.CorrelationId,
                Details = new Dictionary<string, string?>
                {
                    ["method"] = httpContext.Request.Method,
                    ["route"] = route.Length > 200 ? route[..200] : route,
                    ["target"] = locked.Target.Length > 64 ? locked.Target[..64] : locked.Target,
                },
            }, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The refusal stands whether or not its audit event could be written; the failure is logged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogAuditFailed(logger, ex, httpContext.TraceIdentifier);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Path} (trace {TraceIdentifier}).")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, PathString path, string traceIdentifier);

    [LoggerMessage(Level = LogLevel.Error, Message = "The audit event of a delete refused by a legal hold could not be written (trace {TraceIdentifier}).")]
    private static partial void LogAuditFailed(ILogger logger, Exception exception, string traceIdentifier);
}
