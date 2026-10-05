using Npgsql;

using Opportunity.Core.Documents;
using Opportunity.Data;
using Opportunity.Data.Coding;
using Opportunity.Data.Documents;
using Opportunity.Data.Fields;
using Opportunity.Data.Migrations;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Documents;

/// <summary>
/// A freshly migrated database with helpers for the core-schema tests. Repositories run as a login in
/// <c>opportunity_app</c>, so row-level security (V0005) applies to them exactly as in production; the fixture
/// helpers (<see cref="DataSource"/>) run as the container superuser, which bypasses RLS, to arrange and inspect data.
/// </summary>
internal sealed class CoreSchemaDatabase : IAsyncDisposable
{
    private CoreSchemaDatabase(string connectionString, string appConnectionString)
    {
        ConnectionString = connectionString;
        AppConnectionString = appConnectionString;
        DataSource = NpgsqlDataSource.Create(connectionString);
        AppDataSource = NpgsqlDataSource.Create(appConnectionString);
        Documents = new DocumentRepository(AppDataSource);
        Reads = new DocumentReadQueries(AppDataSource);
        Fields = new FieldCatalogRepository(AppDataSource);
        Coding = new CodingRepository(AppDataSource);
    }

    public string ConnectionString { get; }

    /// <summary>A login that is a member of <c>opportunity_app</c> (NOBYPASSRLS, owns nothing).</summary>
    public string AppConnectionString { get; }

    /// <summary>Superuser: bypasses RLS. For arranging and asserting only.</summary>
    public NpgsqlDataSource DataSource { get; }

    public NpgsqlDataSource AppDataSource { get; }

    public DocumentRepository Documents { get; }

    public DocumentReadQueries Reads { get; }

    public FieldCatalogRepository Fields { get; }

    public CodingRepository Coding { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Over an existing migrated database (owned by the caller), e.g. one reached through fault proxies.</summary>
    public static CoreSchemaDatabase Over(string connectionString, string appConnectionString) => new(connectionString, appConnectionString);

    public static async Task<CoreSchemaDatabase> CreateAsync(MigrationPostgresFixture postgres)
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using (var migratorSource = NpgsqlDataSource.Create(connectionString))
        {
            await new PostgresMigrator(migratorSource).MigrateAsync(Ct);
        }

        var appConnectionString = await CreateLoginAsync(connectionString, "opportunity_app");
        return new CoreSchemaDatabase(connectionString, appConnectionString);
    }

    /// <summary>Creates a fresh LOGIN role (in <paramref name="groupRole"/>, if given) and returns its connection string.</summary>
    public static async Task<string> CreateLoginAsync(string connectionString, string? groupRole, string options = "")
    {
        var login = "login_" + Guid.NewGuid().ToString("N")[..12];
        const string password = "test-only-password";
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(
                $"CREATE ROLE {login} LOGIN PASSWORD '{password}' {options} {(groupRole is null ? "" : "IN ROLE " + groupRole)}", connection);
            await command.ExecuteNonQueryAsync(Ct);
        }

        return new NpgsqlConnectionStringBuilder(connectionString) { Username = login, Password = password }.ConnectionString;
    }

    /// <summary>Runs <paramref name="work"/> in a workspace-bound transaction of the app login and commits it.</summary>
    public async Task InWorkspaceAsync(Guid workspaceId, Func<WorkspaceTransaction, Task> work)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(AppDataSource, workspaceId, Ct);
        await work(tx);
        await tx.CommitAsync(Ct);
    }

    /// <summary>An EF Core context enlisted in a workspace-bound transaction of the app login; commits on success.</summary>
    public Task InDbContextAsync(Guid workspaceId, Func<OpportunityDbContext, Task> work) =>
        InWorkspaceAsync(workspaceId, async tx =>
        {
            await using var context = tx.CreateDbContext();
            await work(context);
        });

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

    public async ValueTask DisposeAsync()
    {
        await AppDataSource.DisposeAsync();
        await DataSource.DisposeAsync();

        // Tests also open raw NpgsqlConnections; their global pools are keyed by these per-test strings.
        using (var app = new NpgsqlConnection(AppConnectionString))
        {
            NpgsqlConnection.ClearPool(app);
        }

        using var admin = new NpgsqlConnection(ConnectionString);
        NpgsqlConnection.ClearPool(admin);
    }
}
