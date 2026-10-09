using System.Globalization;
using System.Text;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Productions;
using Opportunity.Contracts.Api;
using Opportunity.Core.Productions;
using Opportunity.Import.LoadFiles;
using Opportunity.Production.Exports;

namespace Opportunity.Production.Productions;

/// <summary>A page of the re-designation report: changed members the caller may view, the restricted count and where to continue.</summary>
public sealed record RedesignationReport(IReadOnlyList<RedesignationRow> Rows, long Restricted, long? NextAfter);

/// <summary>
/// Confidentiality designations of productions (E12-T04): per-member listing, overrides with an audited reason, and the
/// re-designation report and overlay load file of a finalized production whose documents were re-designated since.
/// </summary>
public sealed partial class ProductionService
{
    public const int MaxOverrideReasonLength = 2000;

    /// <summary>Members scanned per store call by the re-designation report.</summary>
    private const int RedesignationScan = 1_000;

    /// <summary>Members one report request scans at most before it answers with a cursor.</summary>
    private const int RedesignationScanBudget = 50_000;

    /// <summary>What decides the designations of <paramref name="production"/>, from its stored specification.</summary>
    public static DesignationPlan PlanOf(ProductionRecord production)
    {
        ArgumentNullException.ThrowIfNull(production);
        var specification = ProductionSpecificationRules.Deserialize(production.SpecificationJson);
        return specification.Designations is null
            ? DesignationPlan.None with { StampsDesignation = ProductionSpecificationRules.StampsDesignation(specification) }
            : new DesignationPlan(specification.Designations.FieldId, ProductionSpecificationRules.LevelsOf(specification),
                ProductionSpecificationRules.RuleOf(specification), ProductionSpecificationRules.StampsDesignation(specification));
    }

    /// <summary>A page of the members' designations (computed for a draft, frozen once finalized); documents the caller may not view are left out and counted.</summary>
    public async Task<(IReadOnlyList<DesignationRow> Rows, long Restricted, long? LastSequence)> ListDesignationsAsync(
        SecurityPrincipal principal, ProductionRecord production, long afterSequence, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(production);
        var rows = await productions.ReadDesignationsAsync(production.WorkspaceId, production.ProductionId, PlanOf(production), afterSequence, limit,
            cancellationToken).ConfigureAwait(false);
        var visible = await VisibleAsync(principal, production.WorkspaceId, rows.Select(r => r.DocumentId), cancellationToken).ConfigureAwait(false);
        return ([.. rows.Where(r => visible.Contains(r.DocumentId))], rows.Count(r => !visible.Contains(r.DocumentId)),
            rows.Count > 0 ? rows[^1].Sequence : null);
    }

    /// <summary>
    /// Sets a draft member's designation for this production, overriding the family rule, with a reason (audited
    /// <c>Production.DesignationOverridden</c>). A document the caller may not view answers like a missing one.
    /// </summary>
    public async Task<(ProductionOutcome Outcome, DesignationOverrideRow? Override)> OverrideDesignationAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid productionId, Guid documentId, DesignationOverrideRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > MaxOverrideReasonLength || reason.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
        {
            return (ProductionOutcome.Invalid("reason", $"Give the reason for overriding the designation (1 to {MaxOverrideReasonLength} characters)."), null);
        }

        var (production, plan, refusal) = await DesignationTargetAsync(principal, workspaceId, productionId, documentId, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return (refusal, null);
        }

        var level = request.ChoiceId is { } choice ? plan!.Levels.FirstOrDefault(l => l.ChoiceId == choice) : null;
        if (request.ChoiceId is not null && level is null)
        {
            return (ProductionOutcome.Invalid("choiceId", "Use a designation level of the production's specification (or null for none)."), null);
        }

