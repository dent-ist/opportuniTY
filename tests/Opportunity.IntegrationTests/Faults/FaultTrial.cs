#if OPPORTUNITY_FAILPOINTS
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Opportunity.Application.Authorization;
using Opportunity.Application.Coding;
using Opportunity.Application.Jobs;
using Opportunity.Application.Search;
using Opportunity.Application.Search.Indexing;
using Opportunity.Application.Snapshots;
using Opportunity.Contracts.Search;
using Opportunity.Core.Coding;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.Correctness.ShadowLedger;
using Opportunity.IntegrationTests.Coding;
using Opportunity.IntegrationTests.Search;
using Opportunity.Search.Indexing;

namespace Opportunity.IntegrationTests.Faults;

/// <summary>The outcome of one seeded trial: pass/fail reasons, the oracle's verdict and the security counters.</summary>
internal sealed record TrialOutcome(
    FaultCell Cell, long Seed, bool Passed, IReadOnlyList<string> Failures, ShadowLedgerVerdict? Verdict, long SecurityChecks, long UnauthorizedRetrievals,
    string FiredAt, TimeSpan Duration)
{
    public string Replay =>
        $"{FaultMatrix.SeedVariable}={Seed} {FaultMatrix.CellsVariable}='{Cell}' dotnet test --project tests/Opportunity.IntegrationTests -- --filter-trait \"Category=FaultMatrix\"";

    public override string ToString() =>
        $"{(Passed ? "PASS" : "FAIL")} {Cell} seed {Seed} in {Duration.TotalSeconds:F1} s; fired at {FiredAt}; security {SecurityChecks} checks, {UnauthorizedRetrievals} unauthorized; " +
        (Verdict?.ToString() ?? "no verdict") + (Passed ? string.Empty : "\n  - " + string.Join("\n  - ", Failures) + "\n  replay: " + Replay);
}

