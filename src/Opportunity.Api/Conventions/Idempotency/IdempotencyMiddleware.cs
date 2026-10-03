using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Opportunity.Application.Idempotency;
using Opportunity.Contracts.Api;

namespace Opportunity.Api.Conventions.Idempotency;

public sealed class IdempotencyOptions
{
    public const string SectionName = "Idempotency";

    /// <summary>How long a completed key is replayed. ADR-019 §2.6 requires at least 24 h.</summary>
    [Range(typeof(TimeSpan), "1.00:00:00", "30.00:00:00")]
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>Endpoint metadata: the endpoint requires an <c>Idempotency-Key</c> header.</summary>
public sealed class RequiresIdempotencyKeyMetadata
{
    internal static readonly RequiresIdempotencyKeyMetadata Instance = new();

    private RequiresIdempotencyKeyMetadata()
    {
    }
}

public static class IdempotencyEndpointExtensions
{
    /// <summary>Marks a job-creating or bulk mutating <c>POST</c> as requiring <c>Idempotency-Key</c> (ADR-019 §2.6).</summary>
    public static TBuilder RequireIdempotencyKey<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(RequiresIdempotencyKeyMetadata.Instance);
}

/// <summary>
/// Enforces ADR-019 §2.6 on endpoints marked with <see cref="IdempotencyEndpointExtensions.RequireIdempotencyKey"/>:
/// missing key → 400, same key + same request → original response replayed, same key + different request → 422,
/// same key still executing → 409. Only successful (2xx) outcomes are recorded: a request that failed (4xx, 5xx or an
/// exception) created nothing, so the key is released and the client may retry or correct it with the same key.
/// </summary>
internal sealed class IdempotencyMiddleware(
    RequestDelegate next,
    IIdempotencyStore store,
    IProblemDetailsService problemDetails,
    IOptions<IdempotencyOptions> options,
    TimeProvider time)
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeaderName = "Idempotent-Replayed";
    public const int MaxKeyLength = 128;

    private static readonly string[] RecordedHeaders = ["Content-Type", "Location", "ETag"];

    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<RequiresIdempotencyKeyMetadata>() is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var header = context.Request.Headers[HeaderName];
        if (StringValues.IsNullOrEmpty(header))
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, ProblemCodes.IdempotencyKeyMissing,
                $"This operation requires an {HeaderName} header.").ConfigureAwait(false);
            return;
        }

        var key = header.ToString();
        if (header.Count != 1 || !IsValidKey(key))
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, ProblemCodes.IdempotencyKeyInvalid,
                $"{HeaderName} must be a single value of 1–{MaxKeyLength} printable ASCII characters.").ConfigureAwait(false);
            return;
        }

        var scope = new IdempotencyScope(
            Principal: context.User.FindFirst("sub")?.Value ?? context.User.Identity?.Name ?? "anonymous",
            WorkspaceId: context.Request.RouteValues.TryGetValue("workspaceId", out var ws) ? ws?.ToString() ?? "" : "",
            Route: $"{context.Request.Method} {(endpoint as RouteEndpoint)?.RoutePattern.RawText}",
            Key: key);

        var hash = await HashRequestAsync(context.Request).ConfigureAwait(false);
        var expiresAt = time.GetUtcNow() + options.Value.Retention;
        var begin = await store.TryBeginAsync(scope, hash, expiresAt, context.RequestAborted).ConfigureAwait(false);

        switch (begin.Outcome)
        {
            case IdempotencyBeginOutcome.Replay:
                await ReplayAsync(context, begin.Response!).ConfigureAwait(false);
                return;
            case IdempotencyBeginOutcome.Mismatch:
                await WriteProblemAsync(context, StatusCodes.Status422UnprocessableEntity, ProblemCodes.IdempotencyKeyReuse,
                    $"This {HeaderName} was already used with a different request.").ConfigureAwait(false);
                return;
            case IdempotencyBeginOutcome.InProgress:
                await WriteProblemAsync(context, StatusCodes.Status409Conflict, ProblemCodes.IdempotencyKeyInProgress,
                    $"A request with this {HeaderName} is still being processed; retry later.").ConfigureAwait(false);
                return;
        }

        await ExecuteAndRecordAsync(context, scope).ConfigureAwait(false);
    }

    private async Task ExecuteAndRecordAsync(HttpContext context, IdempotencyScope scope)
    {
        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch
        {
            context.Response.Body = originalBody;
            await store.ReleaseAsync(scope, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        context.Response.Body = originalBody;
        var response = context.Response;

        if (response.StatusCode is < StatusCodes.Status200OK or >= StatusCodes.Status300MultipleChoices)
        {
            await store.ReleaseAsync(scope, CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            var headers = RecordedHeaders
                .Where(name => response.Headers.ContainsKey(name))
                .ToDictionary(name => name, name => response.Headers[name].ToString(), StringComparer.OrdinalIgnoreCase);
            await store.CompleteAsync(scope, new IdempotentResponse(response.StatusCode, headers, buffer.ToArray()),
                CancellationToken.None).ConfigureAwait(false);
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task ReplayAsync(HttpContext context, IdempotentResponse recorded)
    {
        var response = context.Response;
        response.StatusCode = recorded.StatusCode;
        foreach (var (name, value) in recorded.Headers)
        {
            response.Headers[name] = value;
        }

        response.Headers[ReplayedHeaderName] = "true";
        await response.Body.WriteAsync(recorded.Body, context.RequestAborted).ConfigureAwait(false);
    }

    // Method, path, query and body; header order and whitespace in JSON are deliberately significant (byte equality).
    private static async Task<string> HashRequestAsync(HttpRequest request)
    {
        request.EnableBuffering();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{request.Method}\n{request.Path}{request.QueryString}\n"));

        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, request.HttpContext.RequestAborted).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(chunk, 0, read);
        }

        request.Body.Position = 0;
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static bool IsValidKey(string key) =>
        key.Length is > 0 and <= MaxKeyLength && key.All(c => c is >= '\x21' and <= '\x7e');

    private ValueTask WriteProblemAsync(HttpContext context, int status, string code, string detail)
    {
        context.Response.StatusCode = status;
        return problemDetails.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails =
            {
                Status = status,
                Detail = detail,
                Extensions = { [Problems.CodeExtension] = code },
            },
        });
    }
}
