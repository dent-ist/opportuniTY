using AwesomeAssertions;

using Opportunity.Application.Search;
using Opportunity.Contracts.Search;
using Opportunity.Core.Jobs;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Import;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Search.Projection;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// E08-T04 end to end: a generated volume with natives and extracted text is imported (text stored in object storage),
/// indexed by the chunk index worker through the real object-storage text loader, and searched with the search
/// service: a keyword found only in a document's extracted text finds it, and with a small indexed-text cap the long
/// texts are searchable as <c>texttruncated:true</c> while words past the cap are not indexed (Q-29).
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class ImportedTextSearchTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private const int TextCap = 1_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Keywords_only_in_imported_extracted_text_find_their_documents()
    {
        using var volume = GeneratedVolume.Create(6, new Dictionary<Opportunity.DataGenerator.Corpus.Volumes.DefectType, double>(), seed: 78);
        await using var search = await SearchHarness.CreateAsync(openSearch, postgres);
        var ws = await search.WorkspaceAsync();
        await search.Db.Core.Fields.InitializeWorkspaceAsync(ws, Ct);
        var user = await search.MemberAsync(ws);
        await using var import = ImportHarness.Over(search.Db.Core, rowsPerChunk: 4, volumeRoot: volume.Share, textCap: TextCap);
        await using var index = await ChunkIndexHarness.OverAsync(openSearch, import, search.Options, new ProjectionOptions { IndexedTextCap = TextCap });

        var batch = await import.StartAsync(ws, volume.Dat, GeneratedVolume.Profile(), name: "VOL001.dat");
        (await import.RunAsync(batch)).Status.Should().Be(JobStatus.Completed);
        await index.DeliverAllAsync(ws, batch.JobId);
        (await index.CountAsync(ws)).Should().Be(6);

        // Ground truth from the volume itself: each document's text, and the DAT (which must not contain the keyword).
        var dat = System.Text.Encoding.UTF8.GetString(volume.Dat).ToLowerInvariant();
        var documents = await ImportArtifactTests.ArtifactsAsync(import, ws);
        var texts = documents.Keys.ToDictionary(cn => cn, cn => File.ReadAllText(volume.File("TEXT", cn)!), StringComparer.Ordinal);
        var indexed = texts.ToDictionary(t => t.Key, t => IndexedText.Cap(t.Value, TextCap).Text.ToLowerInvariant(), StringComparer.Ordinal);

        // A whole word from the indexed part of one document's text that the load file never mentions.
        var (target, keyword) = texts
            .SelectMany(t => Words(t.Value[..Math.Min(t.Value.Length, TextCap / 2)]).Select(w => (ControlNumber: t.Key, Word: w)))
            .First(x => x.Word.Length >= 6 && !dat.Contains(x.Word, StringComparison.Ordinal));
        var hits = await ControlNumbersAsync(search, ws, user, keyword);
        hits.Should().Contain(target);
        hits.Should().OnlyContain(cn => indexed[cn].Contains(keyword, StringComparison.Ordinal));

        // Q-29: the cap applies to the index only; the flag is searchable.
        var truncated = texts.Where(t => t.Value.Length > TextCap).Select(t => t.Key).Order(StringComparer.Ordinal).ToList();
        truncated.Should().NotBeEmpty("generated texts are longer than the test cap");
        documents.Where(d => d.Value.TextTruncated).Select(d => d.Key).Order(StringComparer.Ordinal).Should().Equal(truncated);
        (await ControlNumbersAsync(search, ws, user, "texttruncated:true")).Should().Equal(truncated);
        var pastCap = truncated
            .SelectMany(cn => Words(texts[cn][IndexedText.Cap(texts[cn], TextCap).Text.Length..]))
            .FirstOrDefault(w => w.Length >= 6 && !dat.Contains(w, StringComparison.Ordinal) && !texts.Keys.Any(cn => indexed[cn].Contains(w, StringComparison.Ordinal)));
        if (pastCap is not null)
        {
            (await ControlNumbersAsync(search, ws, user, pastCap)).Should().BeEmpty("words past the cap stay in storage but are not indexed");
        }
    }

    private static async Task<List<string>> ControlNumbersAsync(SearchHarness search, Guid ws, Guid user, string query)
    {
        var outcome = await search.SearchAsync(ws, user, query, pageSize: 100);
        outcome.Status.Should().Be(SearchStatus.Ok, string.Join("; ", outcome.QueryErrors.Select(e => e.Code + " " + e.Message)));
        return [.. outcome.Page!.Items.Select(i => i.ControlNumber).Order(StringComparer.Ordinal)];
    }

    /// <summary>Whole, letters-only words (lower case), as the standard tokenizer would emit them.</summary>
    private static IEnumerable<string> Words(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('.', ',', ';', ':', '!', '?', '"', '(', ')', '[', ']'))
            .Where(t => t.Length > 0 && t.All(char.IsAsciiLetter))
            .Select(t => t.ToLowerInvariant());
}
