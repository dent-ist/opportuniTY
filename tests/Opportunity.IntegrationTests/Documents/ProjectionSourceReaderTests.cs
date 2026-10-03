using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Coding;
using Opportunity.Application.Fields;
using Opportunity.Application.Search.Projection;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;
using Opportunity.Data.Search;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Search.Projection;

namespace Opportunity.IntegrationTests.Documents;

/// <summary>
/// E07-T02: the projection builder fed from authoritative PostgreSQL state (app login, RLS on): documents, metadata,
/// coding current state, relationship ids and DocumentVersion read in one snapshot.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class ProjectionSourceReaderTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Builds_version_safe_projections_from_postgres_state()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();
        await db.Fields.InitializeWorkspaceAsync(ws, Ct);
        async Task<FieldDefinition> Field(NewField field) => (await db.Fields.CreateFieldAsync(field, Ct)).Value!;
        var custodian = await Field(new NewField(ws, "Custodian", FieldType.Keyword, FieldStorage.Metadata, IsMultiValue: true));
        var subject = await Field(new NewField(ws, "Subject", FieldType.Text, FieldStorage.Metadata));
        var responsive = await Field(new NewField(ws, "Responsive", FieldType.Boolean, FieldStorage.Coding));
        var issues = await Field(new NewField(ws, "Issues", FieldType.MultiChoice, FieldStorage.Coding));
        var pricing = (await db.Fields.AddChoiceAsync(ws, issues.FieldId, "Pricing", Ct)).Value!.ChoiceId;

        custodian.SearchSlot.Should().Be("kw.s001");
        responsive.SearchSlot.Should().Be("bool.s001", "coding slots are allocated in their own namespace");

        var parent = await db.InsertDocumentAsync(ws, "ABC0001", d =>
            d.Metadata = $$"""{"{{custodian.Key}}": ["Smith"], "{{subject.Key}}": "Pricing memo"}""");
        var child = await db.InsertDocumentAsync(ws, "ABC0002", d =>
        {
            d.FamilyId = parent.DocumentId;
            d.ParentDocumentId = parent.DocumentId;
            d.FamilySequence = 1;
            d.Md5 = [0xAB, 0xCD, .. new byte[14]];
        });
        var deleted = await db.InsertDocumentAsync(ws, "ABC0003");
        await db.ExecuteAsync(
            "UPDATE opportunity.document_projection_state SET is_deleted = true, deleted_at = now(), document_version = 5 WHERE workspace_id = @ws AND document_id = @id",
            ("ws", ws), ("id", deleted.DocumentId));

        var coded = await db.Coding.ApplyAsync(new CodingWriteRequest
        {
            WorkspaceId = ws,
            IdempotencyKey = "projection-test-1",
            Actor = new CodingActor(Guid.CreateVersion7(), CodingActorType.Human),
            Documents = [new CodingTarget(parent.DocumentId)],
            Operations = [CodingFieldOperation.Set(responsive.FieldId, JsonValue.Create(true)), CodingFieldOperation.AddChoices(issues.FieldId, pricing)],
        }, Ct);
        coded.Outcome.Should().Be(CodingWriteOutcome.Applied);

        // A document of another workspace is invisible under RLS: it reads as missing.
        var otherWs = await db.CreateWorkspaceAsync();
        var foreign = await db.InsertDocumentAsync(otherWs, "ABC0001");
        var absent = Guid.CreateVersion7();

        var reader = new ProjectionSourceReader(db.AppDataSource);
        var batch = await reader.ReadAsync(ws, [parent.DocumentId, child.DocumentId, deleted.DocumentId, foreign.DocumentId, absent, parent.DocumentId], Ct);

        batch.Documents.Select(d => (d.DocumentId, d.State, d.DocumentVersion)).Should().Equal(
            (parent.DocumentId, ProjectionSourceState.Live, 2L),
            (child.DocumentId, ProjectionSourceState.Live, 1L),
            (deleted.DocumentId, ProjectionSourceState.Deleted, 5L),
            (foreign.DocumentId, ProjectionSourceState.Missing, (long?)null),
            (absent, ProjectionSourceState.Missing, (long?)null));

        var builder = new CandidateAProjectionBuilder();
        var projections = batch.Documents.Select(s => builder.Build(s, batch.Catalog, null)).ToList();

        var p = projections[0].Writes.Single();
        p.Should().BeEquivalentTo(new { Kind = ProjectionWriteKind.Index, Version = 2L });
        p.Body!["projectionVersion"]!.GetValue<long>().Should().Be(2);
        p.Body["controlNumberSort"]!.GetValue<string>().Should().Be("ABC00000000000000000001");
        p.Body["metadata"]!.ToJsonString().Should().Be("""{"kw":{"s001":["Smith"]},"txt":{"s001":"Pricing memo"}}""");
        p.Body["coding"]!.ToJsonString().Should().Be($$$"""{"bool":{"s001":true},"ch":{"s001":["{{{pricing}}}"]}}""");

        var c = projections[1].Writes.Single().Body!;
        c["familyId"]!.GetValue<string>().Should().Be(parent.DocumentId.ToString("D"));
        c["parentDocumentId"]!.GetValue<string>().Should().Be(parent.DocumentId.ToString("D"));
        c["familySequence"]!.GetValue<int>().Should().Be(1);
        c["md5"]!.GetValue<string>().Should().Be("abcd" + new string('0', 28));
        c["coding"].Should().BeNull();

        projections[2].Writes.Should().Equal(new ProjectionWrite(deleted.DocumentId.ToString("D"), ProjectionWriteKind.Delete, 5, null));
        projections[3].Writes.Single().Kind.Should().Be(ProjectionWriteKind.DeleteUnconditional);
    }
}
