using Microsoft.Extensions.Configuration;

using Npgsql;

using Opportunity.Application.Audit;
using Opportunity.Application.Audit.Chain;
using Opportunity.Data.Audit;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Security.Keys;

namespace Opportunity.IntegrationTests.Audit;

/// <summary>
/// One migrated database with a sealer login (<c>opportunity_audit_sealer</c>), a local signing key store in a temp
/// directory (deleted on dispose), the sealer, the verifier and the app-role audit writer.
/// </summary>
internal sealed class AuditChainHarness : IAsyncDisposable
{
    private readonly List<NpgsqlDataSource> _sources = [];

    private AuditChainHarness(CoreSchemaDatabase db, string sealerConnectionString, string keyDirectory, AuditChainOptions? options)
    {
        Db = db;
        SealerConnectionString = sealerConnectionString;
        KeyDirectory = keyDirectory;
        SealerSource = NpgsqlDataSource.Create(sealerConnectionString);
        Signer = new LocalSigningKeyProvider(new LocalKeyStoreOptions { KeyDirectory = keyDirectory }, new EnvironmentSecretProvider(_ => null));
        Writer = new PostgresAuditEventWriter(db.AppDataSource);
        Sealer = NewSealer(options);
        Verifier = new PostgresAuditChainVerifier(new AuditChainDataSource(SealerSource));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public CoreSchemaDatabase Db { get; }

    public string SealerConnectionString { get; }

    public string KeyDirectory { get; }

    public NpgsqlDataSource SealerSource { get; }

    public LocalSigningKeyProvider Signer { get; }

    public PostgresAuditEventWriter Writer { get; }

    public PostgresAuditChainSealer Sealer { get; }

    public PostgresAuditChainVerifier Verifier { get; }

    public static async Task<AuditChainHarness> CreateAsync(MigrationPostgresFixture postgres, AuditChainOptions? options = null) =>
        await OverAsync(await CoreSchemaDatabase.CreateAsync(postgres), options);

    /// <summary>A harness over an existing database (the database is disposed with the harness).</summary>
    public static async Task<AuditChainHarness> OverAsync(CoreSchemaDatabase db, AuditChainOptions? options = null)
    {
        var sealer = await CoreSchemaDatabase.CreateLoginAsync(db.ConnectionString, "opportunity_audit_sealer");
        return new AuditChainHarness(db, sealer, Directory.CreateTempSubdirectory("opportunity-audit-keys-").FullName, options);
    }

    /// <summary>A second, independent sealer (its own pool), as another dispatcher replica would run.</summary>
    public PostgresAuditChainSealer NewSealer(AuditChainOptions? options = null)
    {
        var source = NpgsqlDataSource.Create(SealerConnectionString);
        _sources.Add(source);
        return new(new AuditChainDataSource(source), Signer, Writer, TimeProvider.System, options);
    }

    public IAuditCheckpointKeys ProviderKeys => new SigningProviderCheckpointKeys(Signer);

    public Task<AuditChainVerification> VerifyAsync(Guid? workspaceId) =>
        Verifier.VerifyAsync(AuditChainFormat.ChainIdOf(workspaceId), ProviderKeys, Ct);

    /// <summary>Writes <paramref name="count"/> events to one chain through the app role, one transaction each.</summary>
    public async Task<List<AuditEvent>> WriteAsync(Guid? workspaceId, int count)
    {
        var written = new List<AuditEvent>(count);
        for (var i = 0; i < count; i++)
        {
            var e = AuditSamples.Event(workspaceId) with { Details = new Dictionary<string, string?> { ["N"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture) } };
            await Writer.WriteAsync(e, Ct);
            written.Add(e);
        }

        return written;
    }

    /// <summary>
    /// Runs SQL as a superuser with triggers disabled (<c>session_replication_role = replica</c>): the attacker of the
    /// tamper tests, a database administrator editing rows directly.
    /// </summary>
    public async Task TamperAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Db.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SET session_replication_role = replica; " + sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    public IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:App"] = Db.AppConnectionString,
            ["ConnectionStrings:AuditSealer"] = SealerConnectionString,
            ["KeyManagement:Local:KeyDirectory"] = KeyDirectory,
        })
        .Build();

    public async ValueTask DisposeAsync()
    {
        await SealerSource.DisposeAsync();
        foreach (var source in _sources)
        {
            await source.DisposeAsync();
        }

        await Db.DisposeAsync();
        Directory.Delete(KeyDirectory, recursive: true);
    }
}

/// <summary>For tests of the audit purge: what the retention job does first (ADR-013 §3.4).</summary>
internal static class AuditChainTestSupport
{
    /// <summary>Seals every chain and takes BeforePurge checkpoints with a throwaway sealer login and key store.</summary>
    public static async Task SealAndCheckpointAsync(CoreSchemaDatabase db)
    {
        var login = await CoreSchemaDatabase.CreateLoginAsync(db.ConnectionString, "opportunity_audit_sealer");
        var keys = Directory.CreateTempSubdirectory("opportunity-audit-keys-").FullName;
        try
        {
            await using var source = NpgsqlDataSource.Create(login);
            var sealer = new PostgresAuditChainSealer(
                new AuditChainDataSource(source),
                new LocalSigningKeyProvider(new LocalKeyStoreOptions { KeyDirectory = keys }, new EnvironmentSecretProvider(_ => null)));
            await sealer.CheckpointAsync(AuditCheckpointReason.BeforePurge, cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.Delete(keys, recursive: true);
        }
    }
}
