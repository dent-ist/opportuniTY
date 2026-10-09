using System.Globalization;
using System.Text.Json.Nodes;
using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Exports;
using Opportunity.Application.Productions;
using Opportunity.Core.Security;
using Opportunity.Production.Productions;

namespace Opportunity.Production.Volumes;

public enum ProductionVolumeStartStatus
{
    Accepted,
    NotFound,

    /// <summary>Only a finalized production is written to a volume (409).</summary>
    Conflict,
}

public sealed record ProductionVolumeStart(ProductionVolumeStartStatus Status, ExportCreation? Creation = null, string? Reason = null);

/// <summary>
/// Starts production volume runs (E12-T05) behind <c>POST …/productions/{id}/volumes</c>; the endpoint has already
/// required <c>Production.Create</c>. A run is a Production job over the production's frozen members; the rendering
/// worker writes it in chunks (<see cref="ProductionVolumeChunkExecutor"/>) and assembles the load files and manifest
/// (<see cref="ProductionVolumeCoordinator"/>). The first run is audited <c>Production.Run</c>, later ones
/// <c>Production.Rerun</c>: every run of the same production writes the same bytes.
/// </summary>
public sealed class ProductionVolumeService(IProductionStore productions, IExportStore exports, TimeProvider time)
{
    public async Task<ProductionVolumeStart> StartAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid productionId, string? clientIdempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (await productions.GetAsync(workspaceId, productionId, cancellationToken).ConfigureAwait(false) is not { } production)
        {
            return new ProductionVolumeStart(ProductionVolumeStartStatus.NotFound);
        }

        if (production.Status != ProductionStatus.Finalized)
        {
            return new ProductionVolumeStart(ProductionVolumeStartStatus.Conflict,
                Reason: "Only a finalized production is written to a volume: finalize it first (a voided production is never produced again).");
        }

        var specification = ProductionSpecificationRules.Deserialize(production.SpecificationJson);
        var settings = ProductionVolumeSettings.ExportSettingsOf(specification);
        var earlier = await exports.ListVolumesAsync(workspaceId, productionId, null, 1, cancellationToken).ConfigureAwait(false);
        var exportId = Guid.CreateVersion7();
        var display = string.IsNullOrEmpty(principal.DisplayName) ? principal.UserId.ToString() : principal.DisplayName;
        var creation = await exports.CreateAsync(new NewExport
        {
            WorkspaceId = workspaceId,
            ExportId = exportId,
            SnapshotId = production.SnapshotId,
            ProductionId = productionId,
            Name = Truncate(production.Name + " – " + settings.VolumeName, 200),
            SettingsJson = settings.Serialize(),
            InitiatedBy = principal.UserId,
            InitiatedByDisplay = display,
            InitiatedByGroups = principal.Groups,
            ClientIdempotencyKey = clientIdempotencyKey,
            CorrelationId = principal.CorrelationId,
            Parameters = new JsonObject
            {
                ["productionId"] = productionId.ToString(),
                ["volumeId"] = exportId.ToString(),
                ["step"] = "volume",
            },
            AuditTemplate = new AuditEvent
            {
                OccurredAt = time.GetUtcNow(),
                Category = AuditTaxonomy.Production.Category,
                Action = earlier.Count == 0 ? AuditTaxonomy.Production.Run : AuditTaxonomy.Production.Rerun,
                ActorType = AuditActorType.User,
                ActorId = principal.UserId.ToString(),
                ActorDisplay = display,
                ClientIp = principal.ClientIp,
                UserAgent = principal.UserAgent,
                CorrelationId = principal.CorrelationId,
                Outcome = AuditOutcome.Success,
                Details = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["ProductionId"] = productionId.ToString(),
                    ["VolumeId"] = exportId.ToString(),
                    ["Volume"] = settings.VolumeName,
                    ["ManifestSha256"] = production.ManifestSha256 is { } sha ? Convert.ToHexStringLower(sha) : null,
                    ["Documents"] = production.BatesDocuments?.ToString(CultureInfo.InvariantCulture),
                },
            },
        }, cancellationToken).ConfigureAwait(false);
        return new ProductionVolumeStart(ProductionVolumeStartStatus.Accepted, creation);
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
