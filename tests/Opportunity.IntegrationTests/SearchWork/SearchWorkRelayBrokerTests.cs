using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Coding;
using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Application.SearchWork;
using Opportunity.Contracts.Messaging.Indexing;
using Opportunity.Core.Coding;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchWork;
using Opportunity.IntegrationTests.Messaging;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Testing.RabbitMq;

namespace Opportunity.IntegrationTests.SearchWork;

[CollectionDefinition(Name)]
public sealed class SearchWorkBrokerGroup : ICollectionFixture<MigrationPostgresFixture>, ICollectionFixture<RabbitMqFixture>
{
    public const string Name = "Search work relay (PostgreSQL + RabbitMQ)";
}

/// <summary>E06-T03: the relay against the real RabbitMQ transport, publisher confirms included (ADR-001 §6.1).</summary>
[Collection(SearchWorkBrokerGroup.Name)]
public sealed class SearchWorkRelayBrokerTests(MigrationPostgresFixture postgres, RabbitMqFixture rabbit)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Committed_outbox_rows_and_chunk_tasks_reach_their_lane_queues_payload_free_and_are_marked_dispatched()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        await using var vhost = await rabbit.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        var w = await db.WorkspaceAsync(documents: 3);

        await db.Core.Coding.ApplyAsync(SearchWorkDatabase.SetResponsive(w, w.Documents[0], true), Ct);
        await db.Core.Coding.ApplyAsync(SearchWorkDatabase.Interactive(w.Id, w.Documents[1],
            CodingFieldOperation.Set(w.Privilege, JsonValue.Create(w.Privileged))), Ct);
        var snapshot = Guid.CreateVersion7();
        var chunk = await db.RunningJobChunkAsync(w.Id, JobType.BulkCoding, ChunkOperationKind.BulkCodingChunk,
            [new ChunkPlan(ChunkMembership.SnapshotRange(snapshot, 1, 1), 1)], snapshotId: snapshot);
        var bulk = await db.Core.Coding.ApplyChunkAsync(chunk, new CodingWriteRequest
        {
            WorkspaceId = w.Id,
            IdempotencyKey = chunk.IdempotencyKey,
            Actor = new CodingActor(SearchWorkDatabase.Reviewer, CodingActorType.BulkHuman),
            JobId = chunk.Lease.JobId,
            Documents = [new CodingTarget(w.Documents[2], 1)],
            Operations = [CodingFieldOperation.Set(w.Responsive, JsonValue.Create(false))],
        }, Ct);

        var relay = new SearchWorkRelay(db.Outbox, db.Tasks, harness.Publisher, new SearchWorkRelayOptions { Owner = "dispatcher-1" });
        var result = await relay.RelayOnceAsync(w.Id, Ct);

        result.Should().Be(new SearchWorkRelayResult(2, 0, 1, 0));
        var interactive = harness.Serializer.Read((await harness.GetAsync(WorkQueues.IndexInteractive.Name)).Body);
        var security = harness.Serializer.Read((await harness.GetAsync(WorkQueues.IndexSecurity.Name)).Body);
        var task = harness.Serializer.Read((await harness.GetAsync(WorkQueues.IndexBulk.Name)).Body);

        var interactivePayload = interactive.Payload.Should().BeOfType<SearchOutboxMessage>().Subject;
        interactivePayload.DocumentId.Should().Be(w.Documents[0]);
        interactivePayload.DocumentVersion.Should().Be(2);
        interactive.Envelope!.IdempotencyKey.Should().Be(ChunkIdempotencyKey.ForOutbox(w.Id, interactivePayload.OutboxId));
        interactive.Envelope.WorkspaceId.Should().Be(w.Id);
        interactive.Envelope.Attempt.Should().Be(1);
        security.Payload.Should().BeOfType<SearchOutboxMessage>().Which.DocumentId.Should().Be(w.Documents[1]);
        task.Payload.Should().Be(new IndexChunkTaskMessage { TaskId = bulk.IndexTaskId!.Value });
        task.Envelope!.JobId.Should().Be(chunk.Lease.JobId);

        (await db.Outbox.GetAsync(w.Id, interactivePayload.OutboxId, Ct))!.Status.Should().Be(SearchOutboxStatus.Dispatched);
        (await db.Tasks.GetAsync(w.Id, bulk.IndexTaskId!.Value, Ct))!.Status.Should().Be(IndexChunkTaskStatus.Dispatched);
        (await relay.RelayOnceAsync(w.Id, Ct)).Claimed.Should().Be(0, "dispatched work is not published again");
    }
}
