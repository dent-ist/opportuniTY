using AwesomeAssertions;

using Opportunity.Application.Search;
using Opportunity.Contracts.Import;
using Opportunity.Contracts.Search;
using Opportunity.Core.Jobs;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Import;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Search.Projection;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// E08-T07 end to end: an overlay whose TextLink names new extracted text replaces the document's text (the prior object
/// stays registered), bumps its version and is reindexed through the chunk's IndexChunkTask, so search finds the new
/// words and no longer the old ones, and the index holds exactly the current PostgreSQL projection.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class OverlayReindexTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_text_link_overlay_replaces_the_text_and_reindexes_the_document()
    {
        var share = Path.Combine(Path.GetTempPath(), "opp-overlay-share-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(share, "VOL001", "TEXT"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(share, "VOL001", "TEXT", "a.txt"), "first draft mentions zebracorn pricing", Ct);
            await File.WriteAllTextAsync(Path.Combine(share, "VOL001", "TEXT", "b.txt"), "corrected text mentions quasarfish pricing", Ct);
            await using var search = await SearchHarness.CreateAsync(openSearch, postgres);
            var ws = await search.WorkspaceAsync();
            await search.Db.Core.Fields.InitializeWorkspaceAsync(ws, Ct);
            var user = await search.MemberAsync(ws);
            await using var import = ImportHarness.Over(search.Db.Core, volumeRoot: share);
            await using var index = await ChunkIndexHarness.OverAsync(openSearch, import, search.Options, new ProjectionOptions());
            var profile = new ImportProfileDefinition
            {
                Paths = new PathSettings { VolumeRoot = "VOL001" },
                Columns = [new ColumnMapping { Column = "DOCTEXT", Targets = [new MappingTarget { Kind = MappingTargetKind.Structural, Structural = StructuralTarget.TextPath }] }],
            };

            var load = await import.StartAsync(ws, ImportHarness.Utf8Bom(ImportHarness.Dat(["BEGDOC", "DOCTEXT"], ["TX-1", @"TEXT\a.txt"])), profile);
            (await import.RunAsync(load)).Status.Should().Be(JobStatus.Completed);
            await index.DeliverAllAsync(ws, load.JobId);
            (await index.CountAsync(ws)).Should().Be(1);
            (await ControlNumbersAsync(search, ws, user, "zebracorn")).Should().Equal("TX-1");
            var before = (await import.DocumentsAsync(ws))["TX-1"];

            var overlay = await import.StartAsync(ws, ImportHarness.Utf8Bom(ImportHarness.Dat(["BEGDOC", "DOCTEXT"], ["TX-1", @"TEXT\b.txt"])), profile, ImportMode.Overlay);
            (await import.RunAsync(overlay)).Status.Should().Be(JobStatus.Completed);
            var after = (await import.DocumentsAsync(ws))["TX-1"];
            after.Version.Should().Be(before.Version + 1);
            (await index.TasksAsync(ws, overlay.JobId)).Should().ContainSingle();
            await index.DeliverAllAsync(ws, overlay.JobId);
            (await index.DriftAsync(ws, [after.DocumentId])).Should().BeEmpty("the index holds the overlaid version");
            (await index.CountAsync(ws)).Should().Be(1);
            await search.ExpireReadersAsync();

            (await ControlNumbersAsync(search, ws, user, "quasarfish")).Should().Equal("TX-1");
            (await ControlNumbersAsync(search, ws, user, "zebracorn")).Should().BeEmpty("the overlay replaced the text");
            (await import.CountAsync(
                $"SELECT count(*) FROM opportunity.stored_object WHERE workspace_id = @ws AND document_id = '{after.DocumentId}' AND area = 2", ws))
                .Should().Be(2, "the prior text object is retained (ADR-011 §4.2)");
        }
        finally
        {
            Directory.Delete(share, recursive: true);
        }
    }

    private static async Task<List<string>> ControlNumbersAsync(SearchHarness search, Guid ws, Guid user, string query)
    {
        var outcome = await search.SearchAsync(ws, user, query, pageSize: 10);
        outcome.Status.Should().Be(SearchStatus.Ok, string.Join("; ", outcome.QueryErrors.Select(e => e.Code + " " + e.Message)));
        return [.. outcome.Page!.Items.Select(i => i.ControlNumber).Order(StringComparer.Ordinal)];
    }
}
