using System.Globalization;
using System.Text.Json.Nodes;

using Opportunity.Application.Authorization;
using Opportunity.Application.Fields;
using Opportunity.Application.Jobs;
using Opportunity.Core.Documents;
using Opportunity.Core.Fields;
using Opportunity.Core.Jobs;

namespace Opportunity.Application.Documents.Dedupe;

public enum DedupeOutcomeStatus
{
    Ok,
    Invalid,
    VersionConflict,
    IdempotencyKeyReuse,
}

/// <summary>The outcome of a policy save or a run submission.</summary>
public sealed record DedupeOutcome
{
    public required DedupeOutcomeStatus Status { get; init; }

    public DedupePolicyRecord? Record { get; init; }

    public JobInfo? Job { get; init; }

    /// <summary>Validation messages by request property.</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; init; } = new Dictionary<string, string[]>();

    public static DedupeOutcome Invalid(string property, string message) =>
        new() { Status = DedupeOutcomeStatus.Invalid, Errors = new Dictionary<string, string[]> { [property] = [message] } };
}

/// <summary>
/// Computed duplicate grouping (E09-T04, Q-09, ADR-009 R14): reads and saves a workspace's dedupe policy and submits a
/// run. A run is a <see cref="JobType.RelationshipFixup"/> job with one <see cref="ChunkOperationKind.RelationshipChunk"/>
/// over the whole workspace (the grouping is a function of every family, so it is applied in one transaction); the
/// import worker executes it (<see cref="DedupeChunkExecutor"/>). The policy the run applies, with the custodian field it
/// resolved, is frozen into the job's parameters at submission. Re-running is idempotent: group ids are UUIDv5 of the
/// key, so an unchanged workspace produces no change and no index work.
/// </summary>
public sealed class DedupeService(IDedupeStore store, IJobRepository jobs, IFieldCatalogRepository fields)
{
    public Task<DedupePolicyRecord> GetAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
        store.GetAsync(workspaceId, cancellationToken);

    public async Task<DedupeOutcome> SaveAsync(
        Guid workspaceId, long expectedVersion, DedupePolicy policy, SecurityPrincipal actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!Enum.IsDefined(policy.HashSource))
        {
            return DedupeOutcome.Invalid("hashSource", "hashSource must be auto, sha256, md5, sha1 or upstreamHash.");
        }

        if (!Enum.IsDefined(policy.Scope))
        {
            return DedupeOutcome.Invalid("scope", "scope must be global or custodial.");
        }

        if (policy.CustodianFieldId is not null || policy.Scope == DuplicateGroupScope.Custodial)
        {
            var catalog = await fields.GetCatalogAsync(workspaceId, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (CustodianField(catalog, policy.CustodianFieldId) is { Error: { } error })
            {
                return DedupeOutcome.Invalid("custodianFieldId", error);
            }
        }

        var (outcome, record) = await store.SaveAsync(workspaceId, expectedVersion, policy, actor, cancellationToken).ConfigureAwait(false);
        return new DedupeOutcome
        {
            Status = outcome == DedupePolicyWriteOutcome.Saved ? DedupeOutcomeStatus.Ok : DedupeOutcomeStatus.VersionConflict,
            Record = record,
        };
    }

    /// <summary>
    /// Submits a run of the saved policy (the default, which removes computed groups, when none was saved). A retry with
    /// the same <paramref name="idempotencyKey"/> returns the same job.
    /// </summary>
    public async Task<DedupeOutcome> StartRunAsync(
        Guid workspaceId, SecurityPrincipal actor, string? idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var policy = (await store.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false)).Policy;
        if (policy is { Enabled: true, Scope: DuplicateGroupScope.Custodial })
        {
            var catalog = await fields.GetCatalogAsync(workspaceId, cancellationToken: cancellationToken).ConfigureAwait(false);
            var (field, error) = CustodianField(catalog, policy.CustodianFieldId);
            if (error is not null)
            {
                return DedupeOutcome.Invalid("custodianFieldId", error + " Save the policy again before running it.");
            }

            policy = policy with { CustodianFieldId = field!.FieldId };
        }

        var creation = await jobs.CreateAsync(new NewJob
        {
            WorkspaceId = workspaceId,
            JobType = JobType.RelationshipFixup,
            InitiatedBy = actor.UserId,
            Parameters = policy.ToJson(),
            ClientIdempotencyKey = idempotencyKey,
            CorrelationId = actor.CorrelationId,
        }, cancellationToken).ConfigureAwait(false);
        var job = creation.Job;
        if (!creation.Created && (job.JobType != JobType.RelationshipFixup || !JsonNode.DeepEquals(job.Parameters, policy.ToJson())))
        {
            return new DedupeOutcome { Status = DedupeOutcomeStatus.IdempotencyKeyReuse };
        }

        if (job.Status == JobStatus.Created)
        {
            await jobs.BeginPreparingAsync(workspaceId, job.JobId, cancellationToken).ConfigureAwait(false);
        }

        job = await jobs.GetAsync(workspaceId, job.JobId, cancellationToken).ConfigureAwait(false) ?? job;
        if (job.Status is JobStatus.Created or JobStatus.Preparing)
        {
            var documents = await store.CountDocumentsAsync(workspaceId, cancellationToken).ConfigureAwait(false);
            await jobs.StartAsync(new JobStartRequest(workspaceId, job.JobId, ChunkOperationKind.RelationshipChunk,
                [new ChunkPlan(WholeWorkspace, (int)Math.Min(documents, int.MaxValue))]), cancellationToken).ConfigureAwait(false);
            job = await jobs.GetAsync(workspaceId, job.JobId, cancellationToken).ConfigureAwait(false) ?? job;
        }

        return new DedupeOutcome { Status = DedupeOutcomeStatus.Ok, Job = job };
    }

    /// <summary>
    /// The one chunk of a run: every document of the workspace, as the DocumentId key range from the nil UUID to the
    /// all-ones UUID (the generation is not used by the run).
    /// </summary>
    public static ChunkMembership WholeWorkspace { get; } = ChunkMembership.DocumentKeyRange(1, Guid.Empty, Guid.AllBitsSet);

    /// <summary>
    /// The custodian field of a Custodial policy: the named field, or the workspace's <c>custodian</c> field. It must be a
    /// live single-value Keyword or Text metadata field.
    /// </summary>
    public static (FieldDefinition? Field, string? Error) CustodianField(FieldCatalog catalog, int? fieldId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var field = fieldId is { } id
            ? catalog.Find(id)
            : catalog.Fields.Where(SearchFieldExpansion.IsCustodian).OrderBy(f => f.FieldId).FirstOrDefault();
        if (field is null || field.IsDeleted)
        {
            return (null, fieldId is null
                ? "The workspace has no Custodian field; name the field that holds the custodian."
                : string.Create(CultureInfo.InvariantCulture, $"Field {fieldId} does not exist."));
        }

        return field is { Storage: FieldStorage.Metadata, IsMultiValue: false, Type: FieldType.Keyword or FieldType.Text }
            ? (field, null)
            : (null, $"'{field.Name}' cannot hold the custodian: use a single-value Keyword or Text metadata field.");
    }
}
