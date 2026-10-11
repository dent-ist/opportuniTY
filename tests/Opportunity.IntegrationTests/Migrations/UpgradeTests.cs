using System.Globalization;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.Extensions.Diagnostics.HealthChecks;

using Npgsql;

using Opportunity.Application.Coding;
using Opportunity.Core.Coding;
using Opportunity.Core.Documents;
using Opportunity.Data;
using Opportunity.Data.Migrations;
using Opportunity.IntegrationTests.Documents;

namespace Opportunity.IntegrationTests.Migrations;

/// <summary>
/// E01-T06 upgrade test, database tier: deploy the N-1 schema, load data (<c>tools/release/upgrade/seed.sql</c>),
/// upgrade with this build's migrator, then smoke-test with this build's code. Runs in every integration run against
/// the baseline in <c>tools/release/upgrade/baseline.env</c>; the release workflow points it at the previous release's
/// own migration scripts (<c>OPPORTUNITY_UPGRADE_FROM_SCRIPTS</c>, extracted from its tag), so an edited or missing
/// historical script fails the release. The image tier (<c>tools/release/upgrade-test.sh</c>) runs the N-1 and N
/// migrator images themselves. See docs/releasing.md.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class UpgradeTests(MigrationPostgresFixture postgres)
{
    private static readonly Guid WorkspaceA = Guid.Parse("00000000-0000-4000-8000-00000000a0a0");
    private static readonly Guid WorkspaceB = Guid.Parse("00000000-0000-4000-8000-00000000b0b0");
    private static readonly Guid Reviewer = Guid.Parse("00000000-0000-4000-8000-0000000000e2");
    private const int Responsiveness = 1000;
    private const int Issues = 1001;
    private const int Comments = 1002;
    private const int Pricing = 3;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Previous_release_schema_with_data_upgrades_to_this_release_and_this_code_reads_and_writes_it()
    {
        var source = UpgradeSource.Resolve();
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);

        // 1. Deploy N-1.
        var previous = await new PostgresMigrator(dataSource, source.Scripts).MigrateAsync(Ct);
        previous.EndVersion.Should().Be(source.SchemaVersion, "the N-1 deployment ({0})", source.Label);

        // 2. Load data into the N-1 schema.
        await ExecuteFileAsync(dataSource, "seed.sql");

        // 3. Upgrade to N.
        var upgrade = await new PostgresMigrator(dataSource).MigrateAsync(Ct);
        upgrade.StartVersion.Should().Be(source.SchemaVersion);
        upgrade.EndVersion.Should().Be(MigrationCatalog.LatestVersion);
        upgrade.Applied.Select(s => s.Version).Should().Equal(
            MigrationCatalog.Scripts.Where(s => s.Version > source.SchemaVersion).Select(s => s.Version));
        (await new PostgresMigrator(dataSource).MigrateAsync(Ct)).WasNoOp.Should().BeTrue("a second run of the migrator is a no-op");

        // 4. Smoke tests: the data survived, N reports ready, N code reads and writes the upgraded data under RLS.
        await ExecuteFileAsync(dataSource, "smoke.sql");
        (await new SchemaVersionHealthCheck(dataSource).CheckHealthAsync(new HealthCheckContext(), Ct)).Status
            .Should().Be(HealthStatus.Healthy);

        var appConnectionString = await CoreSchemaDatabase.CreateLoginAsync(connectionString, "opportunity_app");
        await using var db = CoreSchemaDatabase.Over(connectionString, appConnectionString);

        var listed = await db.Reads.ListByControlNumberAsync(WorkspaceA, null, 100, Ct);
        listed.Select(d => d.ControlNumber).Should().Equal(Enumerable.Range(1, 20).Select(n => $"UPG{n:000000}"));
        (await db.Reads.ListByControlNumberAsync(WorkspaceB, null, 100, Ct)).Should().HaveCount(3);

        var catalog = await db.Fields.GetCatalogAsync(WorkspaceA, cancellationToken: Ct);
        catalog.Find(Responsiveness)!.Name.Should().Be("Responsiveness");
        catalog.Find(Issues)!.Name.Should().Be("Issues");

        var first = listed[0].DocumentId;
        var sixth = listed[5].DocumentId;
        var coding = (await db.Coding.GetCurrentAsync(WorkspaceA, [first, sixth], Ct)).ToDictionary(d => d.DocumentId);
        coding[first].DocumentVersion.Should().Be(2);
        coding[first].Fields.Select(f => f.FieldId).Should().BeEquivalentTo([Responsiveness, Issues, Comments]);
        coding[first].Fields.Single(f => f.FieldId == Comments).Value!.GetValue<string>().Should().Be("Reviewed before the upgrade");
        coding[sixth].Fields.Select(f => f.FieldId).Should().BeEquivalentTo([Responsiveness]);

        var write = await db.Coding.ApplyAsync(new CodingWriteRequest
        {
            WorkspaceId = WorkspaceA,
            IdempotencyKey = "upgrade-" + Guid.CreateVersion7().ToString("N"),
            Actor = new CodingActor(Reviewer, CodingActorType.Human),
            Documents = [new CodingTarget(sixth)],
            Operations =
            [
                CodingFieldOperation.AddChoices(Issues, Pricing),
                CodingFieldOperation.Set(Comments, JsonValue.Create("Reviewed after the upgrade")),
            ],
        }, Ct);
        write.Outcome.Should().Be(CodingWriteOutcome.Applied);
        write.Documents.Single().DocumentVersion.Should().Be(3, "the version continues from the N-1 data");

        var added = Document.Create(WorkspaceA, "UPG000021", caseSensitive: false);
        await db.Documents.InsertAsync(added, Ct);
        (await db.Reads.ListByControlNumberAsync(WorkspaceA, null, 100, Ct)).Should().HaveCount(21);

        // Workspace isolation still holds for the upgraded rows.
        await using var tx = await WorkspaceTransaction.BeginAsync(db.AppDataSource, WorkspaceB, Ct);
        await using var count = tx.Command("SELECT count(*) FROM opportunity.document");
        ((long)(await count.ExecuteScalarAsync(Ct))!).Should().Be(3, "workspace B sees only its own documents");
    }

    private static async Task ExecuteFileAsync(NpgsqlDataSource dataSource, string name)
    {
        await using var command = dataSource.CreateCommand(await File.ReadAllTextAsync(Path.Combine(UpgradeSource.Directory, name), Ct));
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Where the N-1 schema comes from: a release's scripts, a schema version, or the committed baseline.</summary>
    private sealed record UpgradeSource(string Label, int SchemaVersion, IReadOnlyList<MigrationScript> Scripts)
    {
        public static readonly string Directory = Path.Combine(RepositoryRoot(), "tools", "release", "upgrade");

        public static UpgradeSource Resolve()
        {
            var scriptsDirectory = Environment.GetEnvironmentVariable("OPPORTUNITY_UPGRADE_FROM_SCRIPTS");
            if (!string.IsNullOrWhiteSpace(scriptsDirectory))
            {
                var scripts = MigrationCatalog.Order(System.IO.Directory.EnumerateFiles(scriptsDirectory, "V*.sql")
                    .Select(path => MigrationScript.Create(Path.GetFileName(path), File.ReadAllText(path))));
                scripts.Should().NotBeEmpty("OPPORTUNITY_UPGRADE_FROM_SCRIPTS={0} holds the N-1 migrations", scriptsDirectory);
                var label = Environment.GetEnvironmentVariable("OPPORTUNITY_UPGRADE_FROM") ?? scriptsDirectory;
                return new UpgradeSource(label, scripts[^1].Version, scripts);
            }

            var baseline = File.ReadLines(Path.Combine(Directory, "baseline.env"))
                .Select(l => l.Split('=', 2))
                .Where(p => p.Length == 2 && !p[0].StartsWith('#'))
                .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.Ordinal);
            var schema = int.Parse(
                Environment.GetEnvironmentVariable("OPPORTUNITY_UPGRADE_FROM_SCHEMA") ?? baseline["UPGRADE_BASELINE_SCHEMA"],
                CultureInfo.InvariantCulture);
            schema.Should().BeLessThanOrEqualTo(MigrationCatalog.LatestVersion);
            return new UpgradeSource(
                $"schema {schema} ({baseline["UPGRADE_BASELINE_REF"]})",
                schema,
                [.. MigrationCatalog.Scripts.Where(s => s.Version <= schema)]);
        }

        private static string RepositoryRoot()
        {
            for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Opportunity.slnx")))
                {
                    return dir.FullName;
                }
            }

            throw new InvalidOperationException("Repository root (Opportunity.slnx) not found above " + AppContext.BaseDirectory);
        }
    }
}
