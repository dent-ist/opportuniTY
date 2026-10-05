using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Exports;
using Opportunity.Application.Fields;
using Opportunity.Application.Jobs;
using Opportunity.Application.Snapshots;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Core.Snapshots;

namespace Opportunity.Production.Exports;

public enum ExportCreateStatus
{
    Accepted,
    Invalid,

    /// <summary>The snapshot does not exist or is not visible to the caller.</summary>
    NotFound,
}

public sealed record ExportCreateOutcome(ExportCreateStatus Status, ExportCreation? Creation = null, IReadOnlyDictionary<string, string[]>? Errors = null);

/// <summary>
/// Creates exports (E12-T01): validates the request against the field catalogue (fields the caller may not see do not
/// exist for them), checks that the frozen set is a Ready <c>Export</c> snapshot the caller may see, and stores the
/// export with its Export job and <c>Export.Created</c>. The endpoint has already required <c>Export.Create</c>.
/// </summary>
public sealed class ExportService(
    IExportStore exports,
    IDocumentSetSnapshotStore snapshots,
    IFieldCatalogRepository fields,
    IFieldAccessFilter fieldAccess,
    IAuthorizationService authorization,
    TimeProvider time)
{
    public async Task<ExportCreateOutcome> CreateAsync(
        SecurityPrincipal principal, Guid workspaceId, CreateExportRequest request, string? clientIdempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = await snapshots.GetAsync(workspaceId, request.SnapshotId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null
            || (snapshot.CreatedBy != principal.UserId
                && !(await authorization.AuthorizeAsync(principal, workspaceId, Permission.JobViewAll, cancellationToken).ConfigureAwait(false)).IsAllowed))
        {
            return new ExportCreateOutcome(ExportCreateStatus.NotFound);
        }

        if (!SnapshotStrategyRules.AcceptsSnapshot(SetOperationKind.Export, snapshot.Purpose) || snapshot.Status != SnapshotStatus.Ready)
        {
            return Invalid("snapshotId", "Export a Ready frozen set created for export.");
        }

        var name = string.IsNullOrWhiteSpace(request.Name) ? SnapshotRules.DefaultName(SnapshotPurpose.Export, time.GetUtcNow()) : request.Name.Trim();
        if (name.Length > ExportSettingsRules.MaxNameLength || name.Any(char.IsControl))
        {
            return Invalid("name", $"A name has 1 to {ExportSettingsRules.MaxNameLength} characters and no control characters.");
        }

        var catalog = await fields.GetCatalogAsync(workspaceId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var restricted = await fieldAccess.RestrictedFieldIdsAsync(workspaceId, principal, catalog, cancellationToken).ConfigureAwait(false);
        if (ExportSettingsRules.Normalize(request, catalog, restricted, out var errors) is not { } settings)
        {
            return new ExportCreateOutcome(ExportCreateStatus.Invalid, Errors: errors);
        }

        var exportId = Guid.CreateVersion7();
        var creation = await exports.CreateAsync(new NewExport
        {
            WorkspaceId = workspaceId,
            ExportId = exportId,
            SnapshotId = snapshot.SnapshotId,
            Name = name,
            SettingsJson = settings.Serialize(),
            InitiatedBy = principal.UserId,
            InitiatedByDisplay = string.IsNullOrEmpty(principal.DisplayName) ? principal.UserId.ToString() : principal.DisplayName,
            InitiatedByGroups = principal.Groups,
            ClientIdempotencyKey = clientIdempotencyKey,
            CorrelationId = principal.CorrelationId,
            Parameters = new JsonObject { ["exportId"] = exportId.ToString(), ["snapshotId"] = snapshot.SnapshotId.ToString() },
            AuditTemplate = new AuditEvent
            {
                OccurredAt = time.GetUtcNow(),
                Category = AuditTaxonomy.Export.Category,
                Action = AuditTaxonomy.Export.Created,
                ActorType = AuditActorType.User,
                ActorId = principal.UserId.ToString(),
                ActorDisplay = string.IsNullOrEmpty(principal.DisplayName) ? principal.UserId.ToString() : principal.DisplayName,
                ClientIp = principal.ClientIp,
                UserAgent = principal.UserAgent,
                CorrelationId = principal.CorrelationId,
                Outcome = AuditOutcome.Success,
                Details = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["ExportId"] = exportId.ToString(),
                    ["SnapshotDocuments"] = snapshot.DocumentCount?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["Volume"] = settings.VolumeName,
                    ["Columns"] = settings.Columns.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["Natives"] = settings.IncludeNatives ? "true" : "false",
                    ["Text"] = settings.IncludeText ? "true" : "false",
                    ["Images"] = settings.IncludeImages ? "true" : "false",
                },
            },
        }, cancellationToken).ConfigureAwait(false);
        return new ExportCreateOutcome(ExportCreateStatus.Accepted, creation);
    }

    private static ExportCreateOutcome Invalid(string key, string message) =>
        new(ExportCreateStatus.Invalid, Errors: new Dictionary<string, string[]> { [key] = [message] });
}

public static class ExportRegistration
{
    /// <summary>
    /// The export job services of the export worker: the chunk executor, the coordinator and (with
    /// <paramref name="runCoordinator"/>) its polling service. The host registers the stores, the PDP and object storage.
    /// </summary>
    public static IServiceCollection AddExportJobs(this IServiceCollection services, ExportJobOptions? options = null, bool runCoordinator = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(options ?? new ExportJobOptions());
        services.TryAddScoped<ExportCoordinator>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IJobChunkExecutor, ExportChunkExecutor>());
        if (runCoordinator)
        {
            services.AddHostedService<ExportCoordinatorService>();
        }

        return services;
    }

    /// <summary>The API side: <see cref="ExportService"/> (the host registers the stores and the PDP).</summary>
    public static IServiceCollection AddExportService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ExportService>();
        return services;
    }
}
