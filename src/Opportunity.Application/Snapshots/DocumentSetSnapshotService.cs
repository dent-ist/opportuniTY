using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Search;
using Opportunity.Contracts.Search;
using Opportunity.Core.Documents;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.Core.Snapshots;

namespace Opportunity.Application.Snapshots;

/// <summary>
/// What to freeze: exactly one of a query, explicit document IDs or another snapshot, optionally with the families,
/// duplicates and email threads of what it selects (E09-T03; added in the freeze, ADR-002 §5.2.1).
/// </summary>
public sealed record SnapshotCreateRequest(
    SnapshotPurpose Purpose,
    string? Name = null,
    string? Query = null,
    IReadOnlyList<Guid>? DocumentIds = null,
    Guid? SourceSnapshotId = null,
    string? ClientIdempotencyKey = null,
    RelationshipExpansion Expansion = default);

public enum SnapshotCreateStatus
{
    /// <summary>Frozen in the request: count, generation and hashes are final.</summary>
    Ready,

    /// <summary>Too large to freeze in the request; materializing in the background (202).</summary>
    Accepted,

    InvalidRequest,
    InvalidQuery,
    NotFound,
    Forbidden,

    /// <summary>More than <see cref="SnapshotOptions.MaxDocuments"/>: narrow the selection.</summary>
    TooLarge,

    /// <summary>The selection could not be completed (reader lost twice); nothing was frozen.</summary>
    Failed,
}

public sealed record SnapshotCreateOutcome
{
    public required SnapshotCreateStatus Status { get; init; }

    public SnapshotRecord? Snapshot { get; init; }

    public IReadOnlyList<QueryValidationDiagnostic> QueryErrors { get; init; } = [];

    public IReadOnlyDictionary<string, string[]> RequestErrors { get; init; } = new Dictionary<string, string[]>();

    /// <summary>Hit count of a rejected (too large) query.</summary>
    public long? Hits { get; init; }

    internal static SnapshotCreateOutcome Invalid(string field, string message) =>
        new() { Status = SnapshotCreateStatus.InvalidRequest, RequestErrors = new Dictionary<string, string[]> { [field] = [message] } };
}

