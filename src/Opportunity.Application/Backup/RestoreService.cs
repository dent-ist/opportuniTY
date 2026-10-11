using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Opportunity.Application.Audit;
using Opportunity.Application.Audit.Chain;
using Opportunity.Application.Jobs;
using Opportunity.Application.Keys;
using Opportunity.Application.Search.Indexing;
using Opportunity.Application.Storage;
using Opportunity.Core.Workspaces;

namespace Opportunity.Application.Backup;

/// <param name="RestoreId">Chosen by the operator (or generated); re-running reconcile with the same id resumes it.</param>
/// <param name="Path">The search path from <c>restore plan</c>.</param>
/// <param name="TargetTime">The PostgreSQL recovery target; null for an end-of-WAL restore.</param>
/// <param name="BackupLabel">The pgBackRest backup set restored.</param>
/// <param name="SnapshotName">Path A: the OpenSearch snapshot restored.</param>
public sealed record RestoreRequest(
    Guid RestoreId,
    RestorePath Path,
    DateTimeOffset? TargetTime,
    string? BackupLabel,
    string? SnapshotName,
    string OperatorName);

/// <summary>Evidence outside PostgreSQL of work after the recovery target (design §6). Identifiers only.</summary>
/// <param name="Kind">DeletionCertified, ProductionOutput, ExportOutput, KeyUnavailable.</param>
public sealed record LostWindowFinding(string Kind, Guid? WorkspaceId, string Subject, string Action);

public sealed record RestoreReconcileReport(
    RestoreRun Run,
    IReadOnlyList<WorkspaceReconcileResult> Workspaces,
    IReadOnlyList<Guid> PurgedFromSearch,
    IReadOnlyList<Guid> RebuildRequired,
    long SessionsRevoked,
    IReadOnlyList<LostWindowFinding> LostWindow);

/// <param name="RecordKey">Where the signed record is stored (null when no object store is configured).</param>
public sealed record RestoreRecordResult(RestoreRun Run, int Chains, string? RecordKey, string RecordSha256, IReadOnlyList<AuditCheckpoint> Checkpoints);

/// <param name="PreOpen">Must pass before any writer starts (ADR-016 §8); the others are convergence checks after reopening.</param>
public sealed record RestoreGate(string Name, bool Passed, bool PreOpen, string Detail);

public sealed record RestoreVerification(IReadOnlyList<RestoreGate> Gates)
{
    public bool Passed => Gates.All(g => g.Passed);
}

/// <param name="Converge">After reopening (R8): the watermark caught up and a full comparison reports 0 ahead/stale/missing.</param>
/// <param name="CompareSample">Pre-open ahead-check: documents compared per workspace (evenly spread pages); null compares all.</param>
/// <param name="ObjectSampleOfOlder">Inventory: objects older than <c>T_r − 24 h</c> sampled per workspace (all newer ones are checked).</param>
/// <param name="AcknowledgeLostWindow">The operator reviewed the lost-window report (required when it lists findings).</param>
/// <param name="RecoverMissingObjects">Recover a missing object from its bucket version before failing the inventory gate.</param>
/// <param name="Keys">Public keys for the audit checkpoint signatures; null uses the installation's key provider.</param>
public sealed record RestoreVerifyOptions(
    bool Converge = false,
    int? CompareSample = 10_000,
    int ObjectSampleOfOlder = 10_000,
    bool AcknowledgeLostWindow = false,
    bool RecoverMissingObjects = false,
    IAuditCheckpointKeys? Keys = null);

