using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Opportunity.Application.Audit;
using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Application.Telemetry;
using Opportunity.Contracts.Messaging;
using Opportunity.Contracts.Messaging.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.Jobs;
#if OPPORTUNITY_FAILPOINTS
using Opportunity.Jobs.Faults;
#endif

namespace Opportunity.UnitTests.Jobs;

/// <summary>E06-T05: the idempotent job chunk consumer against a scripted <see cref="IJobChunkRepository"/>.</summary>
public sealed class JobChunkConsumerTests
{
    private static readonly Guid Workspace = Guid.CreateVersion7();
    private static readonly Guid Job = Guid.CreateVersion7();
    private static readonly Guid Chunk = Guid.CreateVersion7();
    private static readonly Guid Initiator = Guid.CreateVersion7();
    private static readonly string Key = ChunkIdempotencyKey.ForChunk(Workspace, Job, 3, ChunkOperationKind.BulkCodingChunk, 0);

    private readonly ScriptedChunks _chunks = new();
    private readonly InMemoryAuditEventWriter _audit = new();
    private readonly RecordingMeter _meter = new();
    private readonly ScriptedExecutor _executor = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_claimed_chunk_runs_with_pg_identity_and_commits_before_returning()
    {
        _executor.Handler = context =>
        {
            context.WorkspaceId.Should().Be(Workspace);
            context.InitiatedBy.Should().Be(Initiator);
            context.Chunk.Parameters["fieldId"]!.GetValue<int>().Should().Be(7);
            return Task.FromResult(ChunkExecutionResult.Complete(new ChunkCompletion { ItemsApplied = 100 }));
        };

        await Consumer().HandleAsync(Payload(), Message(), Ct);

        _chunks.Claims.Should().ContainSingle().Which.Should().Be((Workspace, Chunk));
        _chunks.Completions.Should().ContainSingle().Which.ItemsApplied.Should().Be(100);
        _chunks.Failures.Should().BeEmpty();
        _audit.Events.Should().BeEmpty();
        _meter.Measured.Should().Be(1);
        _meter.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task An_executor_that_commits_in_its_own_transaction_is_not_committed_again()
    {
        _executor.Handler = _ => Task.FromResult(
            ChunkExecutionResult.Committed(new ChunkCommitResult(ChunkCommitOutcome.Committed, JobStatus.Running)));

        await Consumer().HandleAsync(Payload(), Message(), Ct);

        _chunks.Completions.Should().BeEmpty();
        _executor.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData(ChunkClaimOutcome.AlreadySettled)]
    [InlineData(ChunkClaimOutcome.LeaseHeld)]
    [InlineData(ChunkClaimOutcome.NotDue)]
    [InlineData(ChunkClaimOutcome.JobNotRunning)]
    [InlineData(ChunkClaimOutcome.Cancelled)]
    [InlineData(ChunkClaimOutcome.AttemptsExhausted)]
    public async Task A_delivery_that_cannot_claim_is_acked_and_dropped_without_work(ChunkClaimOutcome outcome)
    {
        _chunks.Claim = (_, _) => new ChunkClaimResult(outcome);

        await Consumer().HandleAsync(Payload(), Message(), Ct);

        _executor.Calls.Should().Be(0);
        _chunks.Writes.Should().Be(0);
        _audit.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task A_chunk_invisible_in_the_hinted_workspace_is_rejected_and_audited()
    {
        _chunks.Claim = (_, _) => new ChunkClaimResult(ChunkClaimOutcome.NotFound);
        var foreign = Guid.CreateVersion7();

        var handle = () => Consumer().HandleAsync(Payload(), Message(workspaceId: foreign), Ct);

        (await handle.Should().ThrowAsync<PermanentMessageException>()).Which.Message.Should().Contain("EnvelopeMismatch");
        _executor.Calls.Should().Be(0);
        _chunks.Writes.Should().Be(0);
        var audit = _audit.Events.Should().ContainSingle().Subject;
        audit.Category.Should().Be("Integrity");
        audit.Action.Should().Be("MessageRejected");
        audit.WorkspaceId.Should().BeNull("rejections are installation-level events");
        audit.Outcome.Should().Be(AuditOutcome.Denied);
        audit.ReasonCode.Should().Be("EnvelopeMismatch");
        audit.ActorType.Should().Be(AuditActorType.Service);
        audit.ResourceId.Should().Be(Chunk.ToString());
        audit.Details["claimedWorkspaceId"].Should().Be(foreign.ToString());
        audit.Details["claimedJobId"].Should().Be(Job.ToString());
        audit.Details["mismatch"].Should().Be("workspaceId");
        _meter.Failures.Should().Equal("EnvelopeMismatch");
    }

    public static TheoryData<string> Mismatches => new() { "jobId", "operation", "sequence", "idempotencyKey" };

    [Theory]
    [MemberData(nameof(Mismatches))]
    public async Task An_envelope_disagreeing_with_the_chunk_row_releases_the_claim_and_is_rejected(string field)
    {
        var payload = field switch
        {
            "operation" => Payload() with { Operation = JobChunkOperation.ExportChunk },
            "sequence" => Payload() with { Sequence = 4 },
            _ => Payload(),
        };
        var message = field switch
        {
            "jobId" => Message(jobId: Guid.CreateVersion7()),
            "idempotencyKey" => Message(idempotencyKey: new string('0', 64)),
            _ => Message(),
        };

        var handle = () => Consumer().HandleAsync(payload, message, Ct);

        await handle.Should().ThrowAsync<PermanentMessageException>();
        _executor.Calls.Should().Be(0);
        _chunks.Releases.Should().ContainSingle("the claim is given back without charging the attempt");
        _chunks.Completions.Should().BeEmpty();
        _chunks.Failures.Should().BeEmpty();
        _audit.Events.Should().ContainSingle().Which.Details["mismatch"].Should().Be(field);
    }

    [Fact]
    public async Task An_envelope_without_workspace_is_rejected_before_touching_postgres()
    {
        var handle = () => Consumer().HandleAsync(Payload(), Message(workspaceId: Guid.Empty), Ct);

        await handle.Should().ThrowAsync<PermanentMessageException>();
        _chunks.Claims.Should().BeEmpty();
        _audit.Events.Should().ContainSingle();
    }

    [Fact]
    public async Task A_permanent_error_fails_the_chunk_at_once_with_its_last_error_and_acks()
    {
        _executor.Handler = _ => throw new PermanentChunkException("InvalidMapping", "Field 1005 does not exist.");

        await Consumer().HandleAsync(Payload(), Message(), Ct);

        _chunks.Failures.Should().ContainSingle().Which.Should().Be(
            new ChunkError(ChunkErrorClass.Permanent, "InvalidMapping", "Field 1005 does not exist."));
        _chunks.Completions.Should().BeEmpty();
        _meter.Failures.Should().Equal(nameof(ChunkErrorClass.Permanent));
    }

    [Fact]
    public async Task A_transient_error_is_recorded_in_postgres_not_retried_by_the_transport()
    {
        _executor.Handler = _ => throw new TimeoutException("OpenSearch timed out.");

        await Consumer().HandleAsync(Payload(), Message(), Ct);

        var failure = _chunks.Failures.Should().ContainSingle().Subject;
        failure.Class.Should().Be(ChunkErrorClass.Transient);
        failure.Code.Should().Be(nameof(TimeoutException));
    }

    [Fact]
    public async Task When_postgres_cannot_record_the_failure_the_exception_reaches_the_transport_retry()
    {
        _executor.Handler = _ => throw new TimeoutException();
        _chunks.FailWith = new InvalidOperationException("PostgreSQL is down");

        var handle = () => Consumer().HandleAsync(Payload(), Message(), Ct);

        await handle.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(ChunkFence.JobNotRunning, true)]
    [InlineData(ChunkFence.JobCancelling, true)]
    [InlineData(ChunkFence.LeaseLost, false)]
    public async Task A_fence_stops_the_chunk_and_releases_it_unless_the_lease_is_lost(ChunkFence fence, bool released)
    {
        _chunks.Heartbeat = () => fence;
        _executor.Handler = async context =>
        {
            await context.CheckFenceAsync(Ct);
            return ChunkExecutionResult.Complete(ChunkCompletion.Empty);
        };

        await Consumer().HandleAsync(Payload(), Message(), Ct);

        _chunks.Releases.Should().HaveCount(released ? 1 : 0);
        _chunks.Completions.Should().BeEmpty();
        _chunks.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task The_heartbeat_extends_the_lease_and_cancels_the_executor_at_a_fence()
    {
        var beats = 0;
        _chunks.Heartbeat = () => Interlocked.Increment(ref beats) < 3 ? ChunkFence.Proceed : ChunkFence.JobCancelling;
        _executor.Handler = async _ =>
        {
            await Task.Delay(Timeout.Infinite, _executor.Token);
            return ChunkExecutionResult.Complete(ChunkCompletion.Empty);
        };

        await Consumer(new JobLeaseOptions { HeartbeatInterval = TimeSpan.FromMilliseconds(20) })
            .HandleAsync(Payload(), Message(), Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        beats.Should().Be(3);
        _chunks.Releases.Should().ContainSingle();
        _chunks.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task A_chunk_over_its_runtime_cap_fails_transiently()
    {
        _executor.Handler = async _ =>
        {
            await Task.Delay(Timeout.Infinite, _executor.Token);
            return ChunkExecutionResult.Complete(ChunkCompletion.Empty);
        };

        await Consumer(new JobLeaseOptions { MaxChunkRuntime = TimeSpan.FromMilliseconds(50) }).HandleAsync(Payload(), Message(), Ct);

        _chunks.Failures.Should().ContainSingle().Which.Code.Should().Be("ChunkRuntimeExceeded");
    }

    [Fact]
    public async Task Shutdown_releases_the_chunk_without_charging_and_requeues_the_delivery()
    {
        using var shutdown = new CancellationTokenSource();
        _executor.Handler = async _ =>
        {
            await shutdown.CancelAsync();
            await Task.Delay(Timeout.Infinite, _executor.Token);
            return ChunkExecutionResult.Complete(ChunkCompletion.Empty);
        };

        var handle = () => Consumer().HandleAsync(Payload(), Message(), shutdown.Token);

        await handle.Should().ThrowAsync<OperationCanceledException>();
        _chunks.Releases.Should().ContainSingle();
        _chunks.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task A_chunk_without_an_executor_on_this_worker_fails_permanently()
    {
        var consumer = new JobChunkConsumer(
            _chunks, [], _audit, new JobLeaseOptions(), Options, _meter, TimeProvider.System, NullLogger<JobChunkConsumer>.Instance);

        await consumer.HandleAsync(Payload(), Message(), Ct);

        _chunks.Failures.Should().ContainSingle().Which.Should().Match<ChunkError>(e => e.Class == ChunkErrorClass.Permanent && e.Code == "NoExecutor");
    }

    [Fact]
    public void Two_executors_for_one_operation_kind_are_a_configuration_error()
    {
        var create = () => new JobChunkConsumer(
            _chunks, [_executor, new ScriptedExecutor()], _audit, new JobLeaseOptions(), Options, _meter, TimeProvider.System,
            NullLogger<JobChunkConsumer>.Instance);

        create.Should().Throw<InvalidOperationException>();
    }

    public static TheoryData<Exception, ChunkErrorClass> Classified => new()
    {
        { new PermanentChunkException("Code", "message"), ChunkErrorClass.Permanent },
        { new TransientChunkException("Code", "message"), ChunkErrorClass.Transient },
        { new ArgumentException("bad"), ChunkErrorClass.Permanent },
        { new FormatException(), ChunkErrorClass.Permanent },
        { new JsonException(), ChunkErrorClass.Permanent },
        { new TimeoutException(), ChunkErrorClass.Transient },
        { new IOException(), ChunkErrorClass.Transient },
        { new HttpRequestException(), ChunkErrorClass.Transient },
        { new FakeDbException(transient: true), ChunkErrorClass.Transient },
        { new FakeDbException(transient: false), ChunkErrorClass.Permanent },
        { new InvalidOperationException(), ChunkErrorClass.Transient },
    };

    [Theory]
    [MemberData(nameof(Classified))]
    public void Errors_are_classified_transient_or_permanent(Exception exception, ChunkErrorClass expected)
    {
        ChunkErrorClassifier.Classify(exception).Class.Should().Be(expected);
    }

#if OPPORTUNITY_FAILPOINTS
    [Theory]
    [InlineData(Failpoints.BeforeClaim, 0, false)]
    [InlineData(Failpoints.AfterClaim, 1, false)]
    [InlineData(Failpoints.BeforeCommit, 1, false)]
    [InlineData(Failpoints.AfterCommit, 1, true)]
    public async Task A_simulated_crash_at_a_failpoint_escapes_and_records_nothing_more(string failpoint, int claims, bool committed)
    {
        var faults = new CrashAt(failpoint);
        var consumer = new JobChunkConsumer(
            _chunks, [_executor], _audit, new JobLeaseOptions(), Options, _meter, TimeProvider.System,
            NullLogger<JobChunkConsumer>.Instance, metrics: null, faults);

        var handle = () => consumer.HandleAsync(Payload(), Message(), Ct);

        await handle.Should().ThrowAsync<SimulatedCrashException>();
        _chunks.Claims.Should().HaveCount(claims);
        _chunks.Completions.Should().HaveCount(committed ? 1 : 0);
        _chunks.Failures.Should().BeEmpty();
        _chunks.Releases.Should().BeEmpty("a crashed process releases nothing; the lease expires");
        faults.Hits.Should().Equal(Failpoints.All.TakeWhile(f => f != failpoint).Append(failpoint));
    }

    private sealed class CrashAt(string failpoint) : IFaultInjector
    {
        public List<string> Hits { get; } = [];

        public ValueTask HitAsync(string name, FailpointContext context, CancellationToken cancellationToken)
        {
            Hits.Add(name);
            return name == failpoint ? throw new SimulatedCrashException() : ValueTask.CompletedTask;
        }
    }
#endif

    private static readonly JobChunkConsumerOptions Options = new() { WorkerId = "unit-worker" };

    private JobChunkConsumer Consumer(JobLeaseOptions? lease = null) => new(
        _chunks, [_executor], _audit, lease ?? new JobLeaseOptions(), Options, _meter, TimeProvider.System,
        NullLogger<JobChunkConsumer>.Instance);

    private static JobChunkMessage Payload() =>
        new() { ChunkId = Chunk, Sequence = 3, Operation = JobChunkOperation.BulkCodingChunk };

    private static ReceivedMessage Message(Guid? workspaceId = null, Guid? jobId = null, string? idempotencyKey = null)
    {
        var envelope = new MessageEnvelope
        {
            MessageId = Guid.CreateVersion7(),
            MessageType = MessageTypes.JobChunk,
            SchemaVersion = new SchemaVersion(1, 0),
            WorkspaceId = workspaceId ?? Workspace,
            JobId = jobId ?? Job,
            CorrelationId = "corr-unit",
            IdempotencyKey = idempotencyKey ?? Key,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = JsonDocument.Parse("{}").RootElement,
        };
        return new ReceivedMessage(envelope, Payload(), WorkQueues.BulkCoding, Redelivered: false, DeliveryCount: 0, TransportRetry: 0);
    }

    private static ClaimedChunk Claimed(Guid workspaceId) => new()
    {
        Lease = new ChunkLease(workspaceId, Job, Chunk, 1, "unit-worker"),
        JobType = JobType.BulkCoding,
        OperationKind = ChunkOperationKind.BulkCodingChunk,
        InitiatedBy = Initiator,
        Parameters = new JsonObject { ["fieldId"] = 7 },
        Sequence = 3,
        Membership = ChunkMembership.SnapshotRange(Guid.CreateVersion7(), 201, 300),
        ItemCount = 100,
        IdempotencyKey = Key,
        AttemptCount = 1,
        MaxAttempts = 5,
    };

    private sealed class ScriptedExecutor : IJobChunkExecutor
    {
        public ChunkOperationKind OperationKind => ChunkOperationKind.BulkCodingChunk;

        public Func<ChunkExecutionContext, Task<ChunkExecutionResult>> Handler { get; set; } =
            _ => Task.FromResult(ChunkExecutionResult.Complete(ChunkCompletion.Empty));

        public int Calls { get; private set; }

        public CancellationToken Token { get; private set; }

        public Task<ChunkExecutionResult> ExecuteAsync(ChunkExecutionContext context, CancellationToken cancellationToken)
        {
            Calls++;
            Token = cancellationToken;
            return Handler(context);
        }
    }

    private sealed class RecordingMeter : IMessageProcessingMeter
    {
        public int Measured { get; private set; }

        public List<string> Failures { get; } = [];

        public IMessageProcessingMeasurement Measure(string workerType, string messageType, string destination)
        {
            workerType.Should().Be(WorkQueues.BulkCoding.WorkerType);
            messageType.Should().Be(MessageTypes.JobChunk);
            Measured++;
            return new Measurement(Failures);
        }

        private sealed class Measurement(List<string> failures) : IMessageProcessingMeasurement
        {
            public void Fail(string errorType) => failures.Add(errorType);

            public void Dispose()
            {
            }
        }
    }

    private sealed class FakeDbException(bool transient) : DbException("db")
    {
        public override bool IsTransient => transient;
    }

    /// <summary>Claims succeed by default; records every write.</summary>
    private sealed class ScriptedChunks : IJobChunkRepository
    {
        public Func<Guid, Guid, ChunkClaimResult> Claim { get; set; } =
            (ws, _) => new ChunkClaimResult(ChunkClaimOutcome.Claimed, Claimed(ws));

        public Func<ChunkFence> Heartbeat { get; set; } = () => ChunkFence.Proceed;

        public Exception? FailWith { get; set; }

        public ConcurrentQueue<(Guid, Guid)> Claims { get; } = new();

        public ConcurrentQueue<ChunkCompletion> Completions { get; } = new();

        public ConcurrentQueue<ChunkError> Failures { get; } = new();

        public ConcurrentQueue<ChunkLease> Releases { get; } = new();

        public int Writes => Completions.Count + Failures.Count + Releases.Count;

        public Task<ChunkClaimResult> ClaimAsync(
            Guid workspaceId, Guid chunkId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        {
            Claims.Enqueue((workspaceId, chunkId));
            return Task.FromResult(Claim(workspaceId, chunkId));
        }

        public Task<ChunkHeartbeat> HeartbeatAsync(ChunkLease lease, TimeSpan leaseDuration, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChunkHeartbeat(Heartbeat(), DateTimeOffset.UtcNow + leaseDuration));

        public Task<ChunkCommitResult> CompleteAsync(ChunkLease lease, ChunkCompletion completion, CancellationToken cancellationToken = default)
        {
            Completions.Enqueue(completion);
            return Task.FromResult(new ChunkCommitResult(ChunkCommitOutcome.Committed, JobStatus.Running));
        }

        public Task<ChunkFailureResult> FailAsync(ChunkLease lease, ChunkError failure, CancellationToken cancellationToken = default)
        {
            if (FailWith is not null)
            {
                throw FailWith;
            }

            Failures.Enqueue(failure);
            return Task.FromResult(new ChunkFailureResult(
                failure.Class == ChunkErrorClass.Transient ? ChunkFailureOutcome.RetryScheduled : ChunkFailureOutcome.Failed,
                null, JobStatus.Running));
        }

        public Task<ChunkReleaseOutcome> ReleaseAsync(ChunkLease lease, CancellationToken cancellationToken = default)
        {
            Releases.Enqueue(lease);
            return Task.FromResult(ChunkReleaseOutcome.ReturnedToPending);
        }

        public Task<ChunkClaimResult> ClaimNextAsync(
            Guid workspaceId, Guid jobId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> MarkDispatchedAsync(Guid workspaceId, Guid chunkId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DispatchableChunk>> GetDispatchableAsync(
            Guid workspaceId, Guid jobId, int limit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> GetWorkspacesToSweepAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LeaseRecoveryResult> RecoverExpiredLeasesAsync(
            Guid workspaceId, TimeSpan grace, int limit = 100, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
