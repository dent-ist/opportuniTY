using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Opportunity.Contracts.Api;

namespace Opportunity.Api.Conventions;

public static class ApiResults
{
    /// <summary>
    /// ADR-019 §2.5: a long-running operation answers <c>202 Accepted</c> with
    /// <c>Location: /api/v1/workspaces/{workspaceId}/jobs/{jobId}</c> and the job resource as body.
    /// </summary>
    public static Accepted<TJob> JobAccepted<TJob>(string workspaceId, string jobId, TJob job) =>
        TypedResults.Accepted(JobLocation(workspaceId, jobId), job);

    public static string JobLocation(string workspaceId, string jobId) =>
        $"{ApiRoutes.V1Prefix}/workspaces/{Uri.EscapeDataString(workspaceId)}/jobs/{Uri.EscapeDataString(jobId)}";
}

/// <summary>ADR-019 §2.7 optimistic concurrency: versioned resources return <c>ETag</c>, mutations send <c>If-Match</c>.</summary>
public static class EntityTags
{
    public static string ForVersion(long version) =>
        $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>
    /// Returns <c>null</c> when <c>If-Match</c> matches <paramref name="currentVersion"/>; otherwise the problem to
    /// return: 428 <c>precondition-required</c> when the header is missing, 412 <c>version-conflict</c> on mismatch.
    /// </summary>
    public static ProblemHttpResult? CheckIfMatch(HttpRequest request, long currentVersion)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ifMatch = request.Headers.IfMatch;
        if (StringValues.IsNullOrEmpty(ifMatch))
        {
            return Problems.Create(StatusCodes.Status428PreconditionRequired, ProblemCodes.PreconditionRequired,
                "This resource is versioned; send If-Match with its current ETag.");
        }

        var current = new EntityTagHeaderValue(ForVersion(currentVersion));
        var matches = EntityTagHeaderValue.TryParseList(ifMatch, out var tags)
            && tags.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(current, useStrongComparison: true));

        return matches
            ? null
            : Problems.Create(StatusCodes.Status412PreconditionFailed, ProblemCodes.VersionConflict,
                "The resource was modified since it was read.");
    }
}

/// <summary>
/// ADR-019 §2.8 query parameters for collections: <c>?limit=</c> (default 50, max 500) and opaque <c>?cursor=</c>.
/// Bind with <c>[AsParameters]</c>. Cursor encoding and its (user, workspace) binding come with the first paged
/// resource (E07).
/// </summary>
public readonly record struct PageQuery(int? Limit, string? Cursor)
{
    public int EffectiveLimit => Limit ?? Pagination.DefaultLimit;

    public ValidationProblem? Validate()
    {
        if (Limit is null or (>= 1 and <= Pagination.MaxLimit))
        {
            return null;
        }

        return Problems.Validation(new Dictionary<string, string[]>
        {
            ["limit"] = [$"limit must be between 1 and {Pagination.MaxLimit}."],
        });
    }
}