/// <summary>
/// One seeded trial of a matrix cell (E18-T01). The seed fixes the workload (document count, chunk size, which documents
/// are edited, the values and the pacing) and the fault (which hit of the failpoint, window lengths, withheld items);
/// thread interleaving is not controlled, so a failure is replayed by re-running its seed until it reproduces.
/// Pass criteria, all mechanical:
/// <list type="number">
/// <item>Shadow-ledger oracle (E17-T07): 0 stale overwrites, regressions, missing documents and value mismatches.</item>
/// <item>No-fault reference: the final PostgreSQL coding equals what the workload must produce in any interleaving
/// (bulk values, interactive edits winning per field under Q-07).</item>
/// <item>Idempotency: no duplicate CodingEvent per idempotency key, document and field; the job's chunks commit exactly
/// once (counters add up); every JobChunk, IndexChunkTask and SearchOutbox row reaches its terminal success state.</item>
/// <item>Authorization (§26 "0 unauthorized retrievals"): the restricted document never reaches a reviewer without its
/// grant, no other workspace's caller sees any document, and the PDP still decides from authoritative state.</item>
/// </list>
/// </summary>
internal static class FaultTrial
{
    private const string StaleRejections = "opportunity.search.stale_version_rejections";
    private static readonly TimeSpan Quiescence = TimeSpan.FromSeconds(120);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<TrialOutcome> RunAsync(FaultWorldFixture world, FaultCell cell, long seed)
    {
        var clock = Stopwatch.StartNew();
        var random = new Random(unchecked((int)(seed ^ (seed >> 32))));
        var failures = new List<string>();
        var bulk = BulkCodingHarness.Over(world.Authz);
        await using var _ = bulk;

        // ---- Arrange (no fault armed) --------------------------------------------------------------------------------
        var w = await CodingApiHarness.WorkspaceAsync(world.Core);
        await world.Api.GetRequiredService<IWorkspaceSearchPlacement>().PlaceAsync(w.Id, new WorkspacePlacementRequest(false), Ct);
        var qc = await bulk.MemberAsync(w.Id, WorkspaceRole.QcReviewer);
        var reviewer = await bulk.MemberAsync(w.Id, WorkspaceRole.Reviewer);
        var neighbor = await world.Authz.Core.CreateWorkspaceAsync();
        var outsider = await bulk.MemberAsync(neighbor, WorkspaceRole.Reviewer);
        var count = 30 + random.Next(21);
        var docs = await bulk.DocumentsAsync(w.Id, count, "F" + seed.ToString("x", CultureInfo.InvariantCulture)[..6].ToUpperInvariant());
        var restricted = docs[random.Next(docs.Count)];
        await world.Core.ExecuteAsync(
            "INSERT INTO opportunity.document_restriction (workspace_id, document_id, class_key) VALUES (@ws, @doc, @class)",
            ("ws", w.Id), ("doc", restricted), ("class", RestrictionClasses.AttorneysEyesOnly));
        var runsBulk = cell.Workload is FaultWorkload.Mixed or FaultWorkload.Bulk;
        var runsInteractive = cell.Workload is FaultWorkload.Mixed or FaultWorkload.Interactive;
        var snapshot = runsBulk ? await bulk.SnapshotAsync(w.Id, qc, docs) : null;

        var target = new LedgerIndexTarget(
            (await world.Api.GetRequiredService<IIndexManager>().ResolveAsync(w.Id, IndexPurpose.Read, Ct)).Read.Index, w.Id.ToString("D"));
        await using var oracle = await LedgerSupport.StartAsync(
            world.Core.DataSource, world.OpenSearchHttp, w.Id, target, () => world.Meters.Sum(StaleRejections), seed);
        oracle.StartSampling();

        // ---- Act: the workload under the armed fault ------------------------------------------------------------------
        world.Injector.Arm(new FaultPlan(cell.Fault, cell.Failpoint, 1 + random.Next(2), w.Id, seed));
        var expected = docs.ToDictionary(d => d, _ => new Dictionary<int, string?>());
        Guid? jobId = null;
        try
        {
            var edits = runsInteractive ? Edits(random, docs, restricted, w, runsBulk) : [];
            if (runsBulk)
            {
                jobId = await SubmitAsync(bulk, w, qc, snapshot!, chunkSize: 10 + random.Next(9));
                // The restricted document is excluded for the job's initiator, who lacks its grant (ADR-015 D9.4).
                foreach (var doc in docs.Where(d => d != restricted))
                {
                    expected[doc][w.Responsive] = "true";
                    expected[doc][w.Issues] = $"[{w.IssueA}]";
                }
            }

            foreach (var (doc, operation, delay) in edits)
            {
                await Task.Delay(delay, Ct);
                await ApplyWithRetryAsync(world, w.Id, reviewer, doc, operation);
                expected[doc][operation.FieldId] = operation.Value?.ToJsonString();
            }

            await QuiesceAsync(world, w.Id, jobId, failures);
        }
        finally
        {
            world.Injector.Disarm();
            try
            {
                await world.AwaitBackgroundAsync(TimeSpan.FromSeconds(90));
            }
            finally
            {
                await world.RestoreAllAsync();
            }
        }

        if (!world.Injector.Fired)
        {
            failures.Add($"the fault never fired: {cell.Failpoint} was hit {world.Injector.Hits.GetValueOrDefault(cell.Failpoint)} time(s)");
        }

        // ---- Assert -----------------------------------------------------------------------------------------------------
        var verdict = await oracle.ReconcileAsync(Ct);
        await LedgerSupport.KeepAsync(verdict, $"{cell.Fault}-{cell.Failpoint}-{cell.Workload}-{seed}");
        if (!verdict.Passed)
        {
            failures.Add("shadow ledger: " + verdict);
        }

        await CheckReferenceAsync(world, w, docs, expected, failures);
        await CheckIdempotencyAsync(world, w.Id, jobId, docs.Count, failures);
        // Only documents whose coding changed are indexed (the trial inserts its documents without an import).
        var indexed = (await world.Core.ColumnAsync(
                $"SELECT document_id::text FROM opportunity.document_projection_state WHERE workspace_id = '{w.Id}' AND document_version > 1"))
            .Select(Guid.Parse).ToList();
        var (checks, unauthorized) = await CheckAuthorizationAsync(world, w.Id, neighbor, docs, indexed, restricted, reviewer, outsider, failures);
        if (unauthorized > 0)
        {
            failures.Add($"{unauthorized} unauthorized protected-resource retrieval(s)");
        }

        return new TrialOutcome(cell, seed, failures.Count == 0, failures, verdict, checks, unauthorized, world.Injector.FiredAt ?? "-", clock.Elapsed);
    }

