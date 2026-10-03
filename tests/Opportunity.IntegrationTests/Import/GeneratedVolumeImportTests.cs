using System.Diagnostics;
using System.Globalization;

using AwesomeAssertions;

using Opportunity.Core.Jobs;
using Opportunity.DataGenerator.Corpus.Output;
using Opportunity.DataGenerator.Corpus.Profiles;
using Opportunity.DataGenerator.Corpus.Volumes;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Import;

/// <summary>
/// E08-T03 acceptance: a generator-produced volume (E17-T02) imports with zero errors through the whole pipeline
/// (preparation, chunks, one IndexChunkTask each). The default size is 10K documents; set
/// <c>OPPORTUNITY_IMPORT_SCALE_DOCS</c> (e.g. 1000000) for the throughput run, which records rows per second in the
/// test output. Generated files live in a temp directory removed afterwards.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class GeneratedVolumeImportTests(MigrationPostgresFixture postgres)
{
    private const ulong Seed = 20261003;

    [Fact]
    public async Task A_generated_10k_volume_imports_with_zero_errors_and_one_index_task_per_chunk()
    {
        var documents = int.TryParse(Environment.GetEnvironmentVariable("OPPORTUNITY_IMPORT_SCALE_DOCS"), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : 10_000;
        var root = Path.Combine(Path.GetTempPath(), "opp-genvol-" + Guid.NewGuid().ToString("N"));
        try
        {
            CorpusRunner.Run(ProfileSerializer.WithDocumentCount(new CorpusProfile(), documents), Seed, root, new CorpusRunOptions
            {
                Threads = 2,
                WriteGroundTruth = false,
                SinkFactories = [context => new VolumeWriter(context, root, new VolumeOptions { IncludeNatives = false, IncludeText = false, IncludeImages = false })],
            });
            var dat = await File.ReadAllBytesAsync(Path.Combine(root, "VOL001", "DATA", "VOL001.dat"), TestContext.Current.CancellationToken);

            await using var h = await ImportHarness.CreateAsync(postgres);
            var ws = await h.WorkspaceAsync();
            var batch = await h.StartAsync(ws, dat, name: "VOL001.dat");

            var clock = Stopwatch.StartNew();
            await h.PrepareAsync(batch);
            var prepared = clock.Elapsed;
            await h.DeliverOpenChunksAsync(batch);
            var total = clock.Elapsed;

            var job = (await h.Jobs.GetAsync(ws, batch.JobId, TestContext.Current.CancellationToken))!;
            (await h.IssuesAsync(batch)).Should().BeEmpty();
            job.Status.Should().Be(JobStatus.Completed);
            var report = await h.BatchAsync(batch);
            (report.Preparation!.RowsTotal, report.RowsImported, report.RowsErrored).Should().Be((documents, documents, 0L));
            (await h.CountAsync("SELECT count(*) FROM opportunity.document WHERE workspace_id = @ws", ws)).Should().Be(documents);
            var tasks = await h.IndexTasksPerChunkAsync(batch);
            tasks.Should().HaveCount((int)job.Counters.ChunksTotal).And.OnlyContain(t => t.Value == 1);
            (await h.CountAsync("SELECT count(*) FROM opportunity.search_outbox WHERE workspace_id = @ws", ws)).Should().Be(0);

            TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Imported {documents} documents ({dat.Length / 1024} KiB DAT, {job.Counters.ChunksTotal} chunks): preparation {prepared.TotalSeconds:F1} s, total {total.TotalSeconds:F1} s, {documents / total.TotalSeconds:F0} rows/s (PostgreSQL phase; indexing is the index worker's, E07)."));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
