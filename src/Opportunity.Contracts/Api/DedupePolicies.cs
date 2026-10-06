namespace Opportunity.Contracts.Api;

/// <summary>
/// Which hash computed duplicate grouping compares (E09-T04). <c>auto</c> (the default, Q-09): the parent's upstream
/// dedupe hash, or its upstream email hash, when present, otherwise the SHA-256 of its native.
/// </summary>
public enum DedupeHashSourceResource
{
    Auto,
    Sha256,
    Md5,
    Sha1,
    UpstreamHash,
}

/// <summary>Global: duplicates across the whole workspace. Custodial: only copies held by the same custodian.</summary>
public enum DedupeScopeResource
{
    Global,
    Custodial,
}

/// <summary>
/// The dedupe policy to save (<c>PUT …/dedupe-policy</c>, If-Match required). Saving changes no document: start a run.
/// </summary>
/// <param name="Enabled">Off: the next run removes the computed groups (upstream groups always stay).</param>
/// <param name="CustodianFieldId">Custodial scope: the field holding the custodian; omitted means the workspace's Custodian field.</param>
public sealed record DedupePolicyWrite(
    bool Enabled,
    DedupeHashSourceResource HashSource = DedupeHashSourceResource.Auto,
    DedupeScopeResource Scope = DedupeScopeResource.Global,
    int? CustodianFieldId = null);

/// <summary>What the last run found and changed (counts of live documents and families when it committed).</summary>
public sealed record DedupeRunSummaryResource(
    Guid JobId,
    DateTimeOffset RanAt,
    bool Enabled,
    DedupeHashSourceResource HashSource,
    DedupeScopeResource Scope,
    int? CustodianFieldId,
    long FamiliesCompared,
    long FamiliesWithoutHash,
    long FamiliesWithoutCustodian,
    long FamiliesWithUpstreamGroup,
    long Groups,
    long DocumentsGrouped,
    long DocumentsChanged);

/// <summary>
/// A workspace's computed duplicate grouping policy (E09-T04). Grouping is family-level (top-level parents are
/// compared; attachments follow their parent), never rewrites upstream groups, and only labels duplicates: the primary
/// (earliest Family Date, then lowest control number) is flagged, nothing is hidden or coded (Q-09).
/// </summary>
/// <param name="Version">The ETag value; 0 while no policy was saved (the default applies).</param>
public sealed record DedupePolicyResource(
    bool Enabled,
    DedupeHashSourceResource HashSource,
    DedupeScopeResource Scope,
    int? CustodianFieldId,
    long Version,
    Guid? ModifiedBy,
    DateTimeOffset? ModifiedAt,
    DedupeRunSummaryResource? LastRun);