    /// <summary>
    /// 6–12 seeded interactive edits: Responsive (L1 lane) or the security-affecting Confidentiality (L0 lane), on
    /// documents the bulk job also codes when both run; paced 0–150 ms apart so they interleave with the job.
    /// </summary>
    private static List<(Guid Doc, CodingFieldOperation Operation, TimeSpan Delay)> Edits(
        Random random, IReadOnlyList<Guid> docs, Guid restricted, CodingWorkspace w, bool withBulk)
    {
        var edits = new List<(Guid, CodingFieldOperation, TimeSpan)>();
        var touched = docs.Where(d => d != restricted).OrderBy(_ => random.Next()).Take(4 + random.Next(5)).Append(restricted).ToList();
        for (var i = 6 + random.Next(7); i > 0; i--)
        {
            var doc = touched[random.Next(touched.Count)];
            var operation = random.Next(3) switch
            {
                0 => CodingFieldOperation.Set(w.Confidentiality, JsonValue.Create(random.Next(2) == 0 ? w.Confidential : w.AttorneysEyesOnly)),
                1 when !withBulk => CodingFieldOperation.Clear(w.Responsive),
                _ => CodingFieldOperation.Set(w.Responsive, JsonValue.Create(random.Next(2) == 0)),
            };
            edits.Add((doc, operation, TimeSpan.FromMilliseconds(random.Next(150))));
        }

        return edits;
    }

