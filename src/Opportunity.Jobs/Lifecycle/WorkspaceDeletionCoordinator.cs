using System.Globalization;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

using Opportunity.Application.Audit;
using Opportunity.Application.Jobs;
using Opportunity.Application.Keys;
using Opportunity.Application.Search.Indexing;
using Opportunity.Application.Storage;

using Opportunity.Application.Workspaces;
using Opportunity.Application.Workspaces.Deletion;

namespace Opportunity.Jobs.Lifecycle;

/// <summary>
/// Drives approved workspace deletions through ADR-014 §4 (E20-T02). Every step is idempotent and its outcome is saved
/// in <c>workspace_deletion</c> / <c>workspace_deletion_step</c> under a coordinator lease, so a crashed or restarted
/// worker (or another replica, once the lease expired) resumes where the run stood:
/// <list type="number">
/// <item><b>Fence</b>: once approved and past the waiting period, the workspace becomes Deleting (new epoch) in the
/// transaction that checks the approval and the legal holds; workers stop at their next fence, the API answers 404,
/// and PostgreSQL refuses new jobs, documents, objects and search work for it.</item>
/// <item><b>Drain</b>: every unfinished job is cancelled; the step waits until no lease or claim of the workspace is
/// live and <see cref="WorkspaceDeletionOptions.DrainSettleDelay"/> has passed.</item>
/// <item><b>Inventory</b>: counts per table, index, storage area and key: the certificate's "before".</item>
/// <item><b>SearchPurge</b>, <b>DatabasePurge</b>, <b>StoragePurge</b>, <b>KeyDestruction</b> (crypto-shredding, PurgeAll
/// only): in that order, so search never outlives the rows that would tell a late writer to delete.</item>
/// <item><b>Verification</b>: after <see cref="WorkspaceDeletionOptions.ResurrectionGuardDelay"/>, a second search pass
/// removes anything a writer slipped in, then every store is counted again; a store with data left is purged again, up to
/// <see cref="WorkspaceDeletionOptions.MaxVerificationAttempts"/> times, after which the run ends with residuals.</item>
/// <item><b>Certification</b>: the destruction certificate (signed with the audit checkpoint key when one is available)
/// in PostgreSQL and under <c>sys/certificates/</c>; the workspace becomes Purged (a tombstone) and
/// <c>Workspace.Deleted</c> is audited in the workspace's chain and the installation's.</item>
/// </list>
/// Each destructive step re-checks the legal hold in the transaction that records its start (and the purge in every
/// batch): a hold placed during the run halts it (<c>Halted</c>) before the next destructive step, and the run resumes
/// once every hold is released.
/// </summary>
public sealed partial class WorkspaceDeletionCoordinator(
    IWorkspaceDeletionStore store,
    IWorkspaceSearchPurge search,
    IObjectStore objects,
    IWorkspaceCryptoShredder shredder,
    WorkspaceDeletionOptions options,
    TimeProvider time,
    ILogger<WorkspaceDeletionCoordinator> logger,
    ISigningKeyProvider? signer = null,
    IBeforeWorkspaceDeletion? beforePurge = null)
{
    public const string ServiceActorId = "service:workspace-deletion";
    private const string ServiceActorDisplay = "Workspace deletion";
    private const string ProductionsArea = "productions";

    // Bounds one pass over a run, so a pass that keeps advancing still yields to the other runs.
    private const int MaxStepsPerPass = 16;

    /// <summary>Identifies this coordinator in run leases.</summary>
    public string Owner { get; init; } = $"deletion:{Environment.MachineName}:{Guid.NewGuid():N}";

    /// <summary>One pass over every run due now; returns how many runs advanced.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var advanced = 0;
        foreach (var deletionId in await store.ListDueAsync(time.GetUtcNow(), cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (await StepAsync(deletionId, cancellationToken).ConfigureAwait(false))
                {
                    advanced++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The step is retried on the next pass; the error is shown on the deletion's status.
                LogStepFailed(logger, deletionId, ex);
                await TryRecordErrorAsync(deletionId, ex).ConfigureAwait(false);
            }
        }

        return advanced;
    }

    /// <summary>Advances one deletion as far as it can go now; true when anything changed.</summary>
    public async Task<bool> StepAsync(Guid deletionId, CancellationToken cancellationToken)
    {
        var changed = false;
        var leased = false;
        try
        {
            for (var i = 0; i < MaxStepsPerPass; i++)
            {
                var deletion = await store.TryLeaseAsync(deletionId, Owner, options.LeaseDuration, cancellationToken).ConfigureAwait(false);
                leased |= deletion is not null;
                if (deletion is null || !await AdvanceAsync(deletion, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                changed = true;
            }
        }
        finally
        {
            if (leased)
            {
                await store.ReleaseLeaseAsync(deletionId, Owner, CancellationToken.None).ConfigureAwait(false);
            }
        }

        return changed;
    }

    private async Task<bool> AdvanceAsync(WorkspaceDeletion deletion, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        switch (deletion.Status)
        {
            case WorkspaceDeletionStatus.Requested when deletion.ExpiresAt <= now:
                return await store.ExpireAsync(deletion.DeletionId, Owner, now, Audit(deletion, AuditTaxonomy.Workspace.DeletionCancelled,
                    new Dictionary<string, string?> { ["deletionId"] = Id(deletion) }, reasonCode: "Expired"), cancellationToken).ConfigureAwait(false);

            case WorkspaceDeletionStatus.Approved when deletion.RunNotBefore <= now:
                return await StartAsync(deletion, now, cancellationToken).ConfigureAwait(false);

            case WorkspaceDeletionStatus.Halted:
                var resumed = await store.ResumeAsync(deletion.DeletionId, Owner, Audit(deletion, AuditTaxonomy.Workspace.DeletionStarted,
                    new Dictionary<string, string?> { ["deletionId"] = Id(deletion), ["resumed"] = "true", ["step"] = deletion.Step?.ToString() }),
                    cancellationToken).ConfigureAwait(false);
                if (resumed)
                {
                    LogResumed(logger, deletion.DeletionId, deletion.WorkspaceId);
                }

                return resumed;

            case WorkspaceDeletionStatus.Running:
                try
                {
                    return await RunStepAsync(deletion, deletion.Step ?? DeletionStep.Drain, cancellationToken).ConfigureAwait(false);
                }
                catch (PreservationLockedException)
                {
                    await store.HaltAsync(deletion.DeletionId, Owner, time.GetUtcNow(), Audit(deletion, AuditTaxonomy.Workspace.DeletionHalted,
                        new Dictionary<string, string?> { ["deletionId"] = Id(deletion), ["step"] = (deletion.Step ?? DeletionStep.Drain).ToString() },
                        reasonCode: "LegalHold"), cancellationToken).ConfigureAwait(false);
                    LogHalted(logger, deletion.DeletionId, deletion.WorkspaceId);
                    return true;
                }

            default:
                return false;
        }
    }

    private async Task<bool> StartAsync(WorkspaceDeletion deletion, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var outcome = await store.StartAsync(deletion.DeletionId, Owner, now, Audit(deletion, AuditTaxonomy.Workspace.DeletionStarted,
            new Dictionary<string, string?>
            {
                ["deletionId"] = Id(deletion),
                ["retentionProfile"] = deletion.RetentionProfile.ToString(),
            }), cancellationToken).ConfigureAwait(false);
        switch (outcome)
        {
            case DeletionStartOutcome.Started:
                LogStarted(logger, deletion.DeletionId, deletion.WorkspaceId);
                return true;
            case DeletionStartOutcome.Held:
                // Not started; the store records the reason on the deletion (audited once) and the next pass checks again.
                LogWaitingForHold(logger, deletion.DeletionId, deletion.WorkspaceId);
                return false;
            default:
                return false;
        }
    }

    private Task<bool> RunStepAsync(WorkspaceDeletion deletion, DeletionStep step, CancellationToken cancellationToken) => step switch
    {
        DeletionStep.Fence or DeletionStep.Drain => DrainAsync(deletion, cancellationToken),
        DeletionStep.Inventory => InventoryAsync(deletion, cancellationToken),
        DeletionStep.SearchPurge => SearchPurgeAsync(deletion, cancellationToken),
        DeletionStep.DatabasePurge => DatabasePurgeAsync(deletion, cancellationToken),
        DeletionStep.StoragePurge => StoragePurgeAsync(deletion, cancellationToken),
        DeletionStep.KeyDestruction => KeyDestructionAsync(deletion, cancellationToken),
        DeletionStep.Verification => VerifyAsync(deletion, cancellationToken),
        DeletionStep.Certification => CertifyAsync(deletion, cancellationToken),
        _ => Task.FromResult(false),
    };

    private async Task<bool> DrainAsync(WorkspaceDeletion deletion, CancellationToken cancellationToken)
    {
        var record = await store.BeginStepAsync(deletion.DeletionId, Owner, DeletionStep.Drain, cancellationToken).ConfigureAwait(false);
        var cancelled = await store.CancelJobsAsync(deletion.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var inFlight = await store.CountWorkInFlightAsync(deletion.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (inFlight.Total > 0 || time.GetUtcNow() < record.StartedAt + options.DrainSettleDelay)
        {
            return false;
        }

        await CompleteAsync(deletion, DeletionStep.Drain, new JsonObject { ["jobsCancelled"] = cancelled }, DeletionStep.Inventory, null, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private async Task<bool> InventoryAsync(WorkspaceDeletion deletion, CancellationToken cancellationToken)
    {
        await store.BeginStepAsync(deletion.DeletionId, Owner, DeletionStep.Inventory, cancellationToken).ConfigureAwait(false);

        // Seam for the audit hash chain (#117): checkpoint the workspace's audit chain once it is fenced and drained,
        // before anything is purged. Wired at integration; idempotent, since a retried step calls it again.
        if (beforePurge is not null)
        {
            await beforePurge.BeforePurgeAsync(deletion.DeletionId, deletion.WorkspaceId, cancellationToken).ConfigureAwait(false);
        }

        var counts = new JsonObject
        {
            ["postgres"] = await PostgresCountsAsync(deletion, cancellationToken).ConfigureAwait(false),
            ["openSearch"] = SearchCounts(await search.CountAsync(deletion.WorkspaceId, cancellationToken).ConfigureAwait(false)),
            ["objects"] = await ObjectCountsAsync(deletion.WorkspaceId, cancellationToken).ConfigureAwait(false),
            ["keys"] = KeyCounts(await store.CountKeysAsync(deletion.WorkspaceId, cancellationToken).ConfigureAwait(false)),
        };
        await CompleteAsync(deletion, DeletionStep.Inventory, counts, DeletionStep.SearchPurge, null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> SearchPurgeAsync(WorkspaceDeletion deletion, CancellationToken cancellationToken)
    {
        await store.BeginStepAsync(deletion.DeletionId, Owner, DeletionStep.SearchPurge, cancellationToken).ConfigureAwait(false);
        var purged = await search.PurgeAsync(deletion.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var left = await search.CountAsync(deletion.WorkspaceId, cancellationToken).ConfigureAwait(false);
        await CompleteAsync(deletion, DeletionStep.SearchPurge, new JsonObject
        {
            ["indexesDeleted"] = purged.IndexesDeleted,
            ["documentsDeleted"] = purged.DocumentsDeleted,
            ["documentsAfter"] = left.Documents,
        }, DeletionStep.DatabasePurge, time.GetUtcNow() + options.ResurrectionGuardDelay, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> DatabasePurgeAsync(WorkspaceDeletion deletion, CancellationToken cancellationToken)
    {
        await store.BeginStepAsync(deletion.DeletionId, Owner, DeletionStep.DatabasePurge, cancellationToken).ConfigureAwait(false);
        var deleted = await PurgeDatabaseAsync(deletion, cancellationToken).ConfigureAwait(false);
        await CompleteAsync(deletion, DeletionStep.DatabasePurge, new JsonObject
        {
            ["rows"] = deleted.Sum(p => p.Value),
            ["deleted"] = new JsonObject([.. deleted.Where(p => p.Value > 0).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))]),
        }, DeletionStep.StoragePurge, null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> StoragePurgeAsync(WorkspaceDeletion deletion, CancellationToken cancellationToken)
    {
        await store.BeginStepAsync(deletion.DeletionId, Owner, DeletionStep.StoragePurge, cancellationToken).ConfigureAwait(false);
        var result = await PurgeStorageAsync(deletion, cancellationToken).ConfigureAwait(false);
        await CompleteAsync(deletion, DeletionStep.StoragePurge, result, DeletionStep.KeyDestruction, null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> KeyDestructionAsync(WorkspaceDeletion deletion, CancellationToken cancellationToken)
    {
        await store.BeginStepAsync(deletion.DeletionId, Owner, DeletionStep.KeyDestruction, cancellationToken).ConfigureAwait(false);
        JsonObject counts;
        if (deletion.RetentionProfile == DeletionRetentionProfile.PurgeAll)
        {
            var destroyed = await shredder.DestroyWorkspaceKeysAsync(deletion.WorkspaceId, KeyActor(deletion), cancellationToken).ConfigureAwait(false);
            counts = new JsonObject { ["destroyed"] = destroyed.DataKeysDestroyed, ["dedicatedKekDestroyed"] = destroyed.DedicatedKekDestroyed };
        }
        else
        {
            // Retained records are still encrypted with the workspace's data keys: they stay (the certificate says so).
            var keys = await store.CountKeysAsync(deletion.WorkspaceId, cancellationToken).ConfigureAwait(false);
            counts = new JsonObject { ["destroyed"] = 0, ["retained"] = keys.Active + keys.Retired, ["dedicatedKekDestroyed"] = false };
        }

        await CompleteAsync(deletion, DeletionStep.KeyDestruction, counts, DeletionStep.Verification, null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> VerifyAsync(WorkspaceDeletion deletion, CancellationToken cancellationToken)
    {
        if (deletion.NextStepAt is { } due && due > time.GetUtcNow())
        {
            return false;
        }

        await store.BeginStepAsync(deletion.DeletionId, Owner, DeletionStep.Verification, cancellationToken).ConfigureAwait(false);
        long secondPassDocuments = 0;
        var secondPassIndexes = 0;
        JsonObject counts = [];
        var residual = true;
        var attempts = 0;
        while (residual && attempts < options.MaxVerificationAttempts)
        {
            attempts++;
            if (attempts > 1)
            {
                // Something was left: run the destructive steps again before counting again.
                LogResidualsRetried(logger, deletion.DeletionId, attempts);
                await PurgeDatabaseAsync(deletion, cancellationToken).ConfigureAwait(false);
                await PurgeStorageAsync(deletion, cancellationToken).ConfigureAwait(false);
                if (deletion.RetentionProfile == DeletionRetentionProfile.PurgeAll)
                {
                    await shredder.DestroyWorkspaceKeysAsync(deletion.WorkspaceId, KeyActor(deletion), cancellationToken).ConfigureAwait(false);
                }
            }

            // The second search pass: removes whatever a writer that passed its fence just before the fence still sent.
            var pass = await search.PurgeAsync(deletion.WorkspaceId, cancellationToken).ConfigureAwait(false);
            secondPassDocuments += pass.DocumentsDeleted;
            secondPassIndexes += pass.IndexesDeleted;
            (counts, residual) = await CountAfterAsync(deletion, cancellationToken).ConfigureAwait(false);
        }

        counts["openSearch"]!["secondPassDocumentsDeleted"] = secondPassDocuments;
        counts["openSearch"]!["secondPassIndexesDeleted"] = secondPassIndexes;
        counts["attempts"] = attempts;
        counts["residual"] = residual;
        await CompleteAsync(deletion, DeletionStep.Verification, counts, DeletionStep.Certification, null, cancellationToken,
            residual ? DeletionStepOutcomes.Residuals : DeletionStepOutcomes.Success).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> CertifyAsync(WorkspaceDeletion deletion, CancellationToken cancellationToken)
    {
        var record = await store.BeginStepAsync(deletion.DeletionId, Owner, DeletionStep.Certification, cancellationToken).ConfigureAwait(false);
        var steps = await store.GetStepsAsync(deletion.DeletionId, cancellationToken).ConfigureAwait(false);
        var residual = steps.FirstOrDefault(s => s.Step == DeletionStep.Verification)?.Outcome == DeletionStepOutcomes.Residuals;

        // Issued at the step's recorded start, so a retry after a crash rebuilds the same bytes (and object key).
        var issuedAt = record.StartedAt;
        var built = DestructionCertificate.Build(deletion, [.. steps.Where(s => s.Step != DeletionStep.Certification)], residual, issuedAt, options);
        var key = ObjectKeys.DestructionCertificate(deletion.DeletionId, Sha256Digest.FromBytes(built.Sha256));
        using (var content = new MemoryStream(built.Bytes, writable: false))
        {
            await objects.PutAsync(key, content, new PutObjectOptions
            {
                ContentType = DestructionCertificate.MediaType,
                ExpectedSha256 = Sha256Digest.FromBytes(built.Sha256),
                ExpectedLength = built.Bytes.Length,
            }, cancellationToken).ConfigureAwait(false);
        }

        var signature = await SignAsync(built.Bytes, cancellationToken).ConfigureAwait(false);

        var certificate = new StoredDestructionCertificate(
            deletion.DeletionId, deletion.WorkspaceId, issuedAt, built.Sha256, System.Text.Encoding.UTF8.GetString(built.Bytes), key.Value,
            signature?.KeyId, signature?.Value);
        var details = new Dictionary<string, string?>
        {
            ["deletionId"] = Id(deletion),
            ["certificateId"] = Id(deletion),
            ["certificateSha256"] = built.Sha256Hex,
            ["outcome"] = residual ? nameof(WorkspaceDeletionStatus.CompletedWithResiduals) : nameof(WorkspaceDeletionStatus.Completed),
            ["retentionProfile"] = deletion.RetentionProfile.ToString(),
        };
        await store.CertifyAsync(deletion.DeletionId, Owner, certificate, residual, issuedAt,
            Audit(deletion, AuditTaxonomy.Workspace.Deleted, details), cancellationToken).ConfigureAwait(false);

        // The installation chain gets the certificate hash too (ADR-014 §8); a fixed event id keeps a retry from duplicating it.
        await store.WriteInstallationAuditAsync(Audit(deletion, AuditTaxonomy.Workspace.Deleted, details) with
        {
            EventId = CertificateEventId(deletion.DeletionId),
            WorkspaceId = null,
            OccurredAt = issuedAt,
        }, cancellationToken).ConfigureAwait(false);
        LogCertified(logger, deletion.DeletionId, deletion.WorkspaceId, residual);
        return true;
    }

    /// <summary>
    /// Signs the certificate with the audit checkpoint key (ADR-014 §8). Best effort: an installation without a usable
    /// signing key still gets its certificate, unsigned, with its hash in both audit chains.
    /// </summary>
    private async Task<KeySignature?> SignAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        if (signer is null)
        {
            return null;
        }

        try
        {
            return await signer.SignAsync(SigningKeyPurposes.AuditCheckpoint, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is KeyUnavailableException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            LogUnsigned(logger, ex);
            return null;
        }
    }

    /// <summary>The installation-chain event id of a deletion's certificate (stable across retries).</summary>
    public static Guid CertificateEventId(Guid deletionId)
    {
        var bytes = deletionId.ToByteArray();
        bytes[15] ^= 0x5A;
        return new Guid(bytes);
    }

    private async Task<Dictionary<string, long>> PurgeDatabaseAsync(WorkspaceDeletion deletion, CancellationToken cancellationToken)
    {
        var (tables, references) = await store.GetPurgeSchemaAsync(cancellationToken).ConfigureAwait(false);
        var plan = WorkspacePurgePlan.Order(tables, references);
        var deleted = new Dictionary<string, long>(StringComparer.Ordinal);
        var renewAt = time.GetUtcNow() + options.LeaseDuration / 3;
        foreach (var table in plan)
        {
            long total = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = await store.PurgeBatchAsync(deletion.DeletionId, table, options.BatchSize, cancellationToken).ConfigureAwait(false);
                total += batch;
                if (time.GetUtcNow() >= renewAt)
                {
                    _ = await store.TryLeaseAsync(deletion.DeletionId, Owner, options.LeaseDuration, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException($"The lease on deletion {deletion.DeletionId} was lost.");
                    renewAt = time.GetUtcNow() + options.LeaseDuration / 3;
                }

                if (batch == 0)
                {
                    break;
                }
            }

            deleted[table] = total;
        }

        return deleted;
    }

    private async Task<JsonObject> PurgeStorageAsync(WorkspaceDeletion deletion, CancellationToken cancellationToken)
    {
        IEnumerable<ObjectPrefix> prefixes = deletion.RetentionProfile == DeletionRetentionProfile.PurgeAll
            ? [ObjectPrefix.Workspace(deletion.WorkspaceId)]
            : ObjectPrefix.WorkspaceAreas.Where(a => a != ProductionsArea).Order(StringComparer.Ordinal)
                .Select(a => ObjectPrefix.WorkspaceArea(deletion.WorkspaceId, a));
        long count = 0, bytes = 0;
        var areas = new JsonArray();
        var residuals = new JsonArray();
        foreach (var prefix in prefixes)
        {
            var result = await objects.DeletePrefixAsync(prefix, cancellationToken).ConfigureAwait(false);
            count += result.ObjectCount;
            bytes += result.ByteCount;
            areas.Add(prefix.Value.Split('/')[2] is { Length: > 0 } area ? area : "*");
            foreach (var residual in result.Residuals)
            {
                residuals.Add(residual);
            }
        }

        return new JsonObject { ["objects"] = count, ["bytes"] = bytes, ["areas"] = areas, ["providerResiduals"] = residuals };
    }

    private async Task<(JsonObject Counts, bool Residual)> CountAfterAsync(WorkspaceDeletion deletion, CancellationToken cancellationToken)
    {
        var rows = await store.CountRowsAsync(deletion.DeletionId, cancellationToken).ConfigureAwait(false);
        var index = await search.CountAsync(deletion.WorkspaceId, cancellationToken).ConfigureAwait(false);
        long purgedAreaObjects = 0, retainedObjects = 0;
        await foreach (var listing in objects.ListPrefixAsync(ObjectPrefix.Workspace(deletion.WorkspaceId), cancellationToken).ConfigureAwait(false))
        {
            if (deletion.RetentionProfile == DeletionRetentionProfile.RetainRecords && AreaOf(listing.Key) == ProductionsArea)
            {
                retainedObjects++;
            }
            else
            {
                purgedAreaObjects++;
            }
        }

        var keys = await store.CountKeysAsync(deletion.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var usableKeys = keys.Active + keys.Retired;
        var pgResidual = rows.Sum(r => r.Purgeable);
        var keyResidual = deletion.RetentionProfile == DeletionRetentionProfile.PurgeAll && usableKeys > 0;
        var counts = new JsonObject
        {
            ["postgres"] = PostgresJson(rows),
            ["openSearch"] = SearchCounts(index),
            ["objects"] = new JsonObject { ["objects"] = purgedAreaObjects, ["retainedObjects"] = retainedObjects },
            ["keys"] = new JsonObject { ["usable"] = usableKeys, ["destroyed"] = keys.Destroyed },
            ["residualRows"] = pgResidual,
        };
        return (counts, pgResidual > 0 || !index.IsEmpty || purgedAreaObjects > 0 || keyResidual);
    }

    private async Task<JsonObject> PostgresCountsAsync(WorkspaceDeletion deletion, CancellationToken cancellationToken) =>
        PostgresJson(await store.CountRowsAsync(deletion.DeletionId, cancellationToken).ConfigureAwait(false));

    private static JsonObject PostgresJson(IReadOnlyList<PurgeTableCount> rows) =>
        new([.. rows.Where(r => r.Total > 0).Select(r => KeyValuePair.Create(r.Table, (JsonNode?)new JsonObject
        {
            ["total"] = r.Total,
            ["retained"] = r.Retained,
        }))]);

    private async Task<JsonObject> ObjectCountsAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        long count = 0, bytes = 0;
        var areas = new SortedDictionary<string, (long Objects, long Bytes)>(StringComparer.Ordinal);
        await foreach (var listing in objects.ListPrefixAsync(ObjectPrefix.Workspace(workspaceId), cancellationToken).ConfigureAwait(false))
        {
            count++;
            bytes += listing.Length;
            var area = AreaOf(listing.Key);
            var (o, b) = areas.GetValueOrDefault(area);
            areas[area] = (o + 1, b + listing.Length);
        }

        return new JsonObject
        {
            ["objects"] = count,
            ["bytes"] = bytes,
            ["areas"] = new JsonObject([.. areas.Select(a => KeyValuePair.Create(a.Key, (JsonNode?)new JsonObject
            {
                ["objects"] = a.Value.Objects,
                ["bytes"] = a.Value.Bytes,
            }))]),
        };
    }

    private static string AreaOf(ObjectKey key) => key.Value.Split('/') is { Length: > 2 } parts ? parts[2] : string.Empty;

    private static JsonObject SearchCounts(WorkspaceSearchInventory inventory) => new()
    {
        ["documents"] = inventory.Documents,
        ["dedicatedIndexes"] = inventory.DedicatedIndexes,
    };

    private static JsonObject KeyCounts(WorkspaceKeyCounts keys) => new()
    {
        ["total"] = keys.Total,
        ["active"] = keys.Active,
        ["retired"] = keys.Retired,
        ["destroyed"] = keys.Destroyed,
    };

    private Task CompleteAsync(
        WorkspaceDeletion deletion, DeletionStep step, JsonObject counts, DeletionStep next, DateTimeOffset? nextStepAt, CancellationToken cancellationToken,
        string outcome = DeletionStepOutcomes.Success) =>
        store.CompleteStepAsync(deletion.DeletionId, Owner, step, outcome, counts, null, next, nextStepAt,
            Audit(deletion, AuditTaxonomy.Workspace.DeletionStepCompleted, new Dictionary<string, string?>
            {
                ["deletionId"] = Id(deletion),
                ["step"] = step.ToString(),
                ["outcome"] = outcome,
            }), cancellationToken);

    /// <summary>Key destruction is recorded as done on behalf of the approver, who authorized the run.</summary>
    private static OperationsActor KeyActor(WorkspaceDeletion deletion) =>
        deletion.ApprovedBy is { } approver ? OperationsActor.User(approver) : OperationsActor.Cli(ServiceActorDisplay);

    private AuditEvent Audit(WorkspaceDeletion deletion, string action, Dictionary<string, string?> details, string? reasonCode = null) => new()
    {
        WorkspaceId = deletion.WorkspaceId,
        OccurredAt = time.GetUtcNow(),
        Category = AuditTaxonomy.Workspace.Category,
        Action = action,
        ActorType = AuditActorType.Service,
        ActorId = ServiceActorId,
        ActorDisplay = ServiceActorDisplay,
        ResourceType = AuditTaxonomy.Workspace.DeletionResourceType,
        ResourceId = Id(deletion),
        Outcome = AuditOutcome.Success,
        ReasonCode = reasonCode,
        Details = details,
    };

    private async Task TryRecordErrorAsync(Guid deletionId, Exception ex)
    {
        try
        {
            var message = ex.GetType().Name + ": " + ex.Message;
            await store.RecordErrorAsync(deletionId, Owner, message.Length > 500 ? message[..500] : message, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The error is only shown on the status; the run is retried either way.
        catch (Exception recordFailure)
#pragma warning restore CA1031
        {
            LogStepFailed(logger, deletionId, recordFailure);
        }
    }

    private static string Id(WorkspaceDeletion deletion) => deletion.DeletionId.ToString("D", CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Workspace deletion {DeletionId} step failed; retried at the next pass")]
    private static partial void LogStepFailed(ILogger logger, Guid deletionId, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workspace deletion {DeletionId} fenced workspace {WorkspaceId}")]
    private static partial void LogStarted(ILogger logger, Guid deletionId, Guid workspaceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Workspace deletion {DeletionId} waits: workspace {WorkspaceId} is under a legal hold")]
    private static partial void LogWaitingForHold(ILogger logger, Guid deletionId, Guid workspaceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Workspace deletion {DeletionId} halted: workspace {WorkspaceId} was placed under a legal hold")]
    private static partial void LogHalted(ILogger logger, Guid deletionId, Guid workspaceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workspace deletion {DeletionId} resumed for workspace {WorkspaceId}")]
    private static partial void LogResumed(ILogger logger, Guid deletionId, Guid workspaceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Workspace deletion {DeletionId} found data left; purge attempt {Attempt}")]
    private static partial void LogResidualsRetried(ILogger logger, Guid deletionId, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The destruction certificate is not signed: the audit checkpoint key is unavailable")]
    private static partial void LogUnsigned(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workspace deletion {DeletionId} certified for workspace {WorkspaceId} (residuals: {Residuals})")]
    private static partial void LogCertified(ILogger logger, Guid deletionId, Guid workspaceId, bool residuals);
}
