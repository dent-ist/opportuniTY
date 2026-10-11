using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Coding;
using Opportunity.Application.Fields;
using Opportunity.Application.Jobs;
using Opportunity.Application.Productions;
using Opportunity.Application.Snapshots;
using Opportunity.Contracts.Api;
using Opportunity.Core.Jobs;
using Opportunity.Core.Productions;
using Opportunity.Core.Security;
using Opportunity.Core.Snapshots;

namespace Opportunity.Production.Productions;

public enum ProductionOutcomeStatus
{
    Ok,
    Invalid,
    NotFound,

    /// <summary>The production's state does not allow the operation (409).</summary>
    Conflict,

    /// <summary>If-Match did not match (412).</summary>
    VersionConflict,

    /// <summary>The Bates start number or range overlaps another production's live range of the prefix (409).</summary>
    BatesConflict,

    /// <summary>Members are coded Privilege Status = Withhold (E13-T01); the production cannot be finalized (409).</summary>
    PrivilegeWithheld,

    /// <summary>Members' families or duplicates have unresolved privilege conflicts and no override was given (E13-T02, 409).</summary>
    PrivilegeConflicts,

    /// <summary>The caller lacks a permission the request needs beyond the endpoint's (403).</summary>
    Forbidden,

    /// <summary>A member's designation cannot be produced as specified (E12-T04); the production cannot be finalized (409).</summary>
    DesignationRefused,

    /// <summary>The QC gate blocked the finalization (E12-T07, 409 with the QC report).</summary>
    QcBlocked,
}

public sealed record ProductionOutcome(
    ProductionOutcomeStatus Status,
    ProductionRecord? Production = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Reason = null,
    JobInfo? Job = null,
    ProductionQcResult? Qc = null)
{
    public static ProductionOutcome Invalid(string key, string message) =>
        new(ProductionOutcomeStatus.Invalid, Errors: new Dictionary<string, string[]> { [key] = [message] });
}

/// <summary>Result of <see cref="ProductionService.VerifyAsync"/>.</summary>
public sealed record ProductionVerification(bool Consistent, IReadOnlyList<ProductionDifference> Differences, BatesIntegrityReport Integrity);