    /// <summary>The bulk job as the Mass Edit use case creates it, with small chunks (several per trial).</summary>
    private static async Task<Guid> SubmitAsync(BulkCodingHarness bulk, CodingWorkspace w, Guid qc, SnapshotRecord snapshot, int chunkSize)
    {
        CodingFieldOperation[] operations = [CodingFieldOperation.Set(w.Responsive, JsonValue.Create(true)), CodingFieldOperation.AddChoices(w.Issues, w.IssueA)];
        var key = "mass-edit-" + Guid.CreateVersion7().ToString("N");
        var plans = ChunkPlanner.SplitByCount(1, snapshot.DocumentCount!.Value, chunkSize)
            .Select(r => new ChunkPlan(ChunkMembership.SnapshotRange(snapshot.SnapshotId, r.From, r.To), checked((int)r.Count)))
            .ToList();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Each step is idempotent (client key, conditional transitions): a retry after a lost connection finishes it.
                var created = await bulk.Jobs.CreateAsync(new NewJob
                {
                    WorkspaceId = w.Id,
                    JobType = JobType.BulkCoding,
                    InitiatedBy = qc,
                    TargetSnapshotId = snapshot.SnapshotId,
                    Parameters = BulkCodingParameters.ToJson(operations, securityAffecting: false),
                    ClientIdempotencyKey = key,
                }, Ct);
                await bulk.Jobs.BeginPreparingAsync(w.Id, created.Job.JobId, Ct);
                await bulk.Jobs.StartAsync(new JobStartRequest(w.Id, created.Job.JobId, ChunkOperationKind.BulkCodingChunk, plans), Ct);
                return created.Job.JobId;
            }
            catch (NpgsqlException) when (attempt < 120)
            {
                await Task.Delay(250, Ct);
            }
        }
    }

    /// <summary>An interactive save, retried with the same idempotency key while the store is unreachable (as a client would).</summary>
    private static async Task ApplyWithRetryAsync(FaultWorldFixture world, Guid ws, Guid user, Guid doc, CodingFieldOperation operation)
    {
        var request = new CodingWriteRequest
        {
            WorkspaceId = ws,
            IdempotencyKey = "ui-" + Guid.CreateVersion7().ToString("N"),
            Actor = new CodingActor(user, CodingActorType.Human),
            Documents = [new CodingTarget(doc)],
            Operations = [operation],
        };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            try
            {
                var result = await world.Core.Coding.ApplyAsync(request, Ct);
                if (result.Outcome == CodingWriteOutcome.Applied)
                {
                    return;
                }

                throw new InvalidOperationException($"interactive coding refused: {result.Outcome}");
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250, Ct);
            }
        }
    }

    private static async Task QuiesceAsync(FaultWorldFixture world, Guid ws, Guid? jobId, List<string> failures)
    {
        var deadline = DateTime.UtcNow + Quiescence;
        string state = "unknown";
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                state = await world.Core.ScalarAsync<string>(
                    """
                    SELECT concat_ws(' ',
                        'job=' || coalesce((SELECT status FROM opportunity.job WHERE workspace_id = @ws AND job_id = @job), '-'),
                        'chunks-open=' || (SELECT count(*) FROM opportunity.job_chunk WHERE workspace_id = @ws AND status NOT IN (5, 6, 7)),
                        'tasks-open=' || (SELECT count(*) FROM opportunity.index_chunk_task WHERE workspace_id = @ws AND status NOT IN (5, 6)),
                        'outbox-open=' || (SELECT count(*) FROM opportunity.search_outbox WHERE workspace_id = @ws AND status NOT IN (4, 5)))
                    """,
                    ("ws", ws), ("job", jobId ?? Guid.Empty));
                var settled = state.Contains("chunks-open=0", StringComparison.Ordinal) && state.Contains("tasks-open=0", StringComparison.Ordinal)
                    && state.Contains("outbox-open=0", StringComparison.Ordinal)
                    && (jobId is null || state.Contains("job=Completed", StringComparison.Ordinal) || state.Contains("job=CompletedWithErrors", StringComparison.Ordinal)
                        || state.Contains("job=Failed", StringComparison.Ordinal));
                if (settled)
                {
                    return;
                }
            }
            catch (NpgsqlException)
            {
                // PostgreSQL is restarting under the fault; keep waiting.
            }

            await Task.Delay(200, Ct);
        }

        failures.Add($"no quiescence within {Quiescence.TotalSeconds:F0} s: {state}");
    }

    private static async Task CheckReferenceAsync(
        FaultWorldFixture world, CodingWorkspace w, IReadOnlyList<Guid> docs, Dictionary<Guid, Dictionary<int, string?>> expected, List<string> failures)
    {
        var actual = await world.Core.Coding.GetCurrentAsync(w.Id, docs, Ct);
        var mismatches = new List<string>();
        foreach (var document in actual)
        {
            foreach (var field in new[] { w.Responsive, w.Issues, w.Confidentiality })
            {
                var want = expected[document.DocumentId].GetValueOrDefault(field);
                var have = document.Fields.SingleOrDefault(f => f.FieldId == field)?.Value?.ToJsonString();
                if (want != have)
                {
                    mismatches.Add($"{document.DocumentId} field {field}: expected {want ?? "absent"}, PostgreSQL has {have ?? "absent"}");
                }
            }
        }

        if (mismatches.Count > 0)
        {
            failures.Add($"final PostgreSQL state differs from the no-fault reference in {mismatches.Count} value(s); first: {mismatches[0]}");
        }
    }

    private static async Task CheckIdempotencyAsync(FaultWorldFixture world, Guid ws, Guid? jobId, int documents, List<string> failures)
    {
        var duplicates = await world.Core.ScalarAsync<long>(
            """
            SELECT count(*) FROM (
                SELECT 1 FROM opportunity.coding_event WHERE workspace_id = @ws
                GROUP BY idempotency_key, document_id, field_id, event_kind HAVING count(*) > 1) d
            """,
            ("ws", ws));
        if (duplicates > 0)
        {
            failures.Add($"{duplicates} duplicate CodingEvent(s) for one idempotency key, document and field");
        }

        var unsettled = await world.Core.ScalarAsync<string>(
            """
            SELECT concat_ws(' ',
                'chunks-not-committed=' || (SELECT count(*) FROM opportunity.job_chunk WHERE workspace_id = @ws AND status <> 5),
                'tasks-not-applied=' || (SELECT count(*) FROM opportunity.index_chunk_task WHERE workspace_id = @ws AND status <> 5),
                'outbox-not-applied=' || (SELECT count(*) FROM opportunity.search_outbox WHERE workspace_id = @ws AND status <> 4))
            """,
            ("ws", ws));
        if (unsettled != "chunks-not-committed=0 tasks-not-applied=0 outbox-not-applied=0")
        {
            failures.Add("work records did not all reach their terminal success state: " + unsettled);
        }

        if (jobId is not { } job)
        {
            return;
        }

        var info = (await new Data.Jobs.JobRepository(world.Core.AppDataSource).GetAsync(ws, job, Ct))!;
        var c = info.Counters;
        if (info.Status != JobStatus.Completed || c.ChunksCommitted != c.ChunksTotal
            || c.ItemsApplied + c.ItemsUnchanged + c.ItemsSkippedConcurrentEdit + c.ItemsExcludedNoAccess + c.ItemsFailed != documents
            || c.IndexTasksApplied != c.IndexTasksTotal)
        {
            failures.Add($"job {info.Status}: chunks {c.ChunksCommitted}/{c.ChunksTotal}, items applied {c.ItemsApplied} unchanged {c.ItemsUnchanged} " +
                $"skipped {c.ItemsSkippedConcurrentEdit} excluded {c.ItemsExcludedNoAccess} failed {c.ItemsFailed} of {documents}, " +
                $"index tasks {c.IndexTasksApplied}/{c.IndexTasksTotal}");
        }

        var doubleProvenance = await world.Core.ScalarAsync<long>(
            """
            SELECT count(*) FROM (
                SELECT 1 FROM opportunity.coding_event WHERE workspace_id = @ws AND job_id = @job AND event_kind = 1
                GROUP BY document_id, field_id HAVING count(*) > 1) d
            """,
            ("ws", ws), ("job", job));
        if (doubleProvenance > 0)
        {
            failures.Add($"{doubleProvenance} document field(s) carry two job CodingEvents (ADR-010 §5.4)");
        }
    }

    /// <summary>
    /// §26 "0 unauthorized protected-resource retrievals", judged after the faults: search results and PDP decisions for
    /// a reviewer without the restricted document's grant, and for a caller from another workspace.
    /// </summary>
    private static async Task<(long Checks, long Unauthorized)> CheckAuthorizationAsync(
        FaultWorldFixture world, Guid ws, Guid neighbor, IReadOnlyList<Guid> docs, IReadOnlyList<Guid> indexed, Guid restricted, Guid reviewer, Guid outsider,
        List<string> failures)
    {
        long checks = 0, unauthorized = 0;
        var visible = indexed.Where(d => d != restricted).ToHashSet();
        var allowedHits = docs.Where(d => d != restricted).ToHashSet();

        // The reviewer's search: every hit is a workspace document the reviewer may see; eventually all of them.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        IReadOnlyList<Guid> hits = [];
        do
        {
            var outcome = await SearchAsync(world, ws, reviewer);
            hits = outcome.Status == SearchStatus.Ok ? [.. outcome.Page!.Items.Select(i => i.DocumentId)] : [];
            checks += hits.Count;
            unauthorized += hits.Count(h => !allowedHits.Contains(h));
            if (hits.ToHashSet().SetEquals(visible))
            {
                break;
            }

            await Task.Delay(250, Ct);
        }
        while (DateTime.UtcNow < deadline);

        if (!hits.ToHashSet().SetEquals(visible))
        {
            failures.Add($"the reviewer's search returned {hits.Count} of the {visible.Count} documents they may see");
        }

        // Callers of another workspace see nothing of this one.
        foreach (var (workspace, user) in new[] { (ws, outsider), (neighbor, reviewer) })
        {
            var outcome = await SearchAsync(world, workspace, user);
            checks++;
            if (outcome.Status == SearchStatus.Ok && outcome.Page!.Items.Count > 0)
            {
                unauthorized += outcome.Page.Items.Count;
            }
        }

        // The PDP decides from authoritative PostgreSQL state, whatever the index holds.
        await using (var scope = world.Api.CreateAsyncScope())
        {
            var decisions = await scope.ServiceProvider.GetRequiredService<IAuthorizationService>().AuthorizeManyAsync(
                Principal(reviewer), ws, Permission.DocumentView, docs, DenialAudit.PerDocument, Ct);
            foreach (var doc in docs)
            {
                checks++;
                var allowed = decisions.GetValueOrDefault(doc).IsAllowed;
                if (allowed && doc == restricted)
                {
                    unauthorized++;
                }
                else if (!allowed && doc != restricted)
                {
                    failures.Add($"the PDP denied document {doc} to its reviewer");
                }
            }
        }

        return (checks, unauthorized);
    }

    private static async Task<SearchOutcome> SearchAsync(FaultWorldFixture world, Guid ws, Guid user)
    {
        await using var scope = world.Api.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISearchService>().SearchAsync(
            new SearchCaller(Principal(user), ws, null), new SearchRequest(string.Empty, [new SearchSortKey("controlNumber")], 100, true), Ct);
    }

    private static SecurityPrincipal Principal(Guid user) => new() { UserId = user, DisplayName = "user " + user.ToString("N")[..6] };

    public static string Summary(IEnumerable<TrialOutcome> outcomes)
    {
        var list = outcomes.ToList();
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{list.Count(o => o.Passed)}/{list.Count} trials idempotent");
        foreach (var outcome in list)
        {
            text.Append('\n').Append(outcome);
        }

        return text.ToString();
    }
}
#endif
