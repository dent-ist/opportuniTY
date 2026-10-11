using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Conventions;
using Opportunity.Application.Compliance;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Data.Compliance;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Compliance;

/// <summary>
/// Ethical-wall proof and workspace access review (E14-T06) as JSON for the Admin › Compliance screen. Each generation is
/// audited (<c>Security.WallProofGenerated</c>, <c>Security.AccessReviewGenerated</c>). The CSV downloads and the
/// verifiable audit export are gateway endpoints (<c>Content/ComplianceContentEndpoints</c>).
/// </summary>
public sealed class ComplianceEndpoints : IApiEndpointModule
{
    public const string WallProofPath = "/compliance/wall-proof";
    public const string AccessReviewPath = "/compliance/access-review";
    public const string AuditExportPath = "/compliance/audit-export";

    public const string Tag = "Compliance";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var ws = routes.Workspace;

        ws.MapGet(WallProofPath, WallProofAsync)
            .WithName("GetWallProof")
            .WithTags(Tag)
            .Produces<WallProofResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Ethical-wall proof for a period: every audited attempt by a walled user on walled content (expected: denials only). Audited.")
            .WithDescription("from (inclusive) and to (exclusive), ISO 8601; to defaults to now and from to 30 days before it. At most "
                + "500 attempts are listed (breaches first); the counts cover all. The CSV form lists up to 50,000.")
            .RequirePermission(Permission.ComplianceReport);

        ws.MapGet(AccessReviewPath, AccessReviewAsync)
            .WithName("GetAccessReview")
            .WithTags(Tag)
            .Produces<AccessReviewResource>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Workspace access review: users and groups with their roles, ethical walls, last workspace access and last sign-in. Audited.")
            .RequirePermission(Permission.ComplianceReport);
    }

    internal static async Task<IResult> WallProofAsync(
        string workspaceId,
        [FromQuery(Name = "from")] DateTimeOffset? from,
        [FromQuery(Name = "to")] DateTimeOffset? to,
        HttpContext context,
        ComplianceReportService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        if (service.Period(from, to) is not { } period)
        {
            return InvalidPeriod();
        }

        var report = await service.WallProofAsync(access.Principal, access.WorkspaceId, period, ComplianceReportFormat.Json, cancellationToken)
            .ConfigureAwait(false);
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(ToResource(report));
    }

    internal static async Task<IResult> AccessReviewAsync(
        string workspaceId, HttpContext context, ComplianceReportService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var report = await service.AccessReviewAsync(access.Principal, access.WorkspaceId, ComplianceReportFormat.Json, cancellationToken)
            .ConfigureAwait(false);
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(ToResource(report));
    }

    internal static IResult InvalidPeriod() =>
        Problems.Validation(new Dictionary<string, string[]> { ["from"] = ["from must be before to."] });

    private static WallProofResource ToResource(WallProofReport report)
    {
        var data = report.Data;
        return new WallProofResource(
            report.From,
            report.To,
            report.GeneratedAt,
            report.Held,
            new WallProofCountsResource(data.Counts.Attempts, data.Counts.Denied, data.Counts.BreakGlass, data.Counts.Allowed, data.Counts.Failed),
            [.. data.Walls.Select(w => new WallProofWallResource(
                w.WallId, w.Name, w.CreatedAt, [.. w.Members.Select(m => new WallProofMemberResource(m.UserId, m.GroupName, m.DisplayName, m.AddedAt))],
                w.DocumentsCovered))],
            [.. data.Attempts.Select(a => new WallProofAttemptResource(
                a.EventId, a.OccurredAt, a.Result.ToString(), Guid.TryParse(a.ActorId, out var user) ? user : Guid.Empty, a.ActorDisplay,
                a.DocumentId, a.WallIds, a.Category + "." + a.Action, a.Outcome, a.ReasonCode, a.AccessPath, a.CorrelationId))],
            data.Truncated,
            [.. data.Changes.Select(c => new WallProofChangeResource(c.EventId, c.OccurredAt, c.Action, c.ActorDisplay, c.WallId))]);
    }

    private static AccessReviewResource ToResource(AccessReviewReport report) => new(
        report.GeneratedAt,
        report.Users,
        report.Groups,
        [.. report.Entries.Select(e => new AccessReviewEntryResource(
            e.Principal.ToString(),
            e.UserId,
            e.GroupName,
            e.DisplayName,
            e.Email,
            [.. e.Roles.Select(r => new AccessReviewRoleResource(r.Role.Key(), RoleCatalog.Get(r.Role).DisplayName, r.ViaGroup, r.AssignedAt))],
            [.. e.Walls.Select(w => new AccessReviewWallResource(w.WallId, w.Name, w.ViaGroup))],
            e.LastAccessAt,
            e.LastSignInAt,
            e.KnownMembers))]);
}

public static class ComplianceEndpointRegistration
{
    /// <summary>The compliance report endpoints, their use case and the PostgreSQL store.</summary>
    public static IServiceCollection AddComplianceEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddPostgresComplianceReports();
        services.TryAddScoped<ComplianceReportService>();
        services.AddSingleton<IApiEndpointModule, ComplianceEndpoints>();
        return services;
    }
}