/// <summary>
/// The restore tool of ADR-016 §8 (design §4–§7): R6 reconcile (catch-up above the snapshot anchor, redrive, security
/// reset, deletion re-fence, lost-window report), the restore record in every audit chain with an <c>AfterRestore</c>
/// checkpoint and a signed copy under <c>sys/restores/</c>, and the pre-open and convergence gates. It only changes
/// PostgreSQL rows and lets the existing dispatcher and workers re-drive the work: no new index write path.
/// </summary>
public sealed class RestoreService(
    IRestoreStore store,
    ISearchSnapshotStore snapshots,
    ISearchRestoreChecks search,
    IWorkspaceSearchPurge purge,
    IAuditEventWriter audit,
    IAuditChainSealer sealer,
    IAuditChainReader chains,
    ISigningKeyProvider signer,
    IKeyEncryptionKeyProvider keks,
    IWorkspaceDataKeyStore dataKeys,
    TimeProvider time,
    IObjectStore? objects = null,
    IObjectVersionRecovery? versions = null,
    ISearchSnapshotRepository? repository = null)
{
    public const string RecordFormat = "opportunity.restore-record.v1";

    /// <summary>Objects created this long before the recovery target are all checked (design §7.2).</summary>
    public static readonly TimeSpan InventoryWindow = TimeSpan.FromHours(24);

    private const int MaxListedFindings = 200;
    private const int MaxOrphanKeysListed = 50_000;

    /// <summary>R6. Resumable while every writer is stopped; refused once the restore record was written (writers may run).</summary>
    public async Task<RestoreReconcileReport> ReconcileAsync(RestoreRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Path == RestorePath.KeepLive)
        {
            throw new InvalidOperationException("Path B (keep the live cluster after an earlier target) is not automated; use path A or C.");
        }

        if ((request.Path == RestorePath.Snapshot) != (request.SnapshotName is not null))
        {
            throw new ArgumentException("A snapshot name is required for path A (Snapshot) and only there.", nameof(request));
        }

        var run = await store.BeginRunAsync(new RestoreRun(request.RestoreId, request.Path, request.TargetTime, request.BackupLabel,
            request.SnapshotName, request.OperatorName, RestorePhase.Reconciling, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        if (run.Phase is RestorePhase.Recorded or RestorePhase.Verified)
        {
            throw new InvalidOperationException(
                $"Restore {run.RestoreId:D} is already {run.Phase}: reconcile re-pends work and must not run once writers may have started.");
        }

        if (run.Path != request.Path || run.SnapshotName != request.SnapshotName)
        {
            throw new InvalidOperationException($"Restore {run.RestoreId:D} was started with path {run.Path} and snapshot {run.SnapshotName ?? "-"}.");
        }

        IReadOnlyDictionary<Guid, SearchSnapshotAnchor> anchors = new Dictionary<Guid, SearchSnapshotAnchor>();
        if (run.Path == RestorePath.Snapshot)
        {
            var record = await snapshots.FindAsync(repository?.RepositoryName ?? new SearchSnapshotOptions().Repository, run.SnapshotName!, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Snapshot {run.SnapshotName} has no anchor record in the restored database: it was requested after the recovery target, so it is ahead of PostgreSQL. Choose an older snapshot (restore plan) or rebuild.");
            anchors = await snapshots.GetAnchorsAsync(record.SnapshotId, cancellationToken).ConfigureAwait(false);
        }

        var results = new List<WorkspaceReconcileResult>();
        var purged = new List<Guid>();
        var rebuild = new List<Guid>();
        foreach (var workspace in await store.ListWorkspacesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (workspace.Status == WorkspaceStatus.Purged)
            {
                // Design §4.4.3: the snapshot may still hold a workspace whose purge finished after it started.
                if (run.Path != RestorePath.EndOfWal)
                {
                    await purge.PurgeAsync(workspace.WorkspaceId, cancellationToken).ConfigureAwait(false);
                    purged.Add(workspace.WorkspaceId);
                }

                continue;
            }

            long? anchor = null;
            if (run.Path == RestorePath.Snapshot)
            {
                anchors.TryGetValue(workspace.WorkspaceId, out var a);
                anchor = a?.IndexedThroughGeneration ?? 0;
                var placement = await search.CheckPlacementAsync(workspace.WorkspaceId, cancellationToken).ConfigureAwait(false);
                if (a?.PlacementInTransition == true || !placement.Resolves)
                {
                    // Design §4.5: the snapshot does not reflect this placement; reindex it right after reopening (path C).
                    anchor = 0;
                    rebuild.Add(workspace.WorkspaceId);
                }
            }
            else if (run.Path == RestorePath.Rebuild)
            {
                // The empty cluster reflects nothing: freshness must not claim otherwise until the reindex switched.
                anchor = 0;
                rebuild.Add(workspace.WorkspaceId);
            }

            results.Add(await store.ReconcileWorkspaceAsync(workspace.WorkspaceId, anchor, cancellationToken).ConfigureAwait(false));
        }

        var revoked = await store.RevokeSessionsAsync(cancellationToken).ConfigureAwait(false);
        var lost = await LostWindowAsync(cancellationToken).ConfigureAwait(false);
        foreach (var finding in lost.Where(f => f.Kind == "DeletionCertified" && f.WorkspaceId is not null))
        {
            await store.CloseWorkspaceAsync(finding.WorkspaceId!.Value, cancellationToken).ConfigureAwait(false);
        }

        var report = new RestoreReconcileReport(run, results, purged, rebuild, revoked, lost);
        await store.UpdateRunAsync(run.RestoreId, RestorePhase.Reconciled, ToJson(report), null, null, cancellationToken).ConfigureAwait(false);
        return report with { Run = run with { Phase = RestorePhase.Reconciled } };
    }

    /// <summary>
    /// R6d (design §4.6): seals every chain, writes <c>Integrity.Restored</c> with the chain's restored head into the system
    /// chain and every workspace chain, takes <c>AfterRestore</c> checkpoints, and stores the signed restore record under
    /// <c>sys/restores/{restoreId}/{sha256}</c>.
    /// </summary>
    public async Task<RestoreRecordResult> RecordAsync(Guid restoreId, CancellationToken cancellationToken = default)
    {
        var run = await store.GetRunAsync(restoreId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Restore {restoreId:D} has not been reconciled.");
        if (run.Phase == RestorePhase.Reconciling)
        {
            throw new InvalidOperationException($"Restore {restoreId:D} has not finished reconciling.");
        }

        if (run.Phase is RestorePhase.Recorded or RestorePhase.Verified && run.RecordSha256 is { } done)
        {
            return new RestoreRecordResult(run, 0, run.RecordKey, done, []);
        }

        await sealer.SealAsync(null, cancellationToken).ConfigureAwait(false);
        var heads = (await chains.ReadHeadsAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(h => h.ChainId);
        var chainIds = new SortedSet<Guid> { AuditChainFormat.SystemChainId };
        foreach (var workspace in await store.ListWorkspacesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (workspace.Status != WorkspaceStatus.Purged)
            {
                chainIds.Add(workspace.WorkspaceId);
            }
        }

        var restoredHeads = new JsonArray();
        foreach (var chain in chainIds)
        {
            heads.TryGetValue(chain, out var head);
            var sequence = head?.LastSequence ?? 0;
            var hash = head is null ? AuditChainFormat.Hex(AuditChainFormat.Genesis) : AuditChainFormat.Hex(head.LastHash);
            restoredHeads.Add(new JsonObject { ["chain"] = AuditChainFormat.DescribeChain(chain), ["sequence"] = sequence, ["eventHash"] = hash });
            await audit.WriteAsync(RestoredEvent(run, chain, sequence, hash), cancellationToken).ConfigureAwait(false);
        }

        var checkpoints = await sealer.CheckpointAsync(AuditCheckpointReason.AfterRestore, null, cancellationToken).ConfigureAwait(false);

        var record = new JsonObject
        {
            ["format"] = RecordFormat,
            ["restoreId"] = run.RestoreId.ToString("D"),
            ["path"] = run.Path.ToString(),
            ["targetTime"] = run.TargetTime is { } t ? Time(t) : null,
            ["backupLabel"] = run.BackupLabel,
            ["snapshot"] = run.SnapshotName,
            ["operator"] = run.OperatorName,
            ["startedAt"] = Time(run.StartedAt),
            ["recordedAt"] = Time(time.GetUtcNow()),
            ["restoredHeads"] = restoredHeads,
        };
        var canonical = Encoding.UTF8.GetBytes(CanonicalJson.Canonicalize(record.ToJsonString()));
        var signed = new JsonObject { ["record"] = JsonNode.Parse(canonical) };
        try
        {
            var signature = await signer.SignAsync(SigningKeyPurposes.AuditCheckpoint, canonical, cancellationToken).ConfigureAwait(false);
            signed["keyId"] = signature.KeyId;
            signed["algorithm"] = signature.Algorithm;
            signed["signature"] = Convert.ToBase64String(signature.Value);
        }
        catch (Exception ex) when (ex is KeyUnavailableException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // The record still names the restore in every chain; the signature is what makes the stored copy independent.
            signed["signature"] = null;
            signed["unsignedReason"] = ex.GetType().Name;
        }

        var bytes = Encoding.UTF8.GetBytes(CanonicalJson.Canonicalize(signed.ToJsonString()));
        var sha = Sha256Digest.FromBytes(SHA256.HashData(bytes));
        string? key = null;
        if (objects is not null)
        {
            var objectKey = ObjectKeys.RestoreRecord(run.RestoreId, sha);
            using var content = new MemoryStream(bytes, writable: false);
            await objects.PutAsync(objectKey, content, new PutObjectOptions
            {
                ContentType = "application/json",
                ExpectedSha256 = sha,
                ExpectedLength = bytes.Length,
            }, cancellationToken).ConfigureAwait(false);
            key = objectKey.Value;
        }

        await store.UpdateRunAsync(run.RestoreId, RestorePhase.Recorded, null, key, sha.Hex, cancellationToken).ConfigureAwait(false);
        return new RestoreRecordResult(run with { Phase = RestorePhase.Recorded, RecordKey = key, RecordSha256 = sha.Hex }, chainIds.Count, key, sha.Hex,
            checkpoints);
    }

    /// <summary>The pre-open gates of ADR-016 §8 (or, with <see cref="RestoreVerifyOptions.Converge"/>, the R8 convergence check).</summary>
    public async Task<RestoreVerification> VerifyAsync(Guid restoreId, RestoreVerifyOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var run = await store.GetRunAsync(restoreId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Restore {restoreId:D} is unknown.");
        var gates = new List<RestoreGate>();
        var workspaces = (await store.ListWorkspacesAsync(cancellationToken).ConfigureAwait(false))
            .Where(w => w.Status != WorkspaceStatus.Purged)
            .ToList();
        var rebuild = (run.Report?["rebuildRequired"] as JsonArray ?? []).Select(n => Guid.Parse(n!.GetValue<string>())).ToHashSet();

        IReadOnlyDictionary<Guid, SearchSnapshotAnchor> anchors = new Dictionary<Guid, SearchSnapshotAnchor>();
        if (run.Path == RestorePath.Snapshot && await snapshots.FindAsync(repository?.RepositoryName ?? new SearchSnapshotOptions().Repository,
                run.SnapshotName!, cancellationToken).ConfigureAwait(false) is { } snapshot)
        {
            anchors = await snapshots.GetAnchorsAsync(snapshot.SnapshotId, cancellationToken).ConfigureAwait(false);
        }

        gates.Add(new RestoreGate("restore-record", run.Phase is RestorePhase.Recorded or RestorePhase.Verified && run.RecordSha256 is not null, true,
            run.RecordSha256 is { } s ? $"record {s} at {run.RecordKey ?? "(database only)"}" : "run `restore record` first"));
        gates.Add(await AuditGateAsync(run, options, cancellationToken).ConfigureAwait(false));
        gates.Add(await KeysGateAsync(workspaces, cancellationToken).ConfigureAwait(false));

        long inFlight = 0, notPending = 0, breakGlass = 0, searchSessions = 0, watermarkAbove = 0;
        var states = new Dictionary<Guid, WorkspaceRestoreState>();
        foreach (var workspace in workspaces)
        {
            long? anchor = run.Path switch
            {
                RestorePath.Snapshot => rebuild.Contains(workspace.WorkspaceId) ? 0 : anchors.TryGetValue(workspace.WorkspaceId, out var a) ? a.IndexedThroughGeneration : 0,
                RestorePath.Rebuild => 0,
                _ => null,
            };
            var state = await store.InspectWorkspaceAsync(workspace.WorkspaceId, options.Converge ? null : anchor, cancellationToken).ConfigureAwait(false);
            states[workspace.WorkspaceId] = state;
            inFlight += state.InFlight;
            notPending += state.NotPendingAboveAnchor;
            breakGlass += state.OpenBreakGlass;
            searchSessions += state.SearchSessions;
            if (anchor is { } g && state.Watermark > g)
            {
                watermarkAbove++;
            }
        }

        if (!options.Converge)
        {
            gates.Add(new RestoreGate("work-state", inFlight == 0, true, $"{N(inFlight)} claimed, dispatched or running work record(s)"));
            if (run.Path is RestorePath.Snapshot or RestorePath.Rebuild)
            {
                gates.Add(new RestoreGate("watermark", watermarkAbove == 0 && notPending == 0, true,
                    $"{N(watermarkAbove)} workspace(s) with the watermark above the anchor, {N(notPending)} record(s) above it not Pending"));
            }

            var sessions = await store.CountLiveSessionsAsync(cancellationToken).ConfigureAwait(false);
            gates.Add(new RestoreGate("security-reset", sessions == 0 && breakGlass == 0 && searchSessions == 0, true,
                $"{N(sessions)} live session(s), {N(breakGlass)} open break-glass activation(s), {N(searchSessions)} search session(s)"));

            var findings = run.Report?["lostWindow"] as JsonArray ?? [];
            gates.Add(new RestoreGate("lost-window", findings.Count == 0 || options.AcknowledgeLostWindow, true,
                findings.Count == 0 ? "nothing after the target left evidence outside PostgreSQL"
                    : $"{findings.Count} finding(s); review the report and pass --acknowledge-lost-window"));
        }

        gates.Add(await PlacementGateAsync(workspaces, rebuild, cancellationToken).ConfigureAwait(false));
        gates.Add(await SearchGateAsync(run, workspaces, rebuild, states, options, cancellationToken).ConfigureAwait(false));
        if (!options.Converge)
        {
            gates.Add(await ObjectsGateAsync(run, workspaces, options, cancellationToken).ConfigureAwait(false));
        }

        var verification = new RestoreVerification(gates);
        if (verification.Passed && !options.Converge && run.Phase == RestorePhase.Recorded)
        {
            await store.UpdateRunAsync(run.RestoreId, RestorePhase.Verified, null, null, null, cancellationToken).ConfigureAwait(false);
        }

        return verification;
    }

    /// <summary>Design §6: evidence in object storage of work the restored database does not know.</summary>
    public async Task<IReadOnlyList<LostWindowFinding>> LostWindowAsync(CancellationToken cancellationToken = default)
    {
        var findings = new List<LostWindowFinding>();
        if (objects is null)
        {
            return findings;
        }

        var certificates = new Dictionary<Guid, string>();
        await foreach (var listing in objects.ListPrefixAsync(ObjectPrefix.Parse("sys/certificates/"), cancellationToken).ConfigureAwait(false))
        {
            var segments = listing.Key.Value.Split('/');
            if (segments.Length == 4 && Guid.TryParseExact(segments[2], "N", out var deletionId))
            {
                certificates[deletionId] = listing.Key.Value;
            }
        }

        var statuses = await store.GetDeletionStatusesAsync([.. certificates.Keys], cancellationToken).ConfigureAwait(false);
        var workspaces = await store.ListWorkspacesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (deletionId, key) in certificates)
        {
            if (statuses.TryGetValue(deletionId, out var status) && status.StartsWith("Completed", StringComparison.Ordinal))
            {
                continue;
            }

            var workspace = await WorkspaceOfDeletionAsync(deletionId, cancellationToken).ConfigureAwait(false);
            findings.Add(new LostWindowFinding("DeletionCertified", workspace, key,
                "A destruction certificate exists but the restored database has not completed this deletion: the workspace is closed; re-run its deletion (the certificate is the approval of record)."));
        }

        foreach (var workspace in workspaces.Where(w => w.Status != WorkspaceStatus.Purged))
        {
            var (productions, exports) = await store.KnownOutputsAsync(workspace.WorkspaceId, cancellationToken).ConfigureAwait(false);
            await foreach (var listing in objects.ListPrefixAsync(ObjectPrefix.WorkspaceArea(workspace.WorkspaceId, "productions"), cancellationToken)
                .ConfigureAwait(false))
            {
                // ws/{ws}/productions/{productionId}/v{n}/{runId}/...: a volume run is an export row (V0052).
                var s = listing.Key.Value.Split('/');
                if (s.Length >= 6 && Guid.TryParseExact(s[3], "N", out var production) && Guid.TryParseExact(s[5], "N", out var runId)
                    && (!productions.Contains(production) || !exports.Contains(runId)))
                {
                    AddOnce(findings, new LostWindowFinding("ProductionOutput", workspace.WorkspaceId, string.Join('/', s[..6]) + "/",
                        "A production volume the restored database does not know: its MANIFEST.json Bates numbers may already be delivered. Record them before finalizing another production in this workspace."));
                }
            }

            await foreach (var listing in objects.ListPrefixAsync(ObjectPrefix.WorkspaceArea(workspace.WorkspaceId, "exports"), cancellationToken)
                .ConfigureAwait(false))
            {
                var s = listing.Key.Value.Split('/');
                if (s.Length >= 5 && Guid.TryParseExact(s[3], "N", out var export) && !exports.Contains(export))
                {
                    AddOnce(findings, new LostWindowFinding("ExportOutput", workspace.WorkspaceId, string.Join('/', s[..5]) + "/",
                        "Export output the restored database does not know; kept until reviewed."));
                }
            }

            if (findings.Count > MaxListedFindings)
            {
                break;
            }
        }

        return findings;
    }

    private async Task<Guid?> WorkspaceOfDeletionAsync(Guid deletionId, CancellationToken cancellationToken)
    {
        // The certificate names its workspace; read it only for the finding (identifiers, never content).
        if (objects is null)
        {
            return null;
        }

        await foreach (var listing in objects.ListPrefixAsync(ObjectPrefix.Parse($"sys/certificates/{deletionId:N}/"), cancellationToken)
            .ConfigureAwait(false))
        {
            await using var stream = await objects.OpenReadAsync(listing.Key, cancellationToken: cancellationToken).ConfigureAwait(false);
            var node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (node?["workspaceId"]?.GetValue<string>() is { } text && Guid.TryParse(text, out var ws))
            {
                return ws;
            }
        }

        return null;
    }

    private async Task<RestoreGate> AuditGateAsync(RestoreRun run, RestoreVerifyOptions options, CancellationToken cancellationToken)
    {
        var keys = options.Keys ?? new SigningProviderCheckpointKeys(signer);
        var broken = new List<string>();
        var withoutCheckpoint = 0;
        var ids = await chains.ListChainsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var chain in ids)
        {
            var report = await chains.VerifyAsync(chain, keys, cancellationToken).ConfigureAwait(false);
            if (!report.Intact)
            {
                broken.Add($"{AuditChainFormat.DescribeChain(chain)}: {(report.Issues.Count > 0 ? report.Issues[0].Kind : null)}");
            }

            var afterRestore = false;
            await foreach (var checkpoint in chains.CheckpointsAsync(chain, cancellationToken).ConfigureAwait(false))
            {
                afterRestore |= checkpoint.Data.Reason == AuditCheckpointReason.AfterRestore && checkpoint.Data.CreatedAt >= run.StartedAt.UtcDateTime;
            }

            if (!afterRestore && (chain == AuditChainFormat.SystemChainId || report.Events > 0))
            {
                withoutCheckpoint++;
            }
        }

        return new RestoreGate("audit-chains", broken.Count == 0 && withoutCheckpoint == 0, true,
            broken.Count > 0 ? "broken: " + string.Join("; ", broken.Take(5))
                : withoutCheckpoint > 0 ? $"{withoutCheckpoint} chain(s) without an AfterRestore checkpoint"
                : $"{ids.Count} chain(s) intact, each with an AfterRestore checkpoint");
    }

    private async Task<RestoreGate> KeysGateAsync(List<RestoreWorkspace> workspaces, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        var checkedKeys = 0;
        foreach (var workspace in workspaces)
        {
            foreach (var row in await dataKeys.ListAsync(workspace.WorkspaceId, cancellationToken).ConfigureAwait(false))
            {
                if (row.State == WorkspaceDataKeyState.Destroyed || row.Wrapped is not { } wrapped)
                {
                    continue;
                }

                checkedKeys++;
                try
                {
                    CryptographicOperations.ZeroMemory(await keks.UnwrapAsync(wrapped, cancellationToken).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is KeyUnavailableException or CryptographicException or InvalidOperationException or IOException)
                {
                    problems.Add($"{workspace.WorkspaceId:D} {WorkspaceDataKey.FormatKeyId(row.Version)} ({row.KekId} v{row.KekVersion}): {ex.GetType().Name}");
                }
            }
        }

        var keyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chain in await chains.ListChainsAsync(cancellationToken).ConfigureAwait(false))
        {
            await foreach (var checkpoint in chains.CheckpointsAsync(chain, cancellationToken).ConfigureAwait(false))
            {
                keyIds.Add(checkpoint.KeyId);
            }
        }

        var publicKeys = new SigningProviderCheckpointKeys(signer);
        foreach (var keyId in keyIds)
        {
            if (await publicKeys.GetPublicKeyAsync(keyId, cancellationToken).ConfigureAwait(false) is null)
            {
                problems.Add($"signing key {keyId} has no public key");
            }
        }

        return new RestoreGate("keys", problems.Count == 0, true,
            problems.Count == 0 ? $"{checkedKeys} data key(s) unwrap; {keyIds.Count} checkpoint key id(s) present"
                : string.Join("; ", problems.Take(5)) + (problems.Count > 5 ? $" (+{problems.Count - 5})" : string.Empty));
    }

    private async Task<RestoreGate> PlacementGateAsync(List<RestoreWorkspace> workspaces, HashSet<Guid> rebuild, CancellationToken cancellationToken)
    {
        var unresolved = new List<Guid>();
        foreach (var workspace in workspaces)
        {
            var check = await search.CheckPlacementAsync(workspace.WorkspaceId, cancellationToken).ConfigureAwait(false);
            if (!check.Resolves && !rebuild.Contains(workspace.WorkspaceId))
            {
                unresolved.Add(workspace.WorkspaceId);
            }
        }

        return new RestoreGate("search-placements", unresolved.Count == 0, true,
            unresolved.Count == 0
                ? $"{workspaces.Count} workspace(s): every placement resolves to an existing index and alias ({rebuild.Count} marked for reindex)"
                : $"{unresolved.Count} placement(s) do not resolve: {string.Join(", ", unresolved.Take(5))}");
    }

    private async Task<RestoreGate> SearchGateAsync(
        RestoreRun run, List<RestoreWorkspace> workspaces, HashSet<Guid> rebuild, Dictionary<Guid, WorkspaceRestoreState> states,
        RestoreVerifyOptions options, CancellationToken cancellationToken)
    {
        long ahead = 0, stale = 0, missing = 0, compared = 0, behindWatermark = 0, failed = 0;
        var examples = new List<string>();
        foreach (var workspace in workspaces.Where(w => !rebuild.Contains(w.WorkspaceId)))
        {
            if (options.Converge)
            {
                var state = states[workspace.WorkspaceId];
                if (state.Watermark < state.SearchGeneration)
                {
                    behindWatermark++;
                }

                failed += state.FailedWork;
            }

            var comparison = await search.CompareAsync(workspace.WorkspaceId, options.Converge ? null : options.CompareSample, [], cancellationToken)
                .ConfigureAwait(false);
            compared += comparison.Compared;
            ahead += comparison.Ahead;
            stale += comparison.Stale;
            missing += comparison.Missing;
            if (comparison.Ahead > 0 || (options.Converge && !comparison.Converged))
            {
                examples.AddRange(comparison.Examples.Take(3).Select(e => $"{workspace.WorkspaceId:D}/{e:D}"));
            }
        }

        if (options.Converge)
        {
            var converged = ahead == 0 && stale == 0 && missing == 0 && behindWatermark == 0 && failed == 0;
            return new RestoreGate("search-converged", converged, false,
                $"{N(compared)} compared: {N(ahead)} ahead, {N(stale)} stale, {N(missing)} missing; {N(behindWatermark)} workspace(s) behind, {N(failed)} failed work record(s)"
                + (examples.Count > 0 ? "; e.g. " + string.Join(", ", examples.Take(5)) : string.Empty));
        }

        // Before reopening only "ahead" is wrong: behind is the lag catch-up removes (design §7.1.3).
        var note = run.Path == RestorePath.EndOfWal ? "end of WAL: the live cluster may only lag" : "snapshot rule enforced by restore plan";
        return new RestoreGate("search-not-ahead", ahead == 0, true,
            $"{N(compared)} compared: {N(ahead)} ahead ({N(stale)} stale, {N(missing)} missing will catch up); {note}"
            + (examples.Count > 0 ? "; e.g. " + string.Join(", ", examples.Take(5)) : string.Empty));
    }

    private async Task<RestoreGate> ObjectsGateAsync(
        RestoreRun run, List<RestoreWorkspace> workspaces, RestoreVerifyOptions options, CancellationToken cancellationToken)
    {
        if (objects is null)
        {
            return new RestoreGate("objects", false, true, "no object store is configured for the tool (ObjectStorage__*)");
        }

        var from = (run.TargetTime ?? run.StartedAt) - InventoryWindow;
        long headed = 0, recovered = 0, orphans = 0;
        var missing = new List<string>();
        foreach (var workspace in workspaces)
        {
            foreach (var entry in await store.SampleStoredObjectsAsync(workspace.WorkspaceId, from, options.ObjectSampleOfOlder, cancellationToken)
                .ConfigureAwait(false))
            {
                headed++;
                var key = ObjectKey.Parse(entry.ObjectKey);
                var info = await objects.HeadAsync(key, cancellationToken).ConfigureAwait(false);
                if (info is null && options.RecoverMissingObjects && versions is not null
                    && await versions.RecoverAsync(key, null, cancellationToken).ConfigureAwait(false) == ObjectRecoveryOutcome.Recovered)
                {
                    recovered++;
                    info = await objects.HeadAsync(key, cancellationToken).ConfigureAwait(false);
                }

                if (info is null || info.Length != entry.SizeBytes)
                {
                    missing.Add(entry.ObjectKey);
                }
            }

            orphans += await CountOrphansAsync(workspace.WorkspaceId, cancellationToken).ConfigureAwait(false);
        }

        return new RestoreGate("objects", missing.Count == 0, true,
            $"{N(headed)} registered object(s) checked (all since {Time(from)} plus a sample), {N(recovered)} recovered from versions, "
            + $"{N(missing.Count)} missing or wrong size; {N(orphans)} orphan(s) reported, not deleted"
            + (missing.Count > 0 ? "; e.g. " + string.Join(", ", missing.Take(3)) : string.Empty));
    }

    private async Task<long> CountOrphansAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        long orphans = 0, listed = 0;
        var batch = new List<string>(1_000);
        await foreach (var listing in objects!.ListPrefixAsync(ObjectPrefix.Workspace(workspaceId), cancellationToken).ConfigureAwait(false))
        {
            // Outputs and scratch are never registered as stored objects; only document and import artifacts are.
            if (listing.Key.Area is ObjectArea.Export or ObjectArea.Production or ObjectArea.Tmp or ObjectArea.SnapshotManifest or ObjectArea.Report)
            {
                continue;
            }

            batch.Add(listing.Key.Value);
            if (batch.Count == 1_000)
            {
                orphans += batch.Count - (await store.RegisteredKeysAsync(workspaceId, batch, cancellationToken).ConfigureAwait(false)).Count;
                batch.Clear();
            }

            if (++listed >= MaxOrphanKeysListed)
            {
                break;
            }
        }

        if (batch.Count > 0)
        {
            orphans += batch.Count - (await store.RegisteredKeysAsync(workspaceId, batch, cancellationToken).ConfigureAwait(false)).Count;
        }

        return orphans;
    }

    private static AuditEvent RestoredEvent(RestoreRun run, Guid chain, long sequence, string hash)
    {
        var workspace = AuditChainFormat.WorkspaceIdOf(chain);
        var display = $"Restore tool ({run.OperatorName})";
        return new AuditEvent
        {
            // Stable per restore and chain, with a stable time, so a retried `restore record` does not write it twice.
            EventId = StableId(run.RestoreId, chain),
            WorkspaceId = workspace,
            OccurredAt = run.StartedAt,
            Category = AuditTaxonomy.Integrity.Category,
            Action = AuditTaxonomy.Integrity.Restored,
            ActorType = AuditActorType.Service,
            ActorId = OperationsActor.CliServiceId,
            ActorDisplay = display.Length <= AuditEventRules.MaxActorDisplayLength ? display : display[..AuditEventRules.MaxActorDisplayLength],
            ResourceType = workspace is null ? "Installation" : "Workspace",
            ResourceId = workspace?.ToString(),
            Outcome = AuditOutcome.Success,
            Details = new Dictionary<string, string?>
            {
                ["RestoreId"] = run.RestoreId.ToString("D"),
                ["Path"] = run.Path.ToString(),
                ["TargetTime"] = run.TargetTime is { } t ? Time(t) : null,
                ["BackupLabel"] = run.BackupLabel,
                ["Snapshot"] = run.SnapshotName,
                ["RestoredSequence"] = N(sequence),
                ["RestoredEventHash"] = hash,
            },
        };
    }

    private static Guid StableId(Guid restoreId, Guid chain)
    {
        Span<byte> input = stackalloc byte[32];
        restoreId.TryWriteBytes(input[..16]);
        chain.TryWriteBytes(input[16..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16]);
    }

    private static JsonObject ToJson(RestoreReconcileReport report)
    {
        var workspaces = new JsonArray();
        foreach (var w in report.Workspaces)
        {
            workspaces.Add(new JsonObject
            {
                ["workspaceId"] = w.WorkspaceId.ToString("D"),
                ["anchor"] = w.AnchorGeneration,
                ["outboxRepended"] = w.OutboxRepended,
                ["tasksRepended"] = w.TasksRepended,
                ["watermarkBefore"] = w.WatermarkBefore,
                ["watermarkAfter"] = w.WatermarkAfter,
                ["redriven"] = w.Redriven,
                ["breakGlassEnded"] = w.BreakGlassEnded,
                ["searchSessionsDropped"] = w.SearchSessionsDropped,
                ["reindexesAborted"] = w.ReindexesAborted,
            });
        }

        var lost = new JsonArray();
        foreach (var f in report.LostWindow)
        {
            lost.Add(new JsonObject { ["kind"] = f.Kind, ["workspaceId"] = f.WorkspaceId?.ToString("D"), ["subject"] = f.Subject });
        }

        return new JsonObject
        {
            ["workspaces"] = workspaces,
            ["purgedFromSearch"] = new JsonArray([.. report.PurgedFromSearch.Select(id => JsonValue.Create(id.ToString("D")))]),
            ["rebuildRequired"] = new JsonArray([.. report.RebuildRequired.Select(id => JsonValue.Create(id.ToString("D")))]),
            ["sessionsRevoked"] = report.SessionsRevoked,
            ["lostWindow"] = lost,
        };
    }

    private static void AddOnce(List<LostWindowFinding> findings, LostWindowFinding finding)
    {
        if (!findings.Any(f => f.Subject == finding.Subject))
        {
            findings.Add(finding);
        }
    }

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Time(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