/// <summary>
/// Production use cases (E12-T02/T03) behind <c>/productions</c>; the endpoints have already required
/// <c>Production.Create</c> (or <c>Production.Finalize</c> to finalize and void). A production is a frozen set of
/// purpose Production plus a validated, canonical specification; drafts change freely (each change releases an
/// allocation), Bates numbers are allocated by a job, and finalization freezes everything into a manifest. A finalized
/// production never changes (the database refuses it); a change is a new version.
/// </summary>
public sealed partial class ProductionService(
    IProductionStore productions,
    IDocumentSetSnapshotStore snapshots,
    IFieldCatalogRepository fields,
    IFieldAccessFilter fieldAccess,
    IAuthorizationService authorization,
    IJobRepository jobs,
    ICodingRepository codingStore,
    TimeProvider time,
    ILogger<ProductionService> logger)
{
    public const int MaxNameLength = 200;

    public const int MaxConflictOverrideReasonLength = ProductionQcRules.MaxOverrideReasonLength;

    public async Task<ProductionOutcome> CreateAsync(
        SecurityPrincipal principal, Guid workspaceId, CreateProductionRequest request, Guid? snapshotId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        ProductionSpecification? specification = request.Specification;
        Guid? previousVersion = null;
        if (request.PreviousVersionId is { } previousId)
        {
            if (await productions.GetAsync(workspaceId, previousId, cancellationToken).ConfigureAwait(false) is not { } previous)
            {
                return new ProductionOutcome(ProductionOutcomeStatus.NotFound);
            }

            if (snapshotId is not null)
            {
                return ProductionOutcome.Invalid("previousVersionId", "A new version keeps its production's frozen set; give previousVersionId alone.");
            }

            snapshotId = previous.SnapshotId;
            previousVersion = previous.ProductionId;

            // Unless told otherwise, the new version continues the numbering after the version it replaces.
            specification ??= ProductionSpecificationRules.Deserialize(previous.SpecificationJson) is { } stored
                ? stored with { Bates = stored.Bates with { StartNumber = (previous.BatesLast ?? previous.BatesStart - 1) + 1 } }
                : null;
        }

        if (snapshotId is not { } sid)
        {
            return ProductionOutcome.Invalid("snapshotId", "Give the frozen set (snapshotId), a saved search (savedSearchId) or the version to supersede (previousVersionId).");
        }

        if (await UsableSnapshotAsync(principal, workspaceId, sid, ownSnapshotOnly: previousVersion is null, cancellationToken).ConfigureAwait(false)
            is not { } snapshot)
        {
            return new ProductionOutcome(ProductionOutcomeStatus.NotFound);
        }

        if (snapshot.Status is not (SnapshotStatus.Ready or SnapshotStatus.Materializing) || !SnapshotStrategyRules.AcceptsSnapshot(SetOperationKind.Production, snapshot.Purpose))
        {
            return ProductionOutcome.Invalid("snapshotId", "Produce a frozen set created for a production that is Ready (or still freezing).");
        }

        var name = string.IsNullOrWhiteSpace(request.Name) ? DefaultName(time.GetUtcNow()) : request.Name.Trim();
        if (name.Length > MaxNameLength || name.Any(char.IsControl))
        {
            return ProductionOutcome.Invalid("name", $"A name has 1 to {MaxNameLength} characters and no control characters.");
        }

        var (normalized, errors) = await NormalizeAsync(principal, workspaceId, specification, cancellationToken).ConfigureAwait(false);
        if (normalized is null)
        {
            return new ProductionOutcome(ProductionOutcomeStatus.Invalid, Errors: errors);
        }

        var productionId = Guid.CreateVersion7();
        var result = await productions.CreateAsync(new NewProduction
        {
            WorkspaceId = workspaceId,
            ProductionId = productionId,
            PreviousVersionId = previousVersion,
            Name = name,
            SnapshotId = snapshot.SnapshotId,
            Specification = Row(normalized, snapshot),
            CreatedBy = principal.UserId,
            CreatedByDisplay = Display(principal),
            CreatedByGroups = principal.Groups,
            AuditTemplate = UserEvent(principal, AuditTaxonomy.Production.Created, new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ProductionId"] = productionId.ToString(),
                ["Name"] = name,
                ["PreviousVersionId"] = previousVersion?.ToString(),
                ["SpecificationSha256"] = Convert.ToHexStringLower(normalized.Sha256),
                ["BatesStart"] = normalized.Format.Format(normalized.Specification.Bates.StartNumber),
                ["DesignationFieldId"] = normalized.Specification.Designations?.FieldId?.ToString(CultureInfo.InvariantCulture),
                ["DesignationRule"] = DesignationRuleName(ProductionSpecificationRules.RuleOf(normalized.Specification)),
            }) with
            { SnapshotId = snapshot.SnapshotId },
        }, cancellationToken).ConfigureAwait(false);
        return Outcome(result, normalized.Format);
    }

    public async Task<ProductionOutcome> UpdateAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid productionId, long expectedRowVersion, UpdateProductionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        if (await productions.GetAsync(workspaceId, productionId, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return new ProductionOutcome(ProductionOutcomeStatus.NotFound);
        }

        var snapshotId = request.SnapshotId ?? current.SnapshotId;
        if (await UsableSnapshotAsync(principal, workspaceId, snapshotId, ownSnapshotOnly: snapshotId != current.SnapshotId, cancellationToken)
                .ConfigureAwait(false) is not { } snapshot
            || snapshot.Status is not (SnapshotStatus.Ready or SnapshotStatus.Materializing)
            || !SnapshotStrategyRules.AcceptsSnapshot(SetOperationKind.Production, snapshot.Purpose))
        {
            return ProductionOutcome.Invalid("snapshotId", "Produce a frozen set created for a production that is Ready (or still freezing).");
        }

        var name = string.IsNullOrWhiteSpace(request.Name) ? current.Name : request.Name.Trim();
        if (name.Length > MaxNameLength || name.Any(char.IsControl))
        {
            return ProductionOutcome.Invalid("name", $"A name has 1 to {MaxNameLength} characters and no control characters.");
        }

        var (normalized, errors) = await NormalizeAsync(principal, workspaceId, request.Specification, cancellationToken).ConfigureAwait(false);
        if (normalized is null)
        {
            return new ProductionOutcome(ProductionOutcomeStatus.Invalid, Errors: errors);
        }

        var result = await productions.UpdateDraftAsync(workspaceId, productionId, expectedRowVersion, name, snapshot.SnapshotId, Row(normalized, snapshot),
            UserEvent(principal, AuditTaxonomy.Production.Modified, new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ProductionId"] = productionId.ToString(),
                ["Name"] = name,
                ["SpecificationSha256"] = Convert.ToHexStringLower(normalized.Sha256),
                ["PreviousSpecificationSha256"] = Convert.ToHexStringLower(current.SpecificationSha256),
                ["AllocationReleased"] = current.BatesState is BatesAllocationState.Allocated or BatesAllocationState.Failed ? "true" : "false",
                ["DesignationFieldId"] = normalized.Specification.Designations?.FieldId?.ToString(CultureInfo.InvariantCulture),
                ["DesignationRule"] = DesignationRuleName(ProductionSpecificationRules.RuleOf(normalized.Specification)),
                ["PreviousDesignationRule"] = DesignationRuleName(PlanOf(current).Rule),
            }) with
            { SnapshotId = snapshot.SnapshotId }, cancellationToken).ConfigureAwait(false);
        return Outcome(result, normalized.Format);
    }

    /// <summary>Starts (or returns the running) Bates allocation job of a draft.</summary>
    public async Task<ProductionOutcome> AllocateAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid productionId, string? clientIdempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var (result, job) = await productions.StartAllocationAsync(workspaceId, productionId, new NewJob
        {
            WorkspaceId = workspaceId,
            JobType = JobType.Production,
            InitiatedBy = principal.UserId,
            Parameters = new JsonObject { ["productionId"] = productionId.ToString(), ["step"] = "batesAllocation" },
            ClientIdempotencyKey = clientIdempotencyKey,
            CorrelationId = principal.CorrelationId,
        }, cancellationToken).ConfigureAwait(false);
        var outcome = Outcome(result, null);
        if (outcome.Status != ProductionOutcomeStatus.Ok)
        {
            return outcome;
        }

        job ??= result.Production?.BatesJobId is { } running ? await jobs.GetAsync(workspaceId, running, cancellationToken).ConfigureAwait(false) : null;
        return outcome with { Job = job };
    }

    /// <summary>Finalizes an allocated draft without overrides or acknowledged warnings.</summary>
    public Task<ProductionOutcome> FinalizeAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid productionId, long expectedRowVersion, CancellationToken cancellationToken = default) =>
        FinalizeAsync(principal, workspaceId, productionId, expectedRowVersion, ProductionFinalizeOptions.None, cancellationToken);

    /// <summary>Finalizes an allocated draft, overriding unresolved privilege conflicts with a reason (E13-T02 AC 2) when given.</summary>
    public Task<ProductionOutcome> FinalizeAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid productionId, long expectedRowVersion, string? privilegeConflictOverrideReason,
        CancellationToken cancellationToken = default) =>
        FinalizeAsync(principal, workspaceId, productionId, expectedRowVersion, privilegeConflictOverrideReason is null
            ? ProductionFinalizeOptions.None
            : new ProductionFinalizeOptions([new ProductionQcOverride(ProductionQcCheck.PrivilegeConflicts, privilegeConflictOverrideReason)], false),
            cancellationToken);

    /// <summary>
    /// Freezes an allocated draft behind the QC gate (E12-T07): every member is re-authorized for the caller (Q-15), the
    /// gate's checks run in the finalization transaction over the members as they are now, and a failed blocking check
    /// refuses the finalization (the run and its exceptions are kept and audited) unless <paramref name="options"/>
    /// overrides it with a reason (overridable checks only; privilege conflicts need <c>PrivilegeLog.Generate</c> besides
    /// <c>Production.Finalize</c>); warnings must be acknowledged. A passed run is named in the manifest with its
    /// overrides (each audited as <c>Production.QcOverride</c>; a privilege conflict override also as
    /// <c>Privilege.ConflictOverride</c>), and the specification, membership, Bates numbers, designations, page sets and
    /// redaction versions are frozen.
    /// </summary>
    public async Task<ProductionOutcome> FinalizeAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid productionId, long expectedRowVersion, ProductionFinalizeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(options);
        var (overrides, invalid) = ValidateOverrides(options.Overrides);
        if (invalid is not null)
        {
            return invalid;
        }

        if (overrides.Any(o => o.Check == ProductionQcCheck.PrivilegeConflicts)
            && !(await authorization.AuthorizeAsync(principal, workspaceId, Permission.PrivilegeLogGenerate, cancellationToken).ConfigureAwait(false)).IsAllowed)
        {
            return new ProductionOutcome(ProductionOutcomeStatus.Forbidden,
                Reason: "Overriding privilege conflicts needs PrivilegeLog.Generate as well as Production.Finalize.");
        }

        if (await productions.GetAsync(workspaceId, productionId, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return new ProductionOutcome(ProductionOutcomeStatus.NotFound);
        }

        if (current.RowVersion != expectedRowVersion)
        {
            return new ProductionOutcome(ProductionOutcomeStatus.VersionConflict, current);
        }

        if (current.Status != ProductionStatus.Draft || current.BatesState != BatesAllocationState.Allocated)
        {
            return new ProductionOutcome(ProductionOutcomeStatus.Conflict, current,
                Reason: current.Status == ProductionStatus.Draft ? "Allocate Bates numbers before finalizing." : "Only a draft production is finalized.");
        }

        if (await snapshots.GetAsync(workspaceId, current.SnapshotId, cancellationToken).ConfigureAwait(false) is not { Status: SnapshotStatus.Ready } snapshot)
        {
            return new ProductionOutcome(ProductionOutcomeStatus.Conflict, current, Reason: "The frozen set is no longer Ready.");
        }

        var coding = await codingStore.GetHighWaterAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var at = time.GetUtcNow();
        var qc = await QcRequestAsync(principal, current, ProductionQcPurpose.Finalization, overrides, options.AcknowledgeWarnings, at, cancellationToken)
            .ConfigureAwait(false);
        var format = new BatesFormat(current.BatesPrefix, current.BatesPadding, current.BatesSuffix, BatesNumberingLevel.Page);
        FinalizationWrite Compose(ProductionQcResult result)
        {
            var conflictOverride = result[ProductionQcCheck.PrivilegeConflicts].OverrideReason;
            var (manifest, sha) = ProductionManifest.Build(current, snapshot, coding, ProductionSoftware.Current, principal.UserId, at, conflictOverride, result);
            var details = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ProductionId"] = productionId.ToString(),
                ["Version"] = current.Version.ToString(CultureInfo.InvariantCulture),
                ["SpecificationSha256"] = Convert.ToHexStringLower(current.SpecificationSha256),
                ["ManifestSha256"] = Convert.ToHexStringLower(sha),
                ["BatesRange"] = $"{format.Format(current.BatesFirst!.Value)}-{format.Format(current.BatesLast!.Value)}",
                ["AssignmentsSha256"] = Convert.ToHexStringLower(current.AssignmentsSha256!),
                ["Documents"] = current.BatesDocuments?.ToString(CultureInfo.InvariantCulture),
                ["QcRunId"] = result.QcRunId.ToString(),
                ["QcReportSha256"] = Convert.ToHexStringLower(result.ReportSha256),
                ["QcOverridden"] = string.Join(',', result.Overridden.Select(c => c.Definition.Key)),
                ["QcWarningsAcknowledged"] = string.Join(',', result.Warnings.Select(c => c.Definition.Key)),
            };
            if (conflictOverride is not null)
            {
                details["PrivilegeConflictOverride"] = "true";
            }

            List<AuditEvent> audit =
            [
                Resource(UserEvent(principal, AuditTaxonomy.Production.SpecFrozen, details), current),
                Resource(UserEvent(principal, AuditTaxonomy.Production.Finalized, details), current),
            ];
            foreach (var check in result.Overridden)
            {
                audit.Add(Resource(UserEvent(principal, AuditTaxonomy.Production.QcOverride, new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["ProductionId"] = productionId.ToString(),
                    ["QcRunId"] = result.QcRunId.ToString(),
                    ["Check"] = check.Definition.Key,
                    ["Documents"] = check.Documents.ToString(CultureInfo.InvariantCulture),
                    ["Reason"] = Truncate(check.OverrideReason!, 500),
                    ["ManifestSha256"] = Convert.ToHexStringLower(sha),
                }), current));
            }

            if (conflictOverride is not null)
            {
                audit.Add(Resource(UserEvent(principal, AuditTaxonomy.Privilege.ConflictOverride, new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["ProductionId"] = productionId.ToString(),
                    ["Version"] = current.Version.ToString(CultureInfo.InvariantCulture),
                    ["Reason"] = Truncate(conflictOverride, 500),
                    ["ManifestSha256"] = Convert.ToHexStringLower(sha),
                }) with
                { Category = AuditTaxonomy.Privilege.Category }, current));
            }

            return new FinalizationWrite(manifest, sha, audit);
        }

        var plan = PlanOf(current);
        var designationAudit = UserEvent(principal, AuditTaxonomy.Production.DesignationsFrozen, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ProductionId"] = productionId.ToString(),
            ["DesignationFieldId"] = plan.FieldId?.ToString(CultureInfo.InvariantCulture),
            ["DesignationRule"] = DesignationRuleName(plan.Rule),
        });
        var result = await productions.FinalizeAsync(workspaceId, productionId, expectedRowVersion, principal.UserId, at, qc,
            UserEvent(principal, AuditTaxonomy.Production.QcRun, new Dictionary<string, string?>()), Compose, plan, designationAudit, cancellationToken)
            .ConfigureAwait(false);
        return result.Qc is { Passed: false }
            ? Blocked(result, format)
            : Outcome(result, format) with { Qc = result.Qc };
    }

    public async Task<ProductionOutcome> VoidAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid productionId, long expectedRowVersion, string? reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var text = reason?.Trim() ?? string.Empty;
        if (text.Length is 0 or > 2000)
        {
            return ProductionOutcome.Invalid("reason", "Give the reason for voiding (1 to 2000 characters).");
        }

        var current = await productions.GetAsync(workspaceId, productionId, cancellationToken).ConfigureAwait(false);
        var result = await productions.VoidAsync(workspaceId, productionId, expectedRowVersion, text, principal.UserId,
            UserEvent(principal, AuditTaxonomy.Production.Voided, new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ProductionId"] = productionId.ToString(),
                ["Reason"] = text.Length <= 500 ? text : text[..500],
                ["BatesRange"] = current?.BatesFirst is { } f ? string.Create(CultureInfo.InvariantCulture, $"{f}-{current.BatesLast}") : null,
                ["NumbersRetired"] = "true",
            }), cancellationToken).ConfigureAwait(false);
        return Outcome(result, null);
    }

    public async Task<ProductionOutcome> DiscardAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid productionId, long expectedRowVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var current = await productions.GetAsync(workspaceId, productionId, cancellationToken).ConfigureAwait(false);
        var result = await productions.DiscardAsync(workspaceId, productionId, expectedRowVersion,
            UserEvent(principal, AuditTaxonomy.Production.Discarded, new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ProductionId"] = productionId.ToString(),
                ["ReleasedBatesRange"] = current?.BatesFirst is { } f ? string.Create(CultureInfo.InvariantCulture, $"{f}-{current.BatesLast}") : null,
            }), cancellationToken).ConfigureAwait(false);
        return Outcome(result, null);
    }

    /// <summary>
    /// Re-verifies a production against its manifest: the stored specification and manifest against their hashes, the
    /// frozen set's pages against its root hash, a fresh run of the pure Bates allocation over the stored plan against the
    /// stored numbers and the manifest's assignment hash, and the integrity check. Differences are logged and audited
    /// (<c>Production.VerificationFailed</c>); a clean result is audited as <c>Production.Verified</c>. Re-generating
    /// volume files and comparing their SHA-256 is added with volume generation (E12-T05).
    /// </summary>
    public async Task<(ProductionOutcome Outcome, ProductionVerification? Verification)> VerifyAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid productionId, DocumentSetSnapshotService snapshotVerifier, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(snapshotVerifier);
        if (await productions.GetAsync(workspaceId, productionId, cancellationToken).ConfigureAwait(false) is not { } production)
        {
            return (new ProductionOutcome(ProductionOutcomeStatus.NotFound), null);
        }

        if (production.BatesState != BatesAllocationState.Allocated)
        {
            return (new ProductionOutcome(ProductionOutcomeStatus.Conflict, production, Reason: "Only an allocated or finalized production is verified."), null);
        }

        var differences = new List<ProductionDifference>();
        void Compare(string item, string? expected, string? actual)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                differences.Add(new ProductionDifference(item, expected, actual));
            }
        }

        var specSha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(production.SpecificationJson)));
        Compare("specificationSha256", Convert.ToHexStringLower(production.SpecificationSha256), specSha);

        var recomputed = await RecomputeAssignmentAsync(production, cancellationToken).ConfigureAwait(false);
        Compare("assignmentsSha256 (stored rows)", production.AssignmentsSha256 is { } a ? Convert.ToHexStringLower(a) : null, recomputed.StoredSha);
        Compare("assignmentsSha256 (re-run allocation)", production.AssignmentsSha256 is { } b ? Convert.ToHexStringLower(b) : null, recomputed.RerunSha);
        if (recomputed.FirstMismatch is { } mismatch)
        {
            differences.Add(mismatch);
        }

        var snapshot = await snapshots.GetAsync(workspaceId, production.SnapshotId, cancellationToken).ConfigureAwait(false);
        var snapshotCheck = await snapshotVerifier.VerifyAsync(workspaceId, production.SnapshotId, cancellationToken).ConfigureAwait(false);
        foreach (var problem in snapshotCheck.Problems)
        {
            differences.Add(new ProductionDifference("snapshot", null, problem));
        }

        if (production.Manifest is { } manifest)
        {
            Compare("manifestSha256", production.ManifestSha256 is { } m ? Convert.ToHexStringLower(m) : null,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))));
            var values = ProductionManifest.Read(manifest);
            Compare("manifest.specificationSha256", values.SpecificationSha256, specSha);
            Compare("manifest.snapshot.rootSha256", values.SnapshotRootSha256, snapshot?.RootSha256 is { } root ? Convert.ToHexStringLower(root) : null);
            Compare("manifest.bates.assignmentsSha256", values.AssignmentsSha256, recomputed.RerunSha);
            Compare("manifest.bates.firstNumber", Invariant(values.FirstNumber), production.BatesFirst is { } f ? Invariant(f) : null);
            Compare("manifest.bates.lastNumber", Invariant(values.LastNumber), production.BatesLast is { } l ? Invariant(l) : null);
        }

        var integrity = await productions.CheckIntegrityAsync(workspaceId, productionId, cancellationToken).ConfigureAwait(false);
        foreach (var problem in integrity.Problems)
        {
            differences.Add(new ProductionDifference("integrity", null, problem));
        }

        var consistent = differences.Count == 0;
        if (!consistent)
        {
            LogDifferences(logger, productionId, string.Join("; ", differences.Select(d => $"{d.Item}: expected {d.Expected ?? "-"}, actual {d.Actual ?? "-"}")));
        }

        var verified = UserEvent(principal, consistent ? AuditTaxonomy.Production.Verified : AuditTaxonomy.Production.VerificationFailed,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ProductionId"] = productionId.ToString(),
                ["Differences"] = Invariant(differences.Count),
                ["Items"] = string.Join(',', differences.Select(d => d.Item).Distinct()),
            });
        if (!consistent)
        {
            verified = verified with { Outcome = AuditOutcome.Failure, ReasonCode = "ManifestMismatch" };
        }

        await productions.AuditAsync(Resource(verified, production), cancellationToken).ConfigureAwait(false);
        return (new ProductionOutcome(ProductionOutcomeStatus.Ok, production), new ProductionVerification(consistent, differences, integrity));
    }

    /// <summary>
    /// Bates → documents (a label such as <c>ABC0000123</c>, parsed against every format in use) or document → its
    /// numbers in every production. Documents the caller may not view are left out and counted (Q-52).
    /// </summary>
    public async Task<(IReadOnlyList<BatesLookupRow> Matches, long Restricted)> LookupAsync(
        SecurityPrincipal principal, Guid workspaceId, string? number, Guid? documentId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var rows = new List<BatesLookupRow>();
        if (documentId is { } doc)
        {
            rows.AddRange(await productions.LookupDocumentAsync(workspaceId, doc, cancellationToken).ConfigureAwait(false));
        }
        else if (!string.IsNullOrWhiteSpace(number))
        {
            var label = number.Trim();
            var keys = new HashSet<(string, long)>();
            foreach (var used in await productions.GetFormatsInUseAsync(workspaceId, cancellationToken).ConfigureAwait(false))
            {
                var format = new BatesFormat(used.Prefix, used.Padding, used.Suffix, BatesNumberingLevel.Page);
                if (format.TryParse(label, out var n) && keys.Add((format.PrefixKey, n)))
                {
                    rows.AddRange(await productions.LookupNumberAsync(workspaceId, format.PrefixKey, n, cancellationToken).ConfigureAwait(false));
                }
            }
        }

        var visible = await VisibleAsync(principal, workspaceId, rows.Select(r => r.DocumentId), cancellationToken).ConfigureAwait(false);
        var distinct = rows.DistinctBy(r => (r.ProductionId, r.DocumentId)).ToList();
        return ([.. distinct.Where(r => visible.Contains(r.DocumentId))], distinct.Count(r => !visible.Contains(r.DocumentId)));
    }

    /// <summary>A page of the members with their numbers; documents the caller may not view are left out and counted.</summary>
    public async Task<(IReadOnlyList<ProductionDocumentRow> Rows, long Restricted, long? LastSequence)> ListDocumentsAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid productionId, long afterSequence, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var rows = await productions.ReadDocumentsAsync(workspaceId, productionId, afterSequence, limit, cancellationToken).ConfigureAwait(false);
        var visible = await VisibleAsync(principal, workspaceId, rows.Select(r => r.DocumentId), cancellationToken).ConfigureAwait(false);
        return ([.. rows.Where(r => visible.Contains(r.DocumentId))], rows.Count(r => !visible.Contains(r.DocumentId)),
            rows.Count > 0 ? rows[^1].Sequence : null);
    }

    public static string DefaultName(DateTimeOffset at) =>
        "Production " + at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    private async Task<(NormalizedSpecification? Normalized, Dictionary<string, string[]> Errors)> NormalizeAsync(
        SecurityPrincipal principal, Guid workspaceId, ProductionSpecification? specification, CancellationToken cancellationToken)
    {
        var catalog = await fields.GetCatalogAsync(workspaceId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var restricted = await fieldAccess.RestrictedFieldIdsAsync(workspaceId, principal, catalog, cancellationToken).ConfigureAwait(false);
        var normalized = ProductionSpecificationRules.Normalize(specification, catalog, restricted, out var errors);
        return (normalized, errors);
    }

    /// <summary>
    /// The snapshot when the caller may use it. A frozen set newly named for a production must be the caller's own (or the
    /// caller holds <c>Job.ViewAll</c>), as for exports; the frozen set a production already has is the production's.
    /// </summary>
    private async Task<SnapshotRecord?> UsableSnapshotAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid snapshotId, bool ownSnapshotOnly, CancellationToken cancellationToken)
    {
        var snapshot = await snapshots.GetAsync(workspaceId, snapshotId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return null;
        }

        return !ownSnapshotOnly || snapshot.CreatedBy == principal.UserId
            || (await authorization.AuthorizeAsync(principal, workspaceId, Permission.JobViewAll, cancellationToken).ConfigureAwait(false)).IsAllowed
                ? snapshot
                : null;
    }

    private async Task<HashSet<Guid>> VisibleAsync(SecurityPrincipal principal, Guid workspaceId, IEnumerable<Guid> documentIds, CancellationToken cancellationToken)
    {
        var ids = documentIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var decisions = await authorization.AuthorizeManyAsync(principal, workspaceId, Permission.DocumentView, ids, DenialAudit.Summary, cancellationToken)
            .ConfigureAwait(false);
        return [.. decisions.Where(d => d.Value.IsAllowed).Select(d => d.Key)];
    }

    /// <summary>
    /// Re-runs the pure allocation over the stored plan (family runs kept whole) and hashes both the stored numbers and
    /// the re-run's, reporting the first document where they differ.
    /// </summary>
    private async Task<(string StoredSha, string RerunSha, ProductionDifference? FirstMismatch)> RecomputeAssignmentAsync(
        ProductionRecord production, CancellationToken cancellationToken)
    {
        var format = ProductionSpecificationRules.FormatOf(ProductionSpecificationRules.Deserialize(production.SpecificationJson));
        using var stored = new BatesAssignmentHasher();
        using var rerun = new BatesAssignmentHasher();
        ProductionDifference? mismatch = null;
        var pending = new List<ProductionDocumentRow>();
        void Flush()
        {
            if (pending.Count == 0)
            {
                return;
            }

            var assigned = BatesAllocator.Assign(format, production.BatesStart,
                [.. pending.Select(r => new BatesSliceMember(r.Sequence, r.DocumentId, r.FamilyKey, r.Units, r.FirstOffset))]);
            for (var i = 0; i < pending.Count; i++)
            {
                var row = pending[i];
                var actual = new BatesAssignment(row.Sequence, row.DocumentId, row.BegNumber ?? 0, row.EndNumber ?? 0, row.ProdBegBates ?? string.Empty,
                    row.ProdEndBates ?? string.Empty, row.ProdBegAttach ?? string.Empty, row.ProdEndAttach ?? string.Empty);
                stored.Append(row.Sequence, row.DocumentId, row.DocumentVersion, row.Output, row.Units, actual);
                rerun.Append(row.Sequence, row.DocumentId, row.DocumentVersion, row.Output, row.Units, assigned[i]);
                if (mismatch is null && actual != assigned[i])
                {
                    mismatch = new ProductionDifference(string.Create(CultureInfo.InvariantCulture, $"document {row.Sequence} ({row.DocumentId})"),
                        $"{assigned[i].ProdBegBates}-{assigned[i].ProdEndBates} [{assigned[i].ProdBegAttach}-{assigned[i].ProdEndAttach}]",
                        $"{actual.ProdBegBates}-{actual.ProdEndBates} [{actual.ProdBegAttach}-{actual.ProdEndAttach}]");
                }
            }

            pending.Clear();
        }

        long after = 0;
        while (true)
        {
            var page = await productions.ReadDocumentsAsync(production.WorkspaceId, production.ProductionId, after, 5_000, cancellationToken).ConfigureAwait(false);
            foreach (var row in page)
            {
                if (pending.Count >= 1_000 && pending[^1].FamilyKey != row.FamilyKey)
                {
                    Flush();
                }

                pending.Add(row);
            }

            if (page.Count < 5_000)
            {
                break;
            }

            after = page[^1].Sequence;
        }

        Flush();
        return (Convert.ToHexStringLower(stored.Finish()), Convert.ToHexStringLower(rerun.Finish()), mismatch);
    }

    private static ProductionOutcome Outcome(ProductionWriteResult result, BatesFormat? format) => result.Status switch
    {
        ProductionWriteStatus.Applied => new ProductionOutcome(ProductionOutcomeStatus.Ok, result.Production),
        ProductionWriteStatus.NotFound => new ProductionOutcome(ProductionOutcomeStatus.NotFound),
        ProductionWriteStatus.VersionConflict => new ProductionOutcome(ProductionOutcomeStatus.VersionConflict, result.Production),
        ProductionWriteStatus.BatesConflict => new ProductionOutcome(ProductionOutcomeStatus.BatesConflict, result.Production,
            Reason: "The Bates start number overlaps " + string.Join("; ", (result.Conflicts ?? []).Select(c => string.Create(CultureInfo.InvariantCulture,
                $"production '{c.ProductionName}' ({Label(format, c.FirstNumber)}–{Label(format, c.LastNumber)}, {c.Status})")))
                + "; choose a start number after the numbers already used."),
        ProductionWriteStatus.PrivilegeWithheld => new ProductionOutcome(ProductionOutcomeStatus.PrivilegeWithheld, result.Production, Reason: result.Reason),
        ProductionWriteStatus.PrivilegeConflicts => new ProductionOutcome(ProductionOutcomeStatus.PrivilegeConflicts, result.Production, Reason: result.Reason),
        ProductionWriteStatus.DesignationRefused => new ProductionOutcome(ProductionOutcomeStatus.DesignationRefused, result.Production, Reason: result.Reason),
        _ => new ProductionOutcome(ProductionOutcomeStatus.Conflict, result.Production, Reason: result.Reason),
    };

    private static string Label(BatesFormat? format, long number) => format is null ? Invariant(number) : format.Format(number);

    private static ProductionSpecificationRow Row(NormalizedSpecification normalized, SnapshotRecord snapshot)
    {
        var bates = normalized.Specification.Bates;
        return new ProductionSpecificationRow(normalized.Json, normalized.Sha256, bates.Prefix, bates.Suffix ?? string.Empty, bates.Padding, bates.StartNumber,
            Math.Max(1, snapshot.DocumentCount ?? 1));
    }

    private AuditEvent UserEvent(SecurityPrincipal principal, string action, IReadOnlyDictionary<string, string?> details) => new()
    {
        OccurredAt = time.GetUtcNow(),
        Category = AuditTaxonomy.Production.Category,
        Action = action,
        ActorType = AuditActorType.User,
        ActorId = principal.UserId.ToString(),
        ActorDisplay = Display(principal),
        ClientIp = principal.ClientIp,
        UserAgent = principal.UserAgent,
        CorrelationId = principal.CorrelationId,
        Outcome = AuditOutcome.Success,
        Details = details,
    };

    private static AuditEvent Resource(AuditEvent audit, ProductionRecord production) => audit with
    {
        WorkspaceId = production.WorkspaceId,
        ResourceType = AuditTaxonomy.Production.ResourceType,
        ResourceId = production.ProductionId.ToString(),
        SnapshotId = production.SnapshotId,
        JobId = production.BatesJobId,
    };

    private static string Display(SecurityPrincipal principal) => string.IsNullOrEmpty(principal.DisplayName) ? principal.UserId.ToString() : principal.DisplayName;

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string DesignationRuleName(DesignationFamilyRule rule) =>
        rule == DesignationFamilyRule.Document ? "document" : "highestInFamily";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Production {ProductionId} differs from its manifest: {Differences}")]
    private static partial void LogDifferences(ILogger logger, Guid productionId, string differences);
}

public static class ProductionRegistration
{
    /// <summary>The production worker's Bates allocation: the coordinator (and with <paramref name="runCoordinator"/> its polling service) and the chunk executor.</summary>
    public static IServiceCollection AddProductionJobs(this IServiceCollection services, ProductionJobOptions? options = null, bool runCoordinator = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(options ?? new ProductionJobOptions());
        services.TryAddScoped<BatesAllocationCoordinator>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IJobChunkExecutor, BatesChunkExecutor>());
        if (runCoordinator)
        {
            services.AddHostedService<BatesAllocationCoordinatorService>();
        }

        return services;
    }

    /// <summary>The API side: <see cref="ProductionService"/> (the host registers the stores and the PDP).</summary>
    public static IServiceCollection AddProductionService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ProductionService>();
        return services;
    }
}
