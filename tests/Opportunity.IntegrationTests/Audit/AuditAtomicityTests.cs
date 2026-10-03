using System.Text.Json.Nodes;

using AwesomeAssertions;

using Npgsql;

using Opportunity.Application.Audit;
using Opportunity.Application.Coding;
using Opportunity.Application.Fields;
using Opportunity.Application.Jobs;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;
using Opportunity.Core.Jobs;
using Opportunity.Data.Jobs;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Jobs;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Audit;

/// <summary>
/// E14-T01 acceptance: a coding/privilege change and its audit event commit atomically. Fault injection on either
/// side shows there is no action without audit and no audit without the action (ADR-013 §2.1, §2.4).
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class AuditAtomicityTests(MigrationPostgresFixture postgres)
{
    private static readonly Guid Reviewer = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_privilege_change_commits_with_its_audit_event_carrying_the_security_values()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var w = await WorkspaceAsync(db);
        var doc = await db.InsertDocumentAsync(w.Id, "DOC0001");

        var result = await db.Coding.ApplyAsync(PrivilegeChange(w, doc.DocumentId), Ct);

        result.Outcome.Should().Be(CodingWriteOutcome.Applied);
        result.TouchesSecurityAffectingField.Should().BeTrue();
        var audit = (await AuditSamples.ReadAllAsync(db, w.Id)).Should().ContainSingle().Subject.Event;
        audit.Category.Should().Be("Coding");
        audit.Action.Should().Be("Changed");
        audit.ResourceType.Should().Be("Document");
        audit.ResourceId.Should().Be(doc.DocumentId.ToString());
        audit.ActorId.Should().Be(Reviewer.ToString());
        audit.Details.Should().Contain("SecurityAffecting", "true")
            .And.Contain("CodingEvents", "2")
            .And.Contain($"Field.{w.Privilege}.Old", null)
            .And.Contain($"Field.{w.Privilege}.New", w.Privileged.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .And.NotContainKey($"Field.{w.Responsive}.New", "values are recorded only for security-affecting fields");
        var writeId = await db.ScalarAsync<Guid>("SELECT write_id FROM opportunity.coding_write");
        audit.Details["CodingWriteId"].Should().Be(writeId.ToString(), "the audit event links to its CodingEvents");
    }

    [Fact]
    public async Task When_the_audit_insert_fails_the_coding_change_does_not_happen()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var w = await WorkspaceAsync(db);
        var doc = await db.InsertDocumentAsync(w.Id, "DOC0001");
        await InjectFailureAsync(db, "audit.audit_event");
        var request = PrivilegeChange(w, doc.DocumentId);

        var act = () => db.Coding.ApplyAsync(request, Ct);
        (await act.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Be("injected failure");

        (await db.Documents.GetVersionAsync(w.Id, doc.DocumentId, Ct)).Should().Be(1);
        (await db.ScalarAsync<long>("SELECT count(*) FROM opportunity.document_coding_field")).Should().Be(0);
        (await db.ScalarAsync<long>("SELECT count(*) FROM opportunity.document_coding_choice")).Should().Be(0);
        (await db.ScalarAsync<long>("SELECT count(*) FROM opportunity.coding_event")).Should().Be(0);
        (await db.ScalarAsync<long>("SELECT count(*) FROM opportunity.coding_write")).Should().Be(0);

        // Once the store accepts events again, the same request applies, with exactly one event.
        await db.ExecuteAsync("DROP TRIGGER test_fail ON audit.audit_event");
        (await db.Coding.ApplyAsync(request, Ct)).Outcome.Should().Be(CodingWriteOutcome.Applied);
        (await AuditSamples.ReadAllAsync(db, w.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task When_the_coding_write_fails_after_the_audit_insert_no_audit_event_remains()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var w = await WorkspaceAsync(db);
        var doc = await db.InsertDocumentAsync(w.Id, "DOC0001");
        await InjectFailureAsync(db, "opportunity.coding_event");

        var act = () => db.Coding.ApplyAsync(PrivilegeChange(w, doc.DocumentId), Ct);
        await act.Should().ThrowAsync<PostgresException>();

        (await db.ScalarAsync<long>("SELECT count(*) FROM audit.audit_event")).Should().Be(0);
        (await db.ScalarAsync<long>("SELECT count(*) FROM opportunity.document_coding_field")).Should().Be(0);
    }

    [Fact]
    public async Task Bulk_chunks_are_audited_once_per_chunk_with_the_job()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var w = await WorkspaceAsync(db);
        var docs = new List<Guid>();
        for (var i = 1; i <= 3; i++)
        {
            docs.Add((await db.InsertDocumentAsync(w.Id, $"DOC{i:D4}")).DocumentId);
        }

        var job = Guid.CreateVersion7();
        var result = await db.Coding.ApplyAsync(new CodingWriteRequest
        {
            WorkspaceId = w.Id,
            IdempotencyKey = "chunk-1",
            Actor = new CodingActor(Reviewer, CodingActorType.BulkHuman),
            JobId = job,
            Documents = [.. docs.Select(d => new CodingTarget(d, 1))],
            Operations = [CodingFieldOperation.Set(w.Responsive, JsonValue.Create(true))],
            Audit = Template(w.Id) with { ActorType = AuditActorType.Service, ActorId = "worker:bulk-coding", OnBehalfOf = Reviewer, ChunkSequence = 1, JobId = job },
        }, Ct);

        result.EventsWritten.Should().Be(3);
        var audit = (await AuditSamples.ReadAllAsync(db, w.Id)).Should().ContainSingle("never one event per document").Subject.Event;
        audit.Action.Should().Be("BulkChunkApplied");
        audit.ResourceType.Should().Be("Job");
        audit.JobId.Should().Be(job);
        audit.ChunkSequence.Should().Be(1);
        audit.Details.Should().Contain("Documents", "3").And.Contain("Changed", "3").And.Contain("SecurityAffecting", "false");
    }

    [Fact]
    public async Task Job_lifecycle_events_commit_with_the_job_change()
    {
        await using var jobs = await JobDatabase.CreateAsync(postgres);
        await using var db = await jobs.AsAppRoleAsync();
        var ws = await db.Core.CreateWorkspaceAsync();
        var initiator = Guid.CreateVersion7();
        await db.Core.ExecuteAsync(
            "INSERT INTO opportunity.app_user (user_id, issuer, subject, display_name, groups_refreshed_at, last_sign_in_at) " +
            "VALUES (@id, 'https://idp.test', 'sub-1', 'Pat Admin', now(), now())", ("id", initiator));

        // Fault injection: the job is not created when its audit event cannot be written.
        await InjectFailureAsync(db.Core, "audit.audit_event");
        var newJob = new NewJob { WorkspaceId = ws, JobType = JobType.BulkCoding, InitiatedBy = initiator, CorrelationId = "corr-job-1" };
        var create = () => db.Jobs.CreateAsync(newJob, Ct);
        await create.Should().ThrowAsync<PostgresException>();
        (await db.Core.ScalarAsync<long>("SELECT count(*) FROM opportunity.job")).Should().Be(0);
        await db.Core.ExecuteAsync("DROP TRIGGER test_fail ON audit.audit_event");

        var created = await db.Jobs.CreateAsync(newJob, Ct);
        (await db.Jobs.CancelAsync(ws, created.Job.JobId, initiator, cancellationToken: Ct)).Status.Should().Be(JobStatus.Cancelled);

        var (_, failingJob) = await db.CreateRunningJobAsync(chunkCount: 1, workspaceId: ws);
        var claim = (await db.Chunks.ClaimNextAsync(ws, failingJob, "worker", JobDatabase.Lease, Ct)).Chunk!;
        (await db.Chunks.FailAsync(claim.Lease, ChunkError.Permanent("EnvelopeMismatch", "workspace differs"), Ct))
            .JobStatus.Should().Be(JobStatus.CompletedWithErrors);
        (await db.Jobs.ReplayFailedChunksAsync(ws, failingJob, requestedBy: initiator, cancellationToken: Ct)).ChunksReplayed.Should().Be(1);
        (await db.Jobs.FailAsync(ws, failingJob, "planner gave up", Ct)).Status.Should().Be(JobStatus.Failed);

        var events = await ReadJobEventsAsync(db.Core, ws);
        events.Select(e => (e.Event.ResourceId, e.Event.Action)).Should().Equal(
            (created.Job.JobId.ToString(), "Created"),
            (created.Job.JobId.ToString(), "Cancelled"),
            (failingJob.ToString(), "Created"),
            (failingJob.ToString(), "CompletedWithErrors"),
            (failingJob.ToString(), "Replayed"),
            (failingJob.ToString(), "Failed"));
        events[0].Event.Should().BeEquivalentTo(new
        {
            ActorType = AuditActorType.User,
            ActorId = initiator.ToString(),
            ActorDisplay = "Pat Admin",
            CorrelationId = "corr-job-1",
            JobId = created.Job.JobId,
        });
        events[3].Event.ActorId.Should().Be(JobSql.JobEngineActor);
        events[3].Event.Outcome.Should().Be(AuditOutcome.Failure);
        events[3].Event.Details.Should().Contain("ChunksFailed", "1");
        events[4].Event.ActorId.Should().Be(initiator.ToString());
        events[5].Event.OnBehalfOf.Should().NotBeNull();
        events[5].Event.Details.Values.Should().NotContain("planner gave up", "free-text reasons are not copied into audit");
    }

    private static async Task<List<StoredAuditEvent>> ReadJobEventsAsync(CoreSchemaDatabase db, Guid ws) =>
        [.. (await AuditSamples.ReadAllAsync(db, ws)).Where(e => e.Event.Category == "Job").OrderBy(e => e.RecordedAt)];

    private static Task InjectFailureAsync(CoreSchemaDatabase db, string table) => db.ExecuteAsync(
        $"""
        CREATE OR REPLACE FUNCTION public.test_fail() RETURNS trigger LANGUAGE plpgsql AS
        $$ BEGIN RAISE EXCEPTION 'injected failure'; END $$;
        CREATE TRIGGER test_fail BEFORE INSERT ON {table} FOR EACH ROW EXECUTE FUNCTION public.test_fail();
        """);

    private static AuditEvent Template(Guid ws) => new()
    {
        WorkspaceId = ws,
        OccurredAt = DateTimeOffset.UtcNow,
        Category = "Coding",
        Action = "Changed",
        ActorType = AuditActorType.User,
        ActorId = Reviewer.ToString(),
        ActorDisplay = "Rev Iewer",
        ClientIp = "198.51.100.4",
        Outcome = AuditOutcome.Success,
        CorrelationId = "corr-coding",
    };

    private static CodingWriteRequest PrivilegeChange(TestWorkspace w, Guid documentId) => new()
    {
        WorkspaceId = w.Id,
        IdempotencyKey = "ui-" + Guid.CreateVersion7().ToString("N"),
        Actor = new CodingActor(Reviewer, CodingActorType.Human),
        Documents = [new CodingTarget(documentId)],
        Operations =
        [
            CodingFieldOperation.Set(w.Responsive, JsonValue.Create(true)),
            CodingFieldOperation.Set(w.Privilege, JsonValue.Create(w.Privileged)),
        ],
        Audit = Template(w.Id),
    };

    private static async Task<TestWorkspace> WorkspaceAsync(CoreSchemaDatabase db)
    {
        var ws = await db.CreateWorkspaceAsync();
        await db.Fields.InitializeWorkspaceAsync(ws, Ct);
        var responsive = (await db.Fields.CreateFieldAsync(new NewField(ws, "Responsive", FieldType.Boolean, FieldStorage.Coding), Ct)).Value!.FieldId;
        var privilege = (await db.Fields.CreateFieldAsync(new NewField(ws, "Privilege Status", FieldType.SingleChoice, FieldStorage.Coding,
            SecurityClass: SecurityClass.PrivilegeStatus), Ct)).Value!.FieldId;
        var privileged = (await db.Fields.AddChoiceAsync(ws, privilege, "Privileged", Ct)).Value!.ChoiceId;
        return new TestWorkspace(ws, responsive, privilege, privileged);
    }

    private sealed record TestWorkspace(Guid Id, int Responsive, int Privilege, int Privileged);
}
