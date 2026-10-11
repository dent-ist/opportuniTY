using System.Diagnostics;
using System.Globalization;

using Opportunity.Api.Conventions;
using Opportunity.Api.Exports;
using Opportunity.Application.Audit;
using Opportunity.Application.ListExports;
using Opportunity.Application.Storage;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Production.ListExports;
using Opportunity.Security.Authorization;
using Opportunity.Security.Http;

namespace Opportunity.Api.Content;

/// <summary>
/// A document list export's CSV through the protected-content gateway (#181):
/// <c>GET …/list-exports/{exportId}/content</c>. PEP-1 requires <c>Document.ExportList</c>; the export must be the
/// caller's own and completed (its documents were re-authorized when it ran, Q-15). Every download writes a durable
/// <c>Export.ListDownloaded</c> before the first byte; the file is streamed part by part, never held whole in memory.
/// </summary>
public sealed class ListExportContentEndpoints : IApiEndpointModule
{
    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Workspace.MapGet(ListExportEndpoints.ListExportsPath + "/{exportId}/content", DownloadAsync)
            .WithName("DownloadListExport")
            .WithTags("Exports")
            .WithMetadata(ProtectedContentEndpointMetadata.Instance)
            .RequirePermission(Permission.DocumentExportList)
            .WithSummary("The completed list export as CSV (UTF-8 with BOM), one row per document. Audited (Export.ListDownloaded).")
            .Produces<Stream>(StatusCodes.Status200OK, "text/csv")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    internal static async Task<IResult> DownloadAsync(
        string workspaceId, string exportId, HttpContext context, ListExportService service, IListExportStore store, IAuditEventWriter audit,
        TimeProvider time, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(exportId, out var id)
            || await service.GetOwnAsync(access.Principal, access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } export)
        {
            return Problems.NotFound("No such list export.");
        }

        if (export.Status != ListExportStatus.Completed)
        {
            return Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict,
                export.Status == ListExportStatus.Failed ? "The export failed; export the list again." : "The export has not finished yet.");
        }

        var principal = access.Principal;
        var downloaded = new AuditEvent
        {
            WorkspaceId = access.WorkspaceId,
            OccurredAt = time.GetUtcNow(),
            Category = AuditTaxonomy.Export.Category,
            Action = AuditTaxonomy.Export.ListDownloaded,
            ActorType = AuditActorType.User,
            ActorId = principal.UserId.ToString(),
            ActorDisplay = string.IsNullOrEmpty(principal.DisplayName) ? principal.UserId.ToString() : principal.DisplayName,
            ClientIp = principal.ClientIp,
            UserAgent = principal.UserAgent,
            CorrelationId = principal.CorrelationId ?? Activity.Current?.TraceId.ToHexString(),
            ResourceType = AuditTaxonomy.Export.ListResourceType,
            ResourceId = export.ExportId.ToString(),
            Outcome = AuditOutcome.Success,
            JobId = export.JobId,
            SnapshotId = export.SnapshotId,
            Details = new Dictionary<string, string?>
            {
                ["ExportId"] = export.ExportId.ToString(),
                ["Rows"] = export.RowCount?.ToString(CultureInfo.InvariantCulture),
                ["Bytes"] = export.ByteCount?.ToString(CultureInfo.InvariantCulture),
                ["Sha256"] = export.ContentSha256,
            },
        };
        await audit.WriteAsync(downloaded, cancellationToken).ConfigureAwait(false);

        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers[ProtectedContentGateway.RetrievalIdHeader] = downloaded.EventId.ToString();
        response.Headers.ContentDisposition = ContentDispositionHeader.Attachment(ListExportCsv.FileName(export.ExportId, export.CreatedAt));
        response.Headers.ContentSecurityPolicy = ApiContentSecurityPolicies.ProtectedContent;
        response.Headers.XContentTypeOptions = "nosniff";
        if (export.ByteCount is { } length)
        {
            response.ContentLength = length;
        }

        return TypedResults.Stream(async output =>
        {
            await output.WriteAsync(ListExportCsv.Header(export.Columns), context.RequestAborted).ConfigureAwait(false);
            await store.ReadPartsAsync(export.WorkspaceId, export.ExportId,
                part => output.WriteAsync(part, context.RequestAborted), context.RequestAborted).ConfigureAwait(false);
        }, ListExportCsv.ContentType);
    }
}

public static class ListExportContentRegistration
{
    public static IServiceCollection AddListExportContentEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IApiEndpointModule, ListExportContentEndpoints>();
        return services;
    }
}
