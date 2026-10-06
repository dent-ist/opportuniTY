using System.Globalization;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Fields;
using Opportunity.Application.Identity;
using Opportunity.Application.Jobs;
using Opportunity.Application.Snapshots;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;

namespace Opportunity.Application.Coding;

/// <summary>
/// The <see cref="ChunkOperationKind.BulkCodingChunk"/> executor (E10-T04), run by the idempotent chunk consumer
/// (E06-T05) with the chunk, job and initiator read from PostgreSQL. Per chunk:
/// <list type="number">
/// <item>ADR-015 D9.4: the initiator's principal is rebuilt from PostgreSQL (user and current IdP group snapshot) and the
/// job's permissions are re-checked: <c>Coding.Bulk</c>, plus <c>Coding.WritePrivilege</c> when a security-affecting
/// field is written. Without them the job fails and its remaining chunks are cancelled; committed chunks stay (Q-34).
/// A field that was deleted, retyped or restricted since the submission fails the job the same way.</item>
/// <item>The chunk's members (ordinals From…To of the frozen snapshot, with their Q-07 baseline versions) are
/// re-authorized in one PDP call; documents the initiator can no longer access are excluded with the generic reason
/// <c>AccessChanged</c> (the precise reason is in the per-document <c>AuthZ.Denied</c> audit only).</item>
/// <item>One transaction (<see cref="ICodingRepository.ApplyChunkAsync(ClaimedChunk, CodingWriteRequest, IReadOnlyList{JobItemResult}, CancellationToken)"/>):
/// state-based coding with the Q-07 skip rule per field (a value someone else changed after the baseline is left alone
/// and recorded as a <c>BulkSkippedConcurrentEdit</c> CodingEvent), restriction classes recomputed per changed
/// document, CodingEvents (actor <c>BulkHuman</c>), version bumps, one IndexChunkTask, the item results, the
/// <c>Coding.BulkChunkApplied</c> audit event and fence F3. The chunk's idempotency key is the coding write's key, so a
/// replay can never write events twice.</item>
/// </list>
/// </summary>
public sealed class BulkCodingChunkExecutor(
    ICodingRepository coding,
    IDocumentSetSnapshotStore snapshots,
    IAuthorizationService authorization,
    IUserDirectory users,
    IFieldCatalogRepository fields,
    IFieldAccessFilter fieldAccess,
    IJobRepository jobs,
    TimeProvider time) : IJobChunkExecutor
{
    /// <summary>Reason code of members excluded because the initiator lost access (ADR-015 D9.4).</summary>
    public const string AccessChanged = "AccessChanged";

    /// <summary>Service identity in the chunk audit events, acting on behalf of the initiator (ADR-013 §4).</summary>
    public const string ServiceActor = "service:bulk-coding";

    public ChunkOperationKind OperationKind => ChunkOperationKind.BulkCodingChunk;

    public async Task<ChunkExecutionResult> ExecuteAsync(ChunkExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var chunk = context.Chunk;
        var membership = chunk.Membership;
        if (chunk.JobType != JobType.BulkCoding || membership.Kind != ChunkMembershipKind.SnapshotRange
            || membership.SnapshotId is not { } snapshotId || snapshotId != chunk.TargetSnapshotId)
        {
            throw new PermanentChunkException("NotABulkCodingChunk", "A bulk coding chunk is an ordinal range of the job's snapshot.");
        }

        IReadOnlyList<CodingFieldOperation> operations;
        PropagationJobParameters? propagation;
        try
        {
            operations = BulkCodingParameters.Parse(chunk.Parameters);
            propagation = BulkCodingParameters.Propagation(chunk.Parameters);
        }
        catch (FormatException ex)
        {
            throw new PermanentChunkException("InvalidParameters", ex.Message, ex);
        }

        var ws = context.WorkspaceId;

        // D9.4: who the job acts for, rebuilt from PostgreSQL now (never from the message or the submission).
        if (await users.GetAsync(chunk.InitiatedBy, cancellationToken).ConfigureAwait(false) is not { } user)
        {
            return await StopJobAsync(context, "The user who started the job no longer exists; the remaining chunks were cancelled.", cancellationToken)
                .ConfigureAwait(false);
        }

        var principal = new SecurityPrincipal
        {
            UserId = user.UserId,
            DisplayName = user.DisplayName ?? user.UserId.ToString(),
            Groups = user.Groups,
            CorrelationId = chunk.CorrelationId,
        };
        // A propagation (E09-T05) acts with the reviewer's Coding.Write; a Mass Edit needs Coding.Bulk.
        var permission = propagation is null ? Permission.CodingBulk : Permission.CodingWrite;
        if (!(await authorization.AuthorizeAsync(principal, ws, permission, cancellationToken).ConfigureAwait(false)).IsAllowed)
        {
            return await StopJobAsync(context,
                $"The user who started the job no longer has {permission.Name()}; the remaining chunks were cancelled.", cancellationToken)
                .ConfigureAwait(false);
        }

        var catalog = await fields.GetCatalogAsync(ws, cancellationToken: cancellationToken).ConfigureAwait(false);
        var restricted = await fieldAccess.RestrictedFieldIdsAsync(ws, principal, catalog, cancellationToken).ConfigureAwait(false);
        var security = false;
        foreach (var operation in operations)
        {
            if (catalog.Find(operation.FieldId) is not { IsDeleted: false, Storage: FieldStorage.Coding } field || restricted.Contains(operation.FieldId))
            {
                return await StopJobAsync(context,
                    $"Field {operation.FieldId.ToString(CultureInfo.InvariantCulture)} can no longer be coded by this job; the remaining chunks were cancelled.",
                    cancellationToken).ConfigureAwait(false);
            }

            security |= field.IsSecurityAffecting;
        }

        if (security
            && !(await authorization.AuthorizeAsync(principal, ws, Permission.CodingWritePrivilege, cancellationToken).ConfigureAwait(false)).IsAllowed)
        {
            return await StopJobAsync(context,
                "The user who started the job no longer has Coding.WritePrivilege; the remaining chunks were cancelled.", cancellationToken)
                .ConfigureAwait(false);
        }

        var from = membership.RangeFrom!.Value;
        var to = membership.RangeTo!.Value;
        var members = await snapshots.ReadMembersAsync(ws, snapshotId, from, to, cancellationToken).ConfigureAwait(false);
        if (members.Count != to - from + 1)
        {
            throw new PermanentChunkException("SnapshotUnavailable", "The chunk's snapshot members are no longer available.");
        }

        // Q-15 / D9.4: every member is re-authorized for the initiator against current PostgreSQL security state.
        var decisions = await authorization.AuthorizeManyAsync(
            principal, ws, permission, [.. members.Select(m => m.DocumentId)], DenialAudit.PerDocument, cancellationToken)
            .ConfigureAwait(false);
        var excluded = new List<JobItemResult>();
        var targets = new List<CodingTarget>(members.Count);
        foreach (var member in members)
        {
            var decision = decisions.GetValueOrDefault(member.DocumentId);
            if (decision.IsAllowed || decision.Reason == AuthorizationReasons.DocumentNotFound)
            {
                // A deleted document stays a target: the coding store skips and reports it (ADR-010 §8.3).
                targets.Add(new CodingTarget(member.DocumentId, member.BaselineVersion));
            }
            else
            {
                excluded.Add(new JobItemResult(JobItemResultKind.ExcludedNoAccess, member.DocumentId, null, null, AccessChanged));
            }
        }

        // Fence F2 before the PostgreSQL batch; F3 runs inside the coding transaction.
        await context.CheckFenceAsync(cancellationToken).ConfigureAwait(false);
        var result = await coding.ApplyChunkAsync(chunk, new CodingWriteRequest
        {
            WorkspaceId = ws,
            IdempotencyKey = chunk.IdempotencyKey,
            Actor = new CodingActor(chunk.InitiatedBy, CodingActorType.BulkHuman),
            JobId = chunk.Lease.JobId,
            Documents = targets,
            Operations = operations,
            OriginEventIds = propagation?.OriginEventIds,
            Audit = ChunkAudit(chunk, snapshotId, excluded.Count),
        }, excluded, cancellationToken).ConfigureAwait(false);

        switch (result.Coding.Outcome)
        {
            case CodingWriteOutcome.Applied:
                return ChunkExecutionResult.Committed(result.Commit!);
            case CodingWriteOutcome.Invalid:
                // E.g. a choice was deleted or a field retyped since the submission: no chunk can succeed any more.
                var reason = string.Join(" ", result.Coding.Errors.Select(e => e.Message));
                return await StopJobAsync(context,
                    "The coding operations are no longer valid: " + (reason.Length <= 500 ? reason : reason[..500]), cancellationToken)
                    .ConfigureAwait(false);
            default:
                throw new PermanentChunkException("Coding" + result.Coding.Outcome,
                    $"The chunk's coding write was refused ({result.Coding.Outcome}).");
        }
    }

    /// <summary>
    /// Job-level stop (ADR-010 §2 Running → Failed, D9.4): fails the job, which cancels its open chunks, and ends this
    /// attempt at a fence so the consumer cancels this chunk too. Nothing of the chunk was written.
    /// </summary>
    private async Task<ChunkExecutionResult> StopJobAsync(ChunkExecutionContext context, string reason, CancellationToken cancellationToken)
    {
        await jobs.FailAsync(context.WorkspaceId, context.Chunk.Lease.JobId, reason, cancellationToken).ConfigureAwait(false);
        throw new ChunkFencedException(ChunkFence.JobCancelling);
    }

    private AuditEvent ChunkAudit(ClaimedChunk chunk, Guid snapshotId, int excluded) => new()
    {
        OccurredAt = time.GetUtcNow(),
        Category = AuditTaxonomy.Coding.Category,
        Action = AuditTaxonomy.Coding.BulkChunkApplied,
        ActorType = AuditActorType.Service,
        ActorId = ServiceActor,
        ActorDisplay = "Bulk coding worker",
        OnBehalfOf = chunk.InitiatedBy,
        Outcome = AuditOutcome.Success,
        CorrelationId = chunk.CorrelationId,
        JobId = chunk.Lease.JobId,
        ChunkSequence = chunk.Sequence,
        SnapshotId = snapshotId,
        Details = new Dictionary<string, string?>
        {
            ["OrdinalFrom"] = chunk.Membership.RangeFrom?.ToString(CultureInfo.InvariantCulture),
            ["OrdinalTo"] = chunk.Membership.RangeTo?.ToString(CultureInfo.InvariantCulture),
            ["ExcludedNoAccess"] = excluded.ToString(CultureInfo.InvariantCulture),
        },
    };
}
