using AwesomeAssertions;

using Opportunity.Contracts.Import;
using Opportunity.Core.Jobs;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Import;

/// <summary>
/// E08-T03 × E09-T02: upstream duplicate groups and email threads mapped in a load file are recorded by each import
/// chunk's own transaction (RelationshipWriter), roll back with a failed attempt, survive redelivery unchanged and are
/// recounted when an overlay moves a document to another group.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class ImportRelationshipTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Header = ["BEGDOC", "DuplicateGroupID", "EmailThreadID", "DedupeHash"];

    [Fact]
    public async Task Groups_and_threads_are_written_in_the_chunk_transaction_and_survive_a_failed_attempt_and_redelivery()
    {
        await using var h = await ImportHarness.CreateAsync(postgres, rowsPerChunk: 2);
        var ws = await h.WorkspaceAsync();
        var dat = ImportHarness.Utf8Bom(ImportHarness.Dat(
            Header,
            ["DUP-1", "G1", "T1", "aa01"],
            ["DUP-2", "G1", "T1", "aa01"],
            ["DUP-3", "G1", "", "aa01"],
            ["DUP-4", "G2", "T2", ""],
            ["DUP-5", "", "", ""]));
        var batch = await h.StartAsync(ws, dat);
        await h.PrepareAsync(batch);
        var chunks = await h.Jobs.GetChunksAsync(ws, batch.JobId, cancellationToken: Ct);
        chunks.Should().HaveCount(3);

        // Chunk 2's transaction fails after its documents and relationships were written: nothing of it may remain.
        await h.Db.ExecuteAsync(
            """
            CREATE TABLE public.relationship_crash_flag (armed boolean);
            CREATE FUNCTION public.relationship_crash() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
            BEGIN
                IF NEW.row_no = 3 AND EXISTS (SELECT FROM public.relationship_crash_flag) THEN
                    RAISE EXCEPTION 'simulated crash inside the chunk transaction' USING ERRCODE = '40001';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER relationship_crash BEFORE INSERT ON opportunity.import_batch_member FOR EACH ROW EXECUTE FUNCTION public.relationship_crash();
            INSERT INTO public.relationship_crash_flag VALUES (true);
            """);
        await h.DeliverOpenChunksAsync(batch);
        (await GroupCountAsync(h, ws, "G1")).Should().Be(2, "only chunk 1's documents are committed");
        (await h.CountAsync("SELECT count(*) FROM opportunity.duplicate_group WHERE workspace_id = @ws", ws)).Should().Be(1);

        await h.Db.ExecuteAsync("DELETE FROM public.relationship_crash_flag");
        await h.Db.ExecuteAsync("UPDATE opportunity.job_chunk SET available_at = now() - interval '1 minute' WHERE workspace_id = @ws", ("ws", ws));
        await h.DeliverOpenChunksAsync(batch);
        foreach (var chunk in chunks)
        {
            await h.Consumer().HandleAsync(ImportHarness.Payload(chunk), ImportHarness.Received(chunk, redelivered: true), Ct);
        }

        (await h.Jobs.GetAsync(ws, batch.JobId, Ct))!.Status.Should().Be(JobStatus.Completed);
        (await GroupCountAsync(h, ws, "G1")).Should().Be(3);
        (await GroupCountAsync(h, ws, "G2")).Should().Be(1);
        (await h.CountAsync("SELECT count(*) FROM opportunity.duplicate_group WHERE workspace_id = @ws", ws)).Should().Be(2);
        (await h.CountAsync("SELECT count(*) FROM opportunity.email_thread WHERE workspace_id = @ws AND member_count = 2", ws)).Should().Be(1);
        (await h.CountAsync("SELECT count(*) FROM opportunity.email_thread WHERE workspace_id = @ws", ws)).Should().Be(2);
        (await h.CountAsync("SELECT count(*) FROM opportunity.document WHERE workspace_id = @ws AND is_duplicate_primary", ws))
            .Should().Be(2, "one primary per group");
        (await h.CountAsync("SELECT count(*) FROM opportunity.document WHERE workspace_id = @ws AND upstream_dedupe_hash = 'aa01'", ws)).Should().Be(3);
        (await h.CountAsync("SELECT count(*) FROM opportunity.document WHERE workspace_id = @ws", ws)).Should().Be(5);

        // An overlay moving DUP-3 from G1 to G2 recounts both groups.
        var overlay = await h.StartAsync(ws, ImportHarness.Utf8Bom(ImportHarness.Dat(Header, ["DUP-3", "G2", "", ""])), mode: ImportMode.Overlay);
        await h.RunAsync(overlay);
        (await GroupCountAsync(h, ws, "G1")).Should().Be(2);
        (await GroupCountAsync(h, ws, "G2")).Should().Be(2);
    }

    private static Task<int> GroupCountAsync(ImportHarness h, Guid ws, string value) =>
        h.Db.ScalarAsync<int>(
            "SELECT coalesce((SELECT member_count FROM opportunity.duplicate_group WHERE workspace_id = @ws AND hash_value = @value), 0)",
            ("ws", ws), ("value", value));
}