/// <summary>
/// The materialized <c>DocumentSetSnapshot</c> service (E10-T02, ADR-002 §5): freezes a document set from a search
/// (through <see cref="ISearchService"/>, post-filtered per page, Q-12), explicit IDs or a saved selection (another
/// snapshot), re-authorizes every candidate for the creator with the purpose's permission inside the freeze (so a
/// snapshot never holds a document its creator cannot act on at the freeze point, ADR-015 D5.8 / T-38), stores ordered
/// immutable member pages with Q-07 baselines and SHA-256 hashes, and serves restartable chunk iteration by ordinal.
/// </summary>
public sealed class DocumentSetSnapshotService(
    IDocumentSetSnapshotStore store,
    ISearchService search,
    IAuthorizationService authorization,
    IAuditEventWriter audit,
    SnapshotOptions options,
    TimeProvider time)
{
    private const string ResourceType = "DocumentSetSnapshot";
    private const string CorrelationTag = "opportunity.correlation_id";

    /// <summary>Creates and, when small enough, freezes a snapshot for <paramref name="caller"/>.</summary>
    public async Task<SnapshotCreateOutcome> CreateAsync(SearchCaller caller, SnapshotCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        // §22 / ADR-002: the strategy rule decides; this service serves only operations it always materializes.
        if (!SnapshotStrategyRules.AlwaysMaterialized(SnapshotStrategyRules.OperationFor(request.Purpose)))
        {
            return SnapshotCreateOutcome.Invalid("purpose", "This purpose runs under a point-in-time reader, not a snapshot.");
        }

        var principal = caller.Principal;
        var ws = caller.WorkspaceId;
        var decision = await authorization.AuthorizeAsync(principal, ws, SnapshotRules.RequiredPermission(request.Purpose), cancellationToken)
            .ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            return new SnapshotCreateOutcome { Status = Denied(decision) };
        }

        if (request.ClientIdempotencyKey is { } key
            && await store.FindByClientKeyAsync(ws, principal.UserId, key, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return Existing(existing);
        }

        var name = string.IsNullOrWhiteSpace(request.Name) ? SnapshotRules.DefaultName(request.Purpose, time.GetUtcNow()) : request.Name.Trim();
        var snapshotId = Guid.CreateVersion7();
        if (request.Query is { } query)
        {
            return await CreateFromQueryAsync(caller, request, snapshotId, name, query, cancellationToken).ConfigureAwait(false);
        }

        if (request.SourceSnapshotId is { } sourceId)
        {
            var source = await store.GetAsync(ws, sourceId, cancellationToken).ConfigureAwait(false);
            if (source is null || !await CanSeeAsync(principal, source, cancellationToken).ConfigureAwait(false))
            {
                return new SnapshotCreateOutcome { Status = SnapshotCreateStatus.NotFound };
            }

            if (source.Status != SnapshotStatus.Ready)
            {
                return SnapshotCreateOutcome.Invalid("sourceSnapshotId", "The source snapshot is not ready.");
            }

            var created = await CreateHeaderAsync(caller, request, snapshotId, name, SnapshotSourceKind.Snapshot, cancellationToken).ConfigureAwait(false);
            if (!created.Created)
            {
                return Existing(created.Snapshot);
            }

            if (source.DocumentCount > options.SynchronousMaxDocuments)
            {
                await store.ReleaseClaimAsync(ws, snapshotId, options.InstanceId, cancellationToken).ConfigureAwait(false);
                return new SnapshotCreateOutcome { Status = SnapshotCreateStatus.Accepted, Snapshot = created.Snapshot };
            }

            return await MaterializeClaimedAsync(created.Snapshot, principal, cancellationToken).ConfigureAwait(false);
        }

        var ids = request.DocumentIds!.Distinct().ToList();
        var header = await CreateHeaderAsync(caller, request with { DocumentIds = ids }, snapshotId, name, SnapshotSourceKind.DocumentIds, cancellationToken)
            .ConfigureAwait(false);
        if (!header.Created)
        {
            return Existing(header.Snapshot);
        }

        await store.StageAsync(ws, snapshotId, ids, SnapshotInclusionReason.Explicit, cancellationToken).ConfigureAwait(false);
        return await FreezeAsync(header.Snapshot, principal, new SnapshotFreezeRequest
        {
            WorkspaceId = ws,
            SnapshotId = snapshotId,
            ClaimOwner = options.InstanceId,
            SelectedAt = time.GetUtcNow(),
            AuthorizationBatchSize = options.AuthorizationBatchSize,
        }, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Background materialization of a Materializing snapshot nobody holds (too large for the request, or its process
    /// died): claims it and runs the selection and freeze as its creator. A snapshot out of attempts fails.
    /// </summary>
    public async Task<SnapshotStatus?> MaterializePendingAsync(Guid workspaceId, Guid snapshotId, CancellationToken cancellationToken = default)
    {
        if (!await store.TryClaimAsync(workspaceId, snapshotId, options.InstanceId, options.ClaimLease, cancellationToken).ConfigureAwait(false)
            || await store.GetAsync(workspaceId, snapshotId, cancellationToken).ConfigureAwait(false) is not { } snapshot)
        {
            return null;
        }

        if (snapshot.AttemptCount > options.MaxAttempts)
        {
            await FailAsync(snapshot, null, "AttemptsExhausted", "The selection could not be completed; create the snapshot again.", cancellationToken)
                .ConfigureAwait(false);
            return SnapshotStatus.Failed;
        }

        var principal = new SecurityPrincipal
        {
            UserId = snapshot.CreatedBy,
            DisplayName = snapshot.CreatedByDisplay,
            Groups = snapshot.CreatedByGroups,
            CorrelationId = snapshot.CorrelationId,
        };
        var outcome = await MaterializeClaimedAsync(snapshot, principal, cancellationToken).ConfigureAwait(false);
        return outcome.Snapshot?.Status;
    }

    /// <summary>Members <paramref name="fromOrdinal"/>…<paramref name="toOrdinal"/> of a Ready snapshot (one chunk).</summary>
    public Task<IReadOnlyList<SnapshotMember>> ReadRangeAsync(
        Guid workspaceId, Guid snapshotId, long fromOrdinal, long toOrdinal, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fromOrdinal);
        ArgumentOutOfRangeException.ThrowIfLessThan(toOrdinal, fromOrdinal);
        return store.ReadMembersAsync(workspaceId, snapshotId, fromOrdinal, toOrdinal, cancellationToken);
    }

    /// <summary>
    /// Iterates the members after <paramref name="afterOrdinal"/> in batches; restart from the last ordinal processed
    /// and nothing is skipped or repeated, because membership never changes once Ready.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<SnapshotMember>> IterateAsync(
        Guid workspaceId, Guid snapshotId, long afterOrdinal = 0, int batchSize = SnapshotRules.DefaultPageSize,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterOrdinal);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var snapshot = await store.GetAsync(workspaceId, snapshotId, cancellationToken).ConfigureAwait(false);
        if (snapshot is not { Status: SnapshotStatus.Ready, DocumentCount: { } count })
        {
            yield break;
        }

        for (var from = afterOrdinal + 1; from <= count; from += batchSize)
        {
            yield return await store.ReadMembersAsync(workspaceId, snapshotId, from, Math.Min(count, from + batchSize - 1), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Plans the chunks of a job over a Ready snapshot (ADR-010 §4 <c>SnapshotRange</c>): dense ordinal ranges covering
    /// 1…N exactly once.
    /// </summary>
    public static IReadOnlyList<ChunkMembership> PlanChunks(SnapshotRecord snapshot, int maxItems)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot is not { Status: SnapshotStatus.Ready, DocumentCount: { } count })
        {
            throw new InvalidOperationException("Only a Ready snapshot can be planned.");
        }

        return [.. ChunkPlanner.SplitByCount(1, count, maxItems).Select(r => ChunkMembership.SnapshotRange(snapshot.SnapshotId, r.From, r.To))];
    }

    /// <summary>Recomputes every page hash, the dense ordinals and the root hash from the stored members.</summary>
    public async Task<SnapshotVerification> VerifyAsync(Guid workspaceId, Guid snapshotId, CancellationToken cancellationToken = default)
    {
        var snapshot = await store.GetAsync(workspaceId, snapshotId, cancellationToken).ConfigureAwait(false);
        if (snapshot is not { Status: SnapshotStatus.Ready, DocumentCount: { } count, RootSha256: { } root })
        {
            return new SnapshotVerification(false, 0, 0, ["The snapshot is not Ready."]);
        }

        var problems = new List<string>();
        var hashes = new List<byte[]>();
        long expected = 1;
        var pageNo = 1;
        while (true)
        {
            var pages = await store.ReadPagesAsync(workspaceId, snapshotId, pageNo, 100, cancellationToken).ConfigureAwait(false);
            foreach (var page in pages)
            {
                if (page.PageNo != pageNo || page.FirstOrdinal != expected)
                {
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"Page {page.PageNo} starts at {page.FirstOrdinal}; expected page {pageNo} at {expected}."));
                }

                foreach (var member in page.Members)
                {
                    if (member.Ordinal != expected++)
                    {
                        problems.Add(string.Create(CultureInfo.InvariantCulture, $"Ordinal {member.Ordinal} out of sequence on page {page.PageNo}."));
                    }
                }

                var hash = SnapshotHashing.PageHash(page.Members);
                if (!hash.AsSpan().SequenceEqual(page.Sha256))
                {
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"Page {page.PageNo} hash mismatch."));
                }

                hashes.Add(page.Sha256);
                pageNo++;
            }

            if (pages.Count < 100)
            {
                break;
            }
        }

        if (expected - 1 != count)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{expected - 1} members stored; the header says {count}."));
        }

        if (!SnapshotHashing.RootHash(count, hashes).AsSpan().SequenceEqual(root))
        {
            problems.Add("Root hash mismatch.");
        }

        return new SnapshotVerification(problems.Count == 0, expected - 1, hashes.Count, problems);
    }

    /// <summary>
    /// Freezes an explicit set that another use case selected and authorized (E09-T05: the targets of a coding
    /// propagation), in the request whatever its size: each member is re-authorized for the caller with
    /// <paramref name="memberPermission"/> instead of the purpose's own permission, which the use case has replaced
    /// with its own check. A retry with the same <paramref name="clientIdempotencyKey"/> returns the same snapshot.
    /// </summary>
    public async Task<SnapshotCreateOutcome> FreezeSelectionAsync(
        SearchCaller caller, SnapshotPurpose purpose, string name, IReadOnlyList<Guid> documentIds, Permission memberPermission,
        string clientIdempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(documentIds);
        ArgumentException.ThrowIfNullOrEmpty(clientIdempotencyKey);
        var ws = caller.WorkspaceId;
        if (await store.FindByClientKeyAsync(ws, caller.Principal.UserId, clientIdempotencyKey, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return Existing(existing);
        }

        var ids = documentIds.Distinct().ToList();
        var request = new SnapshotCreateRequest(purpose, name, DocumentIds: ids, ClientIdempotencyKey: clientIdempotencyKey);
        var header = await CreateHeaderAsync(caller, request, Guid.CreateVersion7(), name, SnapshotSourceKind.DocumentIds, cancellationToken)
            .ConfigureAwait(false);
        if (!header.Created)
        {
            return Existing(header.Snapshot);
        }

        foreach (var batch in ids.Chunk(SnapshotRules.MaxExplicitDocumentIds))
        {
            await store.StageAsync(ws, header.Snapshot.SnapshotId, batch, SnapshotInclusionReason.Explicit, cancellationToken).ConfigureAwait(false);
        }

        return await FreezeAsync(header.Snapshot, caller.Principal, new SnapshotFreezeRequest
        {
            WorkspaceId = ws,
            SnapshotId = header.Snapshot.SnapshotId,
            ClaimOwner = options.InstanceId,
            SelectedAt = time.GetUtcNow(),
            AuthorizationBatchSize = options.AuthorizationBatchSize,
        }, null, cancellationToken, memberPermission).ConfigureAwait(false);
    }

    /// <summary>Whether <paramref name="principal"/> may see the snapshot's header: its creator, or holders of <c>Job.ViewAll</c>.</summary>
    public async Task<bool> CanSeeAsync(SecurityPrincipal principal, SnapshotRecord snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.CreatedBy == principal.UserId
            || (await authorization.AuthorizeAsync(principal, snapshot.WorkspaceId, Permission.JobViewAll, cancellationToken).ConfigureAwait(false)).IsAllowed;
    }

    private async Task<SnapshotCreateOutcome> CreateFromQueryAsync(
        SearchCaller caller, SnapshotCreateRequest request, Guid snapshotId, string name, string query, CancellationToken cancellationToken)
    {
        var ws = caller.WorkspaceId;
        for (var attempt = 1; ; attempt++)
        {
            var watermark = await store.ReadWatermarkAsync(ws, cancellationToken).ConfigureAwait(false);
            var selectedAt = time.GetUtcNow();
            var selection = await search.SelectAsync(
                caller,
                new SearchSelectionRequest(query, options.SynchronousMaxDocuments, options.SelectionPageSize),
                (ids, ct) => store.StageAsync(ws, snapshotId, ids, SnapshotInclusionReason.Hit, ct),
                cancellationToken).ConfigureAwait(false);
            switch (selection.Status)
            {
                case SearchSelectionStatus.InvalidQuery:
                    return new SnapshotCreateOutcome { Status = SnapshotCreateStatus.InvalidQuery, QueryErrors = selection.QueryErrors };
                case SearchSelectionStatus.NotFound:
                    return new SnapshotCreateOutcome { Status = SnapshotCreateStatus.NotFound };
                case SearchSelectionStatus.Forbidden:
                    return new SnapshotCreateOutcome { Status = SnapshotCreateStatus.Forbidden };
                case SearchSelectionStatus.ReaderLost:
                    await store.ResetStageAsync(ws, snapshotId, cancellationToken).ConfigureAwait(false);
                    if (attempt < options.MaxAttempts)
                    {
                        continue;
                    }

                    return new SnapshotCreateOutcome { Status = SnapshotCreateStatus.Failed };
                case SearchSelectionStatus.TooManyHits when selection.Hits > options.MaxDocuments:
                    return new SnapshotCreateOutcome { Status = SnapshotCreateStatus.TooLarge, Hits = selection.Hits };
                case SearchSelectionStatus.TooManyHits:
                    {
                        var pending = await CreateHeaderAsync(caller, request, snapshotId, name, SnapshotSourceKind.Query, cancellationToken,
                            selection.Normalized).ConfigureAwait(false);
                        if (pending.Created)
                        {
                            await store.ReleaseClaimAsync(ws, snapshotId, options.InstanceId, cancellationToken).ConfigureAwait(false);
                        }

                        return pending.Created
                            ? new SnapshotCreateOutcome { Status = SnapshotCreateStatus.Accepted, Snapshot = pending.Snapshot, Hits = selection.Hits }
                            : Existing(pending.Snapshot);
                    }

                default:
                    {
                        var created = await CreateHeaderAsync(caller, request, snapshotId, name, SnapshotSourceKind.Query, cancellationToken,
                            selection.Normalized).ConfigureAwait(false);
                        if (!created.Created)
                        {
                            await store.ResetStageAsync(ws, snapshotId, cancellationToken).ConfigureAwait(false);
                            return Existing(created.Snapshot);
                        }

                        return await FreezeAsync(created.Snapshot, caller.Principal,
                            SelectionFreeze(created.Snapshot, watermark, selection, selectedAt), selection, cancellationToken).ConfigureAwait(false);
                    }
            }
        }
    }

    /// <summary>Selection (when needed) and freeze of a snapshot this process holds the claim of.</summary>
    private async Task<SnapshotCreateOutcome> MaterializeClaimedAsync(SnapshotRecord snapshot, SecurityPrincipal principal, CancellationToken cancellationToken)
    {
        var ws = snapshot.WorkspaceId;
        await store.ResetStageAsync(ws, snapshot.SnapshotId, cancellationToken).ConfigureAwait(false);
        switch (snapshot.SourceKind)
        {
            case SnapshotSourceKind.Snapshot:
                if (await store.GetAsync(ws, snapshot.SourceSnapshotId!.Value, cancellationToken).ConfigureAwait(false) is not { Status: SnapshotStatus.Ready })
                {
                    return await FailAsync(snapshot, principal, "SourceNotReady", "The source snapshot is no longer available.", cancellationToken)
                        .ConfigureAwait(false);
                }

                await store.StageFromSnapshotAsync(ws, snapshot.SnapshotId, snapshot.SourceSnapshotId.Value, cancellationToken).ConfigureAwait(false);
                return await FreezeAsync(snapshot, principal, new SnapshotFreezeRequest
                {
                    WorkspaceId = ws,
                    SnapshotId = snapshot.SnapshotId,
                    ClaimOwner = options.InstanceId,
                    SelectedAt = time.GetUtcNow(),
                    AuthorizationBatchSize = options.AuthorizationBatchSize,
                }, null, cancellationToken).ConfigureAwait(false);

            case SnapshotSourceKind.Query:
                var caller = new SearchCaller(principal, ws, null);
                var watermark = await store.ReadWatermarkAsync(ws, cancellationToken).ConfigureAwait(false);
                var selectedAt = time.GetUtcNow();
                var selection = await search.SelectAsync(
                    caller,
                    new SearchSelectionRequest(snapshot.QueryText!, options.MaxDocuments, options.SelectionPageSize),
                    (ids, ct) => store.StageAsync(ws, snapshot.SnapshotId, ids, SnapshotInclusionReason.Hit, ct),
                    cancellationToken).ConfigureAwait(false);
                switch (selection.Status)
                {
                    case SearchSelectionStatus.Ok:
                        return await FreezeAsync(snapshot, principal, SelectionFreeze(snapshot, watermark, selection, selectedAt), selection, cancellationToken)
                            .ConfigureAwait(false);
                    case SearchSelectionStatus.ReaderLost:
                        // Leave it claimed-but-expiring: the next attempt starts over with a new reader and watermark.
                        await store.ResetStageAsync(ws, snapshot.SnapshotId, cancellationToken).ConfigureAwait(false);
                        await store.ReleaseClaimAsync(ws, snapshot.SnapshotId, options.InstanceId, cancellationToken).ConfigureAwait(false);
                        return new SnapshotCreateOutcome { Status = SnapshotCreateStatus.Accepted, Snapshot = snapshot };
                    case SearchSelectionStatus.TooManyHits:
                        return await FailAsync(snapshot, principal, "TooLarge", "The selection is too large; narrow it.", cancellationToken).ConfigureAwait(false);
                    default:
                        // The creator lost access or the query no longer binds (e.g. a field was deleted).
                        return await FailAsync(snapshot, principal, selection.Status.ToString(), "The selection can no longer be run by its creator.", cancellationToken)
                            .ConfigureAwait(false);
                }

            default:
                return await FailAsync(snapshot, principal, "Unsupported", "Explicit-ID snapshots are frozen in the request only.", cancellationToken)
                    .ConfigureAwait(false);
        }
    }

    private SnapshotFreezeRequest SelectionFreeze(SnapshotRecord snapshot, SearchWatermark watermark, SearchSelectionOutcome selection, DateTimeOffset selectedAt) => new()
    {
        WorkspaceId = snapshot.WorkspaceId,
        SnapshotId = snapshot.SnapshotId,
        ClaimOwner = options.InstanceId,
        SearchGeneration = watermark.Applied,
        ProjectionGeneration = selection.ProjectionGeneration,
        SelectedWhileIndexing = !watermark.IsCurrent,
        SelectedAt = selectedAt,
        ExcludedBeforeStaging = selection.DroppedTotal,
        AuthorizationBatchSize = options.AuthorizationBatchSize,
    };

    private async Task<SnapshotCreateOutcome> FreezeAsync(
        SnapshotRecord snapshot, SecurityPrincipal principal, SnapshotFreezeRequest request, SearchSelectionOutcome? selection,
        CancellationToken cancellationToken, Permission? memberPermission = null)
    {
        var permission = memberPermission ?? SnapshotRules.MemberPermission(snapshot.Purpose);
        var excluded = new Dictionary<string, long>(StringComparer.Ordinal);
        var result = await store.FreezeAsync(
            request with { Audit = frozen => FreezeAudit(frozen, principal, selection, permission, excluded) },
            async (ids, ct) =>
            {
                var decisions = await authorization.AuthorizeManyAsync(principal, snapshot.WorkspaceId, permission, [.. ids], DenialAudit.Caller, ct)
                    .ConfigureAwait(false);
                var denied = new Dictionary<Guid, string>();
                foreach (var id in ids)
                {
                    if (!decisions.TryGetValue(id, out var decision) || !decision.IsAllowed)
                    {
                        var reason = decisions.TryGetValue(id, out var d) ? d.Reason : AuthorizationReasons.DocumentNotFound;
                        denied[id] = reason;
                        excluded[reason] = excluded.GetValueOrDefault(reason) + 1;
                    }
                }

                return denied;
            },
            cancellationToken).ConfigureAwait(false);

        if (result.Outcome == SnapshotFreezeOutcome.ClaimLost)
        {
            // Another process took the snapshot over (our claim expired); it reports the result.
            return new SnapshotCreateOutcome
            {
                Status = SnapshotCreateStatus.Accepted,
                Snapshot = await store.GetAsync(snapshot.WorkspaceId, snapshot.SnapshotId, cancellationToken).ConfigureAwait(false) ?? snapshot,
            };
        }

        return new SnapshotCreateOutcome { Status = SnapshotCreateStatus.Ready, Snapshot = result.Snapshot };
    }

    /// <summary>
    /// The freeze's audit record, from the closed ADR-013 taxonomy: one <c>Search.Executed</c> carrying the
    /// <c>SnapshotId</c> (resource none, per ADR-013 §5 "PIT or snapshot ID") with the frozen count and root hash and,
    /// for query sources, the full text in the restricted details (Q-16); plus one summary <c>AuthZ.Denied</c> when the
    /// freeze excluded candidates (Q-59). There is no Snapshot category in ADR-013 (lead decision requested).
    /// </summary>
    private List<AuditEvent> FreezeAudit(
        SnapshotRecord frozen, SecurityPrincipal principal, SearchSelectionOutcome? selection, Permission permission, Dictionary<string, long> excluded)
    {
        var details = new Dictionary<string, string?>
        {
            ["context"] = "SnapshotMaterialized",
            ["purpose"] = frozen.Purpose.ToString(),
            ["source"] = frozen.SourceKind.ToString(),
            ["sourceSnapshotId"] = frozen.SourceSnapshotId?.ToString(),
            ["requested"] = frozen.RequestedCount is { } r ? Invariant(r) : null,
            ["hits"] = selection is null ? null : Invariant(selection.Hits),
            ["documentCount"] = Invariant(frozen.DocumentCount ?? 0),
            ["candidateCount"] = Invariant(frozen.CandidateCount ?? 0),
            ["excludedNoAccess"] = Invariant(frozen.ExcludedNoAccess ?? 0),
            ["excludedMissing"] = Invariant(frozen.ExcludedMissing ?? 0),
            ["pageCount"] = Invariant(frozen.PageCount ?? 0),
            ["rootSha256"] = frozen.RootSha256 is { } root ? Convert.ToHexStringLower(root) : null,
            ["projectionGeneration"] = frozen.ProjectionGeneration is { } p ? Invariant(p) : null,
            ["selectedWhileIndexing"] = frozen.SelectedWhileIndexing is { } w ? (w ? "true" : "false") : null,
            ["strategy"] = frozen.Strategy.ToString(),
            ["expand"] = frozen.Expansion.IsNone ? null : frozen.Expansion.ToString(),
            ["strategyRule"] = SnapshotStrategyRules.Describe(SnapshotStrategyRules.Decide(
                SnapshotStrategyRules.OperationFor(frozen.Purpose), new SetSizeEstimate(frozen.CandidateCount ?? 0), options.Pit)),
        };
        Dictionary<string, string?>? restricted = null;
        if (frozen.QueryText is { } text)
        {
            restricted = new Dictionary<string, string?> { ["query"] = text, ["normalized"] = frozen.NormalizedQuery };
            if (selection?.AstJson is { } ast && ast.Length <= AuditEventRules.MaxRestrictedDetailsBytes / 2)
            {
                restricted["ast"] = ast;
            }
        }

        var events = new List<AuditEvent>
        {
            Event(frozen, principal, selection?.BreakGlass == true, AuditTaxonomy.SearchCategory, "Executed", details, restricted,
                AuditOutcome.Success, null),
        };
        var denied = excluded.Where(e => e.Value > 0).OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
        if (denied.Count > 0)
        {
            var total = denied.Sum(d => d.Value);
            var summary = new Dictionary<string, string?>
            {
                ["permission"] = permission.Name(),
                ["requested"] = Invariant(frozen.CandidateCount ?? 0),
                ["denied"] = Invariant(total),
                ["context"] = "SnapshotFreeze",
            };
            foreach (var (reason, count) in denied)
            {
                summary["denied." + reason] = Invariant(count);
            }

            events.Add(Event(frozen, principal, false, AuditTaxonomy.AuthZ.Category, AuditTaxonomy.AuthZ.Denied, summary, null,
                AuditOutcome.Denied, denied.Count == 1 ? denied[0].Key : "Multiple"));
        }

        return events;
    }

    private async Task<SnapshotCreateOutcome> FailAsync(
        SnapshotRecord snapshot, SecurityPrincipal? principal, string reasonCode, string reason, CancellationToken cancellationToken)
    {
        await store.FailAsync(snapshot.WorkspaceId, snapshot.SnapshotId, reason, cancellationToken).ConfigureAwait(false);
        principal ??= new SecurityPrincipal { UserId = snapshot.CreatedBy, DisplayName = snapshot.CreatedByDisplay, CorrelationId = snapshot.CorrelationId };
        await audit.WriteAsync(Event(snapshot, principal, false, AuditTaxonomy.SearchCategory, "Executed",
            new Dictionary<string, string?>
            {
                ["context"] = "SnapshotMaterialized",
                ["purpose"] = snapshot.Purpose.ToString(),
                ["source"] = snapshot.SourceKind.ToString(),
            },
            snapshot.QueryText is { } q ? new Dictionary<string, string?> { ["query"] = q } : null,
            AuditOutcome.Failure, reasonCode), cancellationToken).ConfigureAwait(false);
        return new SnapshotCreateOutcome
        {
            Status = SnapshotCreateStatus.Failed,
            Snapshot = await store.GetAsync(snapshot.WorkspaceId, snapshot.SnapshotId, cancellationToken).ConfigureAwait(false) ?? snapshot,
        };
    }

    private AuditEvent Event(
        SnapshotRecord snapshot, SecurityPrincipal principal, bool breakGlass, string category, string action,
        IReadOnlyDictionary<string, string?> details, IReadOnlyDictionary<string, string?>? restricted, AuditOutcome outcome, string? reason) => new()
        {
            WorkspaceId = snapshot.WorkspaceId,
            OccurredAt = time.GetUtcNow(),
            Category = category,
            Action = action,
            ActorType = AuditActorType.User,
            ActorId = principal.UserId.ToString(),
            ActorDisplay = string.IsNullOrEmpty(principal.DisplayName) ? principal.UserId.ToString() : principal.DisplayName,
            AccessPath = breakGlass ? AuditAccessPath.BreakGlass : AuditAccessPath.Normal,
            ClientIp = principal.ClientIp,
            UserAgent = principal.UserAgent,
            ResourceType = ResourceType,
            ResourceId = snapshot.SnapshotId.ToString(),
            Outcome = outcome,
            ReasonCode = reason,
            CorrelationId = principal.CorrelationId ?? snapshot.CorrelationId ?? Activity.Current?.GetTagItem(CorrelationTag) as string,
            SnapshotId = snapshot.SnapshotId,
            SearchGeneration = snapshot.SearchGeneration,
            Details = details.Where(d => d.Value is not null).ToDictionary(),
            RestrictedDetails = category == AuditTaxonomy.SearchCategory ? restricted : null,
        };

    private Task<SnapshotCreation> CreateHeaderAsync(
        SearchCaller caller, SnapshotCreateRequest request, Guid snapshotId, string name, SnapshotSourceKind kind, CancellationToken cancellationToken,
        string? normalized = null) =>
        store.CreateAsync(new NewSnapshot
        {
            WorkspaceId = caller.WorkspaceId,
            SnapshotId = snapshotId,
            Name = name,
            Purpose = request.Purpose,
            SourceKind = kind,
            QueryText = kind == SnapshotSourceKind.Query ? request.Query : null,
            NormalizedQuery = normalized,
            SourceSnapshotId = kind == SnapshotSourceKind.Snapshot ? request.SourceSnapshotId : null,
            RequestedCount = kind == SnapshotSourceKind.DocumentIds ? request.DocumentIds!.Count : null,
            CreatedBy = caller.Principal.UserId,
            CreatedByDisplay = string.IsNullOrEmpty(caller.Principal.DisplayName) ? caller.Principal.UserId.ToString() : caller.Principal.DisplayName,
            CreatedByGroups = caller.Principal.Groups,
            CorrelationId = caller.Principal.CorrelationId ?? Activity.Current?.GetTagItem(CorrelationTag) as string,
            ClientIdempotencyKey = request.ClientIdempotencyKey,
            Expansion = request.Expansion,
            ClaimOwner = options.InstanceId,
            ClaimLease = options.ClaimLease,
        }, cancellationToken);

    private static SnapshotCreateOutcome? Validate(SnapshotCreateRequest request)
    {
        if (!Enum.IsDefined(request.Purpose))
        {
            return SnapshotCreateOutcome.Invalid("purpose", "Purpose must be BulkCoding, Export, Production or Report.");
        }

        var sources = (request.Query is not null ? 1 : 0) + (request.DocumentIds is not null ? 1 : 0) + (request.SourceSnapshotId is not null ? 1 : 0);
        if (sources != 1)
        {
            return SnapshotCreateOutcome.Invalid("source", "Give exactly one of query, documentIds or snapshotId.");
        }

        if (request.Name is { } name && (name.Trim().Length is 0 or > SnapshotRules.MaxNameLength || name.Any(char.IsControl)))
        {
            return SnapshotCreateOutcome.Invalid("name", $"The name must be 1–{SnapshotRules.MaxNameLength} characters without control characters.");
        }

        if (request.Query is { Length: > SnapshotRules.MaxQueryLength })
        {
            return SnapshotCreateOutcome.Invalid("query", $"The query is longer than {SnapshotRules.MaxQueryLength} characters.");
        }

        if (request.DocumentIds is { } ids && (ids.Count is 0 or > SnapshotRules.MaxExplicitDocumentIds || ids.Contains(Guid.Empty)))
        {
            return SnapshotCreateOutcome.Invalid("documentIds",
                $"Give 1–{SnapshotRules.MaxExplicitDocumentIds} document IDs; select larger sets with a query.");
        }

        if (request.SourceSnapshotId == Guid.Empty)
        {
            return SnapshotCreateOutcome.Invalid("snapshotId", "The snapshot ID is not valid.");
        }

        return null;
    }

    private static SnapshotCreateOutcome Existing(SnapshotRecord snapshot) => new()
    {
        Status = snapshot.Status switch
        {
            SnapshotStatus.Ready => SnapshotCreateStatus.Ready,
            SnapshotStatus.Materializing => SnapshotCreateStatus.Accepted,
            _ => SnapshotCreateStatus.Failed,
        },
        Snapshot = snapshot,
    };

    private static SnapshotCreateStatus Denied(AuthorizationDecision decision) =>
        decision.Outcome == AuthorizationOutcome.NotFound ? SnapshotCreateStatus.NotFound : SnapshotCreateStatus.Forbidden;

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
