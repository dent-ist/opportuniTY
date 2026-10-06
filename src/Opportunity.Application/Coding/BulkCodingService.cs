using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Fields;
using Opportunity.Application.Jobs;
using Opportunity.Application.Snapshots;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.Core.Snapshots;

namespace Opportunity.Application.Coding;

/// <summary>
/// Bulk coding (E10-T04, baseline §21 bulk path, §32 BULK TAG): submits a job over a frozen <c>BulkCoding</c> snapshot
/// (ADR-002: the target is the materialized membership, never a live query) and reads its per-document report. The job
/// is planned in the request: one chunk per 1,000 members (500 when a security-affecting field is written, ADR-010 §6),
/// each a <see cref="ChunkMembershipKind.SnapshotRange"/>, executed by <see cref="BulkCodingChunkExecutor"/>.
/// Authorization: <c>Coding.Bulk</c>, plus <c>Coding.WritePrivilege</c> for security-affecting fields (§24, Q-11); the
/// snapshot must be the caller's own or the caller needs <c>Job.ViewAll</c>.
/// </summary>
public sealed class BulkCodingService(
    IJobRepository jobs,
    IDocumentSetSnapshotStore snapshots,
    IFieldCatalogRepository fields,
    IFieldAccessFilter fieldAccess,
    IAuthorizationService authorization,
    ICodingRepository coding,
    TimeProvider time)
{
    public const int MaxIdempotencyKeyLength = NewJob.MaxClientIdempotencyKeyLength;
    public const int MaxReportPageSize = 1_000;

    public async Task<BulkCodingSubmitOutcome> SubmitAsync(CodingCaller caller, BulkCodingSubmission request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        if (ValidateShape(request) is { } shape)
        {
            return BulkCodingSubmitOutcome.Of(BulkCodingSubmitStatus.Invalid, shape);
        }

        var principal = caller.Principal;
        var ws = caller.WorkspaceId;
        var bulk = await authorization.AuthorizeAsync(principal, ws, Permission.CodingBulk, cancellationToken).ConfigureAwait(false);
        if (!bulk.IsAllowed)
        {
            return BulkCodingSubmitOutcome.Of(Denied(bulk));
        }

        var snapshot = await snapshots.GetAsync(ws, request.SnapshotId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null || !await CanSeeAsync(principal, ws, snapshot.CreatedBy, cancellationToken).ConfigureAwait(false))
        {
            return BulkCodingSubmitOutcome.Of(BulkCodingSubmitStatus.NotFound);
        }

        if (!SnapshotStrategyRules.AcceptsSnapshot(SetOperationKind.BulkCoding, snapshot.Purpose))
        {
            return BulkCodingSubmitOutcome.Of(BulkCodingSubmitStatus.Invalid,
                new FieldError("snapshotId", "snapshot-purpose", "Bulk coding needs a snapshot frozen for bulk coding."));
        }

        if (snapshot.Status != SnapshotStatus.Ready || snapshot.DocumentCount is not { } documentCount)
        {
            return BulkCodingSubmitOutcome.Of(BulkCodingSubmitStatus.SnapshotNotReady);
        }

        // Values are validated against the catalogue here for complete errors; every chunk re-validates under lock.
        var catalog = await fields.GetCatalogAsync(ws, cancellationToken: cancellationToken).ConfigureAwait(false);
        var restricted = await fieldAccess.RestrictedFieldIdsAsync(ws, principal, catalog, cancellationToken).ConfigureAwait(false);
        var errors = new List<FieldError>();
        var operations = new List<CodingFieldOperation>(request.Operations.Count);
        var security = false;
        foreach (var change in request.Operations)
        {
            var field = catalog.Find(change.FieldId);
            if (field is null || field.IsDeleted || restricted.Contains(change.FieldId))
            {
                errors.Add(new FieldError(FieldKey.For(change.FieldId), "unknown-field", "The field does not exist in this workspace."));
            }
            else if (field.Storage != FieldStorage.Coding)
            {
                errors.Add(new FieldError(field.Key, "not-coding-field", $"{field.Name} is imported data and cannot be coded."));
            }
            else if (InteractiveCodingService.Canonicalize(field, change, catalog.ChoicesOf(field.FieldId), out var canonical) is { } error)
            {
                errors.Add(error);
            }
            else
            {
                operations.Add(new CodingFieldOperation(field.FieldId, change.Kind, canonical));
                security |= field.IsSecurityAffecting;
            }
        }

        if (errors.Count > 0)
        {
            return new BulkCodingSubmitOutcome { Status = BulkCodingSubmitStatus.Invalid, Errors = errors };
        }

        if (security)
        {
            var privilege = await authorization.AuthorizeAsync(principal, ws, Permission.CodingWritePrivilege, cancellationToken).ConfigureAwait(false);
            if (!privilege.IsAllowed)
            {
                return BulkCodingSubmitOutcome.Of(Denied(privilege));
            }
        }

        var correlation = principal.CorrelationId ?? Activity.Current?.TraceId.ToHexString();
        var creation = await jobs.CreateAsync(new NewJob
        {
            WorkspaceId = ws,
            JobType = JobType.BulkCoding,
            InitiatedBy = principal.UserId,
            TargetSnapshotId = snapshot.SnapshotId,
            Parameters = BulkCodingParameters.ToJson(operations, security),
            ClientIdempotencyKey = request.IdempotencyKey,
            CorrelationId = correlation,
            SubmissionAudit = SubmittedAudit(principal, snapshot, operations, security, documentCount, correlation),
        }, cancellationToken).ConfigureAwait(false);

        var job = creation.Job;
        if (!creation.Created && (job.JobType != JobType.BulkCoding || job.TargetSnapshotId != snapshot.SnapshotId
            || !JsonNode.DeepEquals(job.Parameters, BulkCodingParameters.ToJson(operations, security))))
        {
            return BulkCodingSubmitOutcome.Of(BulkCodingSubmitStatus.IdempotencyKeyReuse);
        }

        // Planning is idempotent: a retry after a crash between creation and start finishes it.
        job = await StartAsync(job, documentCount, security, cancellationToken).ConfigureAwait(false);
        return new BulkCodingSubmitOutcome { Status = BulkCodingSubmitStatus.Accepted, Job = job, Created = creation.Created };
    }

    /// <summary>
    /// Submits the bulk coding job of a propagation (E09-T05) over <paramref name="snapshot"/>, a Ready BulkCoding
    /// snapshot of the propagation's targets that the propagation service froze for the caller with <c>Coding.Write</c>.
    /// The service has authorized the caller and validated <paramref name="operations"/>; the job's chunks re-check both
    /// (<see cref="BulkCodingChunkExecutor"/>). A retry with the same <paramref name="idempotencyKey"/> returns the job.
    /// </summary>
    public async Task<BulkCodingSubmitOutcome> SubmitPropagationAsync(
        CodingCaller caller, SnapshotRecord snapshot, IReadOnlyList<CodingFieldOperation> operations, bool securityAffecting,
        PropagationJobParameters propagation, string idempotencyKey, AuditEvent submissionAudit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(propagation);
        if (snapshot.Status != SnapshotStatus.Ready || snapshot.DocumentCount is not { } documentCount
            || !SnapshotStrategyRules.AcceptsSnapshot(SetOperationKind.BulkCoding, snapshot.Purpose))
        {
            return BulkCodingSubmitOutcome.Of(BulkCodingSubmitStatus.SnapshotNotReady);
        }

        var parameters = BulkCodingParameters.ToJson(operations, securityAffecting, propagation);
        var creation = await jobs.CreateAsync(new NewJob
        {
            WorkspaceId = caller.WorkspaceId,
            JobType = JobType.BulkCoding,
            InitiatedBy = caller.Principal.UserId,
            TargetSnapshotId = snapshot.SnapshotId,
            Parameters = parameters,
            ClientIdempotencyKey = idempotencyKey,
            CorrelationId = submissionAudit.CorrelationId,
            SubmissionAudit = submissionAudit with { SnapshotId = snapshot.SnapshotId },
        }, cancellationToken).ConfigureAwait(false);

        var job = creation.Job;
        if (!creation.Created && (job.JobType != JobType.BulkCoding || job.TargetSnapshotId != snapshot.SnapshotId
            || !JsonNode.DeepEquals(job.Parameters, parameters)))
        {
            return BulkCodingSubmitOutcome.Of(BulkCodingSubmitStatus.IdempotencyKeyReuse);
        }

        job = await StartAsync(job, documentCount, securityAffecting, cancellationToken).ConfigureAwait(false);
        return new BulkCodingSubmitOutcome { Status = BulkCodingSubmitStatus.Accepted, Job = job, Created = creation.Created };
    }

    /// <summary>
    /// The bulk coding job <paramref name="jobId"/> when <paramref name="caller"/> may see it (their own, or any with
    /// <c>Job.ViewAll</c>); null when it does not exist, is another job type or is not visible.
    /// </summary>
    public async Task<JobInfo?> GetJobAsync(CodingCaller caller, Guid jobId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var job = await jobs.GetAsync(caller.WorkspaceId, jobId, cancellationToken).ConfigureAwait(false);
        return job is { JobType: JobType.BulkCoding }
            && await CanSeeAsync(caller.Principal, caller.WorkspaceId, job.InitiatedBy, cancellationToken).ConfigureAwait(false)
            ? job
            : null;
    }

    /// <summary>
    /// One page of a job's per-document report for one outcome, in a stable order: applied documents by DocumentId
    /// (from the job's CodingEvents), the others in chunk order (from the job's item results).
    /// </summary>
    /// <param name="position">The <see cref="BulkCodingReportPage.Next"/> of the previous page, or null.</param>
    /// <returns>Null when <paramref name="position"/> is malformed.</returns>
    public async Task<BulkCodingReportPage?> GetReportAsync(
        JobInfo job, BulkCodingOutcome outcome, string? position, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxReportPageSize);
        if (outcome == BulkCodingOutcome.Applied)
        {
            Guid? after = null;
            if (position is not null)
            {
                if (!Guid.TryParseExact(position, "N", out var last))
                {
                    return null;
                }

                after = last;
            }

            var ids = await coding.GetJobChangedDocumentsAsync(job.WorkspaceId, job.JobId, after, limit + 1, cancellationToken).ConfigureAwait(false);
            var page = ids.Take(limit).Select(id => new BulkCodingReportItem(id, BulkCodingOutcome.Applied, "Changed", [])).ToList();
            return new BulkCodingReportPage(page, ids.Count > limit ? page[^1].DocumentId.ToString("N") : null);
        }

        (int, int)? afterItem = null;
        if (position is not null)
        {
            var parts = position.Split(':');
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var item))
            {
                return null;
            }

            afterItem = (sequence, item);
        }

        var kind = outcome switch
        {
            BulkCodingOutcome.SkippedChanged => JobItemResultKind.SkippedConcurrentEdit,
            BulkCodingOutcome.SkippedHidden => JobItemResultKind.ExcludedNoAccess,
            _ => JobItemResultKind.Failed,
        };
        var results = await jobs.GetItemResultsAsync(new JobItemResultQuery(job.WorkspaceId, job.JobId)
        {
            Kind = kind,
            After = afterItem,
            Limit = limit + 1,
        }, cancellationToken).ConfigureAwait(false);
        var items = results.Take(limit)
            .Where(r => r.Result.DocumentId is not null)
            .Select(r => new BulkCodingReportItem(r.Result.DocumentId!.Value, outcome, r.Result.ReasonCode, FieldIds(r.Result)))
            .ToList();
        var next = results.Count > limit
            ? string.Create(CultureInfo.InvariantCulture, $"{results[limit - 1].ChunkSequence}:{results[limit - 1].ItemNo}")
            : null;
        return new BulkCodingReportPage(items, next);
    }

    private async Task<JobInfo> StartAsync(JobInfo job, long documentCount, bool security, CancellationToken cancellationToken)
    {
        if (job.Status == JobStatus.Created)
        {
            await jobs.BeginPreparingAsync(job.WorkspaceId, job.JobId, cancellationToken).ConfigureAwait(false);
        }

        if (job.Status is JobStatus.Created or JobStatus.Preparing)
        {
            var bounds = ChunkBounds.For(JobType.BulkCoding, security);
            var plans = ChunkPlanner.SplitByCount(1, documentCount, bounds.MaxItems)
                .Select(r => new ChunkPlan(ChunkMembership.SnapshotRange(job.TargetSnapshotId!.Value, r.From, r.To), checked((int)r.Count)))
                .ToList();
            await jobs.StartAsync(new JobStartRequest(job.WorkspaceId, job.JobId, ChunkOperationKind.BulkCodingChunk, plans), cancellationToken)
                .ConfigureAwait(false);
        }

        return await jobs.GetAsync(job.WorkspaceId, job.JobId, cancellationToken).ConfigureAwait(false) ?? job;
    }

    private async Task<bool> CanSeeAsync(SecurityPrincipal principal, Guid workspaceId, Guid owner, CancellationToken cancellationToken) =>
        owner == principal.UserId
        || (await authorization.AuthorizeAsync(principal, workspaceId, Permission.JobViewAll, cancellationToken).ConfigureAwait(false)).IsAllowed;

    private AuditEvent SubmittedAudit(
        SecurityPrincipal principal, SnapshotRecord snapshot, List<CodingFieldOperation> operations, bool security, long documentCount, string? correlation) => new()
        {
            OccurredAt = time.GetUtcNow(),
            Category = AuditTaxonomy.Coding.Category,
            Action = AuditTaxonomy.Coding.BulkSubmitted,
            ActorType = AuditActorType.User,
            ActorId = principal.UserId.ToString(),
            ActorDisplay = string.IsNullOrEmpty(principal.DisplayName) ? principal.UserId.ToString() : principal.DisplayName,
            ClientIp = principal.ClientIp,
            UserAgent = principal.UserAgent,
            Outcome = AuditOutcome.Success,
            CorrelationId = correlation,
            SnapshotId = snapshot.SnapshotId,
            Details = new Dictionary<string, string?>
            {
                ["Operations"] = string.Join(',', operations.OrderBy(o => o.FieldId).Select(o =>
                    o.FieldId.ToString(CultureInfo.InvariantCulture) + ":" + o.Kind)),
                ["SnapshotDocuments"] = documentCount.ToString(CultureInfo.InvariantCulture),
                ["SecurityAffecting"] = security ? "true" : "false",
            },
        };

    private static List<int> FieldIds(JobItemResult result)
    {
        // Q-07 skips carry every skipped field in the detail ("Fields 3,7"); the first one is also the item's field.
        if (result.Detail is { } detail && detail.StartsWith("Fields ", StringComparison.Ordinal))
        {
            var ids = new List<int>();
            foreach (var part in detail["Fields ".Length..].Split(','))
            {
                if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                {
                    ids.Add(id);
                }
            }

            return ids;
        }

        return result.FieldId is { } fieldId ? [fieldId] : [];
    }

    private static BulkCodingSubmitStatus Denied(AuthorizationDecision decision) =>
        decision.Outcome == AuthorizationOutcome.NotFound ? BulkCodingSubmitStatus.NotFound : BulkCodingSubmitStatus.Forbidden;

    private static FieldError? ValidateShape(BulkCodingSubmission request)
    {
        if (request.SnapshotId == Guid.Empty)
        {
            return new FieldError("snapshotId", "snapshot-required", "A snapshot (frozen set) is required.");
        }

        if (request.Operations is null || request.Operations.Count is 0 or > CodingWriteRequest.MaxOperations)
        {
            return new FieldError("operations", "invalid-operations", $"Between 1 and {CodingWriteRequest.MaxOperations} operations are required.");
        }

        if (request.Operations.Select(c => c.FieldId).Distinct().Count() != request.Operations.Count)
        {
            return new FieldError("operations", "duplicate-field", "Each field may be changed at most once per job.");
        }

        if (request.IdempotencyKey is { } key && (key.Length is 0 or > MaxIdempotencyKeyLength))
        {
            return new FieldError("idempotencyKey", "invalid-idempotency-key", $"An idempotency key has 1–{MaxIdempotencyKeyLength} characters.");
        }

        return null;
    }
}
