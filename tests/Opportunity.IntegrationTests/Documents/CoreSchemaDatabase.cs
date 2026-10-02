using Microsoft.EntityFrameworkCore;

using Npgsql;

using Opportunity.Core.Documents;
using Opportunity.Data;
using Opportunity.Data.Documents;
using Opportunity.Data.Migrations;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Documents;

/// <summary>A freshly migrated database with helpers for the core-schema tests.</summary>
internal sealed class CoreSchemaDatabase : IAsyncDisposable
{
    private CoreSchemaDatabase(string connectionString)
    {
        ConnectionString = connectionString;
        DataSource = NpgsqlDataSource.Create(connectionString);
        Documents = new DocumentRepository(DataSource);
        Reads = new DocumentReadQueries(DataSource);
    }

    public string ConnectionString { get; }

    public NpgsqlDataSource DataSource { get; }

    public DocumentRepository Documents { get; }

    public DocumentReadQueries Reads { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<CoreSchemaDatabase> CreateAsync(MigrationPostgresFixture postgres)
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await new PostgresMigrator(NpgsqlDataSource.Create(connectionString)).MigrateAsync(Ct);
        return new CoreSchemaDatabase(connectionString);
    }

    public OpportunityDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<OpportunityDbContext>().UseNpgsql(DataSource).Options);

    public async Task<Guid> CreateWorkspaceAsync(bool caseSensitive = false)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            "INSERT INTO opportunity.workspace (workspace_id, name, matter_number, display_time_zone, control_number_case_sensitive) " +
            "VALUES (@id, 'Matter ' || @id::text, 'M-1', 'America/New_York', @cs)",
            ("id", id), ("cs", caseSensitive));
        return id;
    }

    public async Task<Document> InsertDocumentAsync(Guid workspaceId, string controlNumber, Action<Document>? configure = null)
    {
        var document = Document.Create(workspaceId, controlNumber, caseSensitive: false);
        configure?.Invoke(document);
        await Documents.InsertAsync(document, Ct);
        return document;
    }

    /// <summary>Registers an object for a document under the workspace's key prefix and returns its id.</summary>
    public async Task<Guid> InsertStoredObjectAsync(Guid workspaceId, Guid? documentId, Guid? keyWorkspaceId = null)
    {
        var objectId = Guid.CreateVersion7();
        var key = $"ws/{keyWorkspaceId ?? workspaceId:N}/docs/{documentId ?? Guid.Empty:N}/native/{objectId:N}";
        await ExecuteAsync(
            """
            INSERT INTO opportunity.stored_object
                (workspace_id, object_id, logical_key, area, document_id, sha256, size_bytes, key_id, encryption_scheme, state)
            VALUES (@ws, @id, @key, 1, @doc, sha256('x'::bytea), 1, 'installation', 1, 1)
            """,
            ("ws", workspaceId), ("id", objectId), ("key", key), ("doc", (object?)documentId ?? DBNull.Value));
        return objectId;
    }

    public async Task<Guid> InsertPageSetAsync(Guid workspaceId, Guid documentId)
    {
        var pageSetId = Guid.CreateVersion7();
        await ExecuteAsync(
            "INSERT INTO opportunity.page_set (workspace_id, page_set_id, document_id, source, page_count, status) " +
            "VALUES (@ws, @id, @doc, 1, 1, 1)",
            ("ws", workspaceId), ("id", pageSetId), ("doc", documentId));
        return pageSetId;
    }

    public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = DataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = DataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    public async Task<List<string>> ColumnAsync(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var values = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    public ValueTask DisposeAsync() => DataSource.DisposeAsync();
}