        var previous = await productions.GetDesignationOverrideAsync(workspaceId, productionId, documentId, cancellationToken).ConfigureAwait(false);
        var audit = UserEvent(principal, AuditTaxonomy.Production.DesignationOverridden, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ProductionId"] = productionId.ToString(),
            ["DocumentId"] = documentId.ToString(),
            ["FieldId"] = plan!.FieldId!.Value.ToString(CultureInfo.InvariantCulture),
            ["ChoiceId"] = request.ChoiceId?.ToString(CultureInfo.InvariantCulture),
            ["Legend"] = level?.Legend ?? string.Empty,
            ["PreviousChoiceId"] = previous?.ChoiceId?.ToString(CultureInfo.InvariantCulture),
            ["Reason"] = reason.Length <= 500 ? reason : reason[..500],
        });
        var status = await productions.SetDesignationOverrideAsync(workspaceId, productionId, documentId, plan.FieldId.Value, request.ChoiceId, reason,
            principal.UserId, remove: false, audit, cancellationToken).ConfigureAwait(false);
        return status switch
        {
            DesignationOverrideStatus.Applied => (new ProductionOutcome(ProductionOutcomeStatus.Ok, production),
                await productions.GetDesignationOverrideAsync(workspaceId, productionId, documentId, cancellationToken).ConfigureAwait(false)),
            DesignationOverrideStatus.Frozen => (new ProductionOutcome(ProductionOutcomeStatus.Conflict, production,
                Reason: "Only a draft's designations are overridden; a finalized production is frozen."), null),
            DesignationOverrideStatus.NotMember => (ProductionOutcome.Invalid("documentId", "The document is not in the production's frozen set."), null),
            _ => (new ProductionOutcome(ProductionOutcomeStatus.NotFound), null),
        };
    }

    /// <summary>Removes a draft member's override: the family rule applies again (audited <c>Production.DesignationOverrideRemoved</c>).</summary>
    public async Task<ProductionOutcome> RemoveDesignationOverrideAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid productionId, Guid documentId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var (production, _, refusal) = await DesignationTargetAsync(principal, workspaceId, productionId, documentId, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        var previous = await productions.GetDesignationOverrideAsync(workspaceId, productionId, documentId, cancellationToken).ConfigureAwait(false);
        if (previous is null)
        {
            return new ProductionOutcome(ProductionOutcomeStatus.NotFound);
        }

        var audit = UserEvent(principal, AuditTaxonomy.Production.DesignationOverrideRemoved, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ProductionId"] = productionId.ToString(),
            ["DocumentId"] = documentId.ToString(),
            ["PreviousChoiceId"] = previous.ChoiceId?.ToString(CultureInfo.InvariantCulture),
        });
        var status = await productions.SetDesignationOverrideAsync(workspaceId, productionId, documentId, previous.FieldId, null, previous.Reason,
            principal.UserId, remove: true, audit, cancellationToken).ConfigureAwait(false);
        return status switch
        {
            DesignationOverrideStatus.Applied => new ProductionOutcome(ProductionOutcomeStatus.Ok, production),
            DesignationOverrideStatus.Frozen => new ProductionOutcome(ProductionOutcomeStatus.Conflict, production,
                Reason: "Only a draft's designations are overridden; a finalized production is frozen."),
            _ => new ProductionOutcome(ProductionOutcomeStatus.NotFound),
        };
    }

    /// <summary>
    /// The re-designation report of a finalized (or voided) production: produced members whose designation under the
    /// production's rule and overrides would now differ from the one they were produced with. Documents the caller may
    /// not view are counted, never listed (Q-52).
    /// </summary>
    public async Task<(ProductionOutcome Outcome, RedesignationReport? Report)> RedesignationReportAsync(
        SecurityPrincipal principal, ProductionRecord production, long afterSequence, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(production);
        if (await RedesignationRefusalAsync(production, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return (refusal, null);
        }

        var plan = PlanOf(production);
        var rows = new List<RedesignationRow>();
        long restricted = 0;
        var after = afterSequence;
        var scanned = 0;
        while (rows.Count < limit && scanned < RedesignationScanBudget)
        {
            var page = await productions.ReadRedesignationsAsync(production.WorkspaceId, production.ProductionId, plan, after, RedesignationScan,
                cancellationToken).ConfigureAwait(false);
            if (page.LastScanned is not { } last)
            {
                return (new ProductionOutcome(ProductionOutcomeStatus.Ok, production), new RedesignationReport(rows, restricted, null));
            }

            var visible = await VisibleAsync(principal, production.WorkspaceId, page.Rows.Select(r => r.DocumentId), cancellationToken).ConfigureAwait(false);
            foreach (var row in page.Rows)
            {
                if (rows.Count == limit)
                {
                    // The page is full: continue after the last listed row next time.
                    return (new ProductionOutcome(ProductionOutcomeStatus.Ok, production), new RedesignationReport(rows, restricted, rows[^1].Sequence));
                }

                if (visible.Contains(row.DocumentId))
                {
                    rows.Add(row);
                }
                else
                {
                    restricted++;
                }

                after = row.Sequence;
            }

            after = last;
            scanned += RedesignationScan;
        }

        return (new ProductionOutcome(ProductionOutcomeStatus.Ok, production), new RedesignationReport(rows, restricted, after));
    }

    /// <summary>The audit event of an overlay download (written before the first byte).</summary>
    public AuditEvent RedesignationExportAudit(SecurityPrincipal principal, ProductionRecord production)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(production);
        return Resource(UserEvent(principal, AuditTaxonomy.Production.RedesignationExported, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ProductionId"] = production.ProductionId.ToString(),
            ["Format"] = "dat",
        }), production);
    }

    /// <summary>Why no re-designation report or overlay exists for <paramref name="production"/>, else null.</summary>
    public async Task<ProductionOutcome?> RedesignationRefusalAsync(ProductionRecord production, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(production);
        if (production.Status is not (ProductionStatus.Finalized or ProductionStatus.Voided))
        {
            return new ProductionOutcome(ProductionOutcomeStatus.Conflict, production,
                Reason: "A re-designation report compares a finalized production with the current designations; this production is not finalized.");
        }

        var first = await productions.ReadDocumentsAsync(production.WorkspaceId, production.ProductionId, 0, 1, cancellationToken).ConfigureAwait(false);
        return first.Count == 1 && first[0].DesignationSource is null
            ? new ProductionOutcome(ProductionOutcomeStatus.Conflict, production,
                Reason: "This production was finalized before designations were recorded with productions, so there is nothing to compare.")
            : null;
    }

    /// <summary>
    /// Writes the overlay load file of the re-designation report: a DAT in the production's delimiters and encoding
    /// with ProdBegBates, ProdEndBates and the designation column (the production's own header for the designation
    /// field, if its load file has one) holding the legend each document should now carry. Only documents the caller
    /// may view are written. Returns the rows written and left out.
    /// </summary>
    public async Task<(long Written, long Restricted)> WriteRedesignationOverlayAsync(
        SecurityPrincipal principal, ProductionRecord production, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(production);
        ArgumentNullException.ThrowIfNull(output);
        var specification = ProductionSpecificationRules.Deserialize(production.SpecificationJson);
        var loadFile = specification.LoadFile ?? new ProductionLoadFileSettings();
        var profile = DelimiterProfile.TryGetPreset(loadFile.Delimiters ?? DelimiterProfile.Concordance.Name, out var preset) ? preset : DelimiterProfile.Concordance;
        var encoding = ExportSettingsRules.ParseDatEncoding(loadFile.Encoding) ?? LoadFileEncodingKind.Utf8;
        var plan = PlanOf(production);
        var header = await DesignationHeaderAsync(production.WorkspaceId, plan, loadFile, cancellationToken).ConfigureAwait(false);

        await output.WriteAsync(LoadFileEncodings.Preamble(encoding).ToArray(), cancellationToken).ConfigureAwait(false);
        await WriteAsync(LoadFileText.DatRow(profile, ["ProdBegBates", "ProdEndBates", header])).ConfigureAwait(false);
        long written = 0, restricted = 0, after = 0;
        while (true)
        {
            var page = await productions.ReadRedesignationsAsync(production.WorkspaceId, production.ProductionId, plan, after, RedesignationScan,
                cancellationToken).ConfigureAwait(false);
            if (page.LastScanned is not { } last)
            {
                break;
            }

            var visible = await VisibleAsync(principal, production.WorkspaceId, page.Rows.Select(r => r.DocumentId), cancellationToken).ConfigureAwait(false);
            var text = new StringBuilder();
            foreach (var row in page.Rows)
            {
                if (!visible.Contains(row.DocumentId))
                {
                    restricted++;
                    continue;
                }

                text.Append(LoadFileText.DatRow(profile, [row.ProdBegBates, row.ProdEndBates, row.CurrentLegend], [false, false, true]));
                written++;
            }

            await WriteAsync(text.ToString()).ConfigureAwait(false);
            after = last;
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return (written, restricted);

        Task WriteAsync(string value) => output.WriteAsync(LoadFileText.Encode(encoding, value), cancellationToken).AsTask();
    }

    /// <summary>The overlay's file name: <c>&lt;production name&gt;_redesignation.dat</c> (unsafe characters replaced).</summary>
    public static string OverlayFileName(ProductionRecord production)
    {
        ArgumentNullException.ThrowIfNull(production);
        var safe = new string([.. production.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')]).Trim('_', '.');
        return (safe.Length > 0 ? safe[..Math.Min(safe.Length, 80)] : "production") + "_redesignation.dat";
    }

    private async Task<string> DesignationHeaderAsync(Guid workspaceId, DesignationPlan plan, ProductionLoadFileSettings loadFile, CancellationToken cancellationToken)
    {
        if (plan.FieldId is { } fieldId)
        {
            if ((loadFile.Fields ?? []).FirstOrDefault(f => f.FieldId == fieldId)?.Header is { Length: > 0 } header)
            {
                return header;
            }

            var catalog = await fields.GetCatalogAsync(workspaceId, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (catalog.Find(fieldId)?.Name is { Length: > 0 } name)
            {
                return name;
            }
        }

        return "Confidentiality";
    }

    /// <summary>The draft, its plan with a designation field, and a document the caller may view; or why not.</summary>
    private async Task<(ProductionRecord? Production, DesignationPlan? Plan, ProductionOutcome? Refusal)> DesignationTargetAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid productionId, Guid documentId, CancellationToken cancellationToken)
    {
        if (await productions.GetAsync(workspaceId, productionId, cancellationToken).ConfigureAwait(false) is not { } production)
        {
            return (null, null, new ProductionOutcome(ProductionOutcomeStatus.NotFound));
        }

        if (!(await VisibleAsync(principal, workspaceId, [documentId], cancellationToken).ConfigureAwait(false)).Contains(documentId))
        {
            return (production, null, new ProductionOutcome(ProductionOutcomeStatus.NotFound));
        }

        if (production.Status != ProductionStatus.Draft)
        {
            return (production, null, new ProductionOutcome(ProductionOutcomeStatus.Conflict, production,
                Reason: "Only a draft's designations are overridden; a finalized production is frozen."));
        }

        var plan = PlanOf(production);
        if (plan.FieldId is null)
        {
            return (production, plan, new ProductionOutcome(ProductionOutcomeStatus.Conflict, production,
                Reason: "The production's specification has no designation field; there is nothing to override."));
        }

        return (production, plan, null);
    }
}
