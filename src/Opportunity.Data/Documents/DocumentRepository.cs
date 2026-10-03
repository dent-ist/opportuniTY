using Npgsql;

using Opportunity.Application.Documents;
using Opportunity.Core.Documents;

namespace Opportunity.Data.Documents;

/// <summary>
/// PostgreSQL implementation of <see cref="IDocumentRepository"/>. DocumentVersion lives in
/// <c>document_projection_state</c> and is bumped here, in the same transaction, whenever a projection input
/// (<see cref="DocumentColumns.ProjectedColumns"/>) actually changes value (ADR-001 §2, ADR-003 R17).
/// </summary>
public sealed class DocumentRepository(NpgsqlDataSource dataSource) : IDocumentRepository
{
    // NFC is applied here, not only in ControlNumber.Normalize: hosts run with InvariantGlobalization (no ICU), where
    // string.Normalize leaves non-ASCII text unchanged. PostgreSQL's normalize() always works on a UTF-8 database.
    private const string NormColumn = "control_number_norm";
    private const string NormalizedNorm = "normalize(" + NormColumn + ", NFC)";

    private static readonly string InsertDocumentSql =
        $"INSERT INTO opportunity.document ({DocumentColumns.NameList(DocumentColumns.All)}) " +
        $"VALUES ({DocumentColumns.ParameterList(DocumentColumns.All).Replace("@" + NormColumn, "normalize(@" + NormColumn + ", NFC)", StringComparison.Ordinal)}) " +
        "RETURNING control_number_norm, control_number_sort_key";

    private const string InsertStateSql =
        "INSERT INTO opportunity.document_projection_state (workspace_id, document_id) VALUES (@workspace_id, @document_id)";

    // Row comparison with IS DISTINCT FROM: NULL-safe, bytea by value, jsonb semantically (key order is irrelevant).
    private static readonly string LockAndCompareSql =
        $"""
        SELECT s.document_version, s.is_deleted,
               ({DocumentColumns.NameList(DocumentColumns.ProjectedColumns, "d")})
                   IS DISTINCT FROM ({DocumentColumns.ParameterList(DocumentColumns.ProjectedColumns)}) AS projected_changed,
               ({DocumentColumns.NameList(DocumentColumns.NonProjectedMutableColumns, "d")})
                   IS DISTINCT FROM ({DocumentColumns.ParameterList(DocumentColumns.NonProjectedMutableColumns)}) AS other_changed
        FROM opportunity.document d
        JOIN opportunity.document_projection_state s
          ON s.workspace_id = d.workspace_id AND s.document_id = d.document_id
        WHERE d.workspace_id = @workspace_id AND d.document_id = @document_id
        FOR UPDATE OF d, s
        """;

    private static readonly string UpdateDocumentSql =
        "UPDATE opportunity.document SET " +
        string.Join(", ", DocumentColumns.MutableColumns.Select(c => $"{c.Name} = @{c.Name}")) +
        ", updated_at = now() WHERE workspace_id = @workspace_id AND document_id = @document_id";

    private const string BumpVersionSql =
        """
        UPDATE opportunity.document_projection_state SET document_version = document_version + 1
        WHERE workspace_id = @workspace_id AND document_id = @document_id
        RETURNING document_version
        """;

    private const string StageTable = "document_stage";

    public async Task InsertAsync(Document document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, document.WorkspaceId, cancellationToken).ConfigureAwait(false);
        await using (var insert = tx.Command(InsertDocumentSql))
        {
            AddParameters(insert, document, DocumentColumns.All);
            await using var reader = await insert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            document.ControlNumberNorm = reader.GetString(0);
            document.ControlNumberSortKey = reader.GetString(1);
        }

        await using (var state = tx.Command(InsertStateSql))
        {
            AddKey(state, document.WorkspaceId, document.DocumentId);
            await state.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task InsertManyAsync(
        Guid workspaceId, IReadOnlyCollection<Document> documents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Any(d => d.WorkspaceId != workspaceId))
        {
            throw new ArgumentException("Every document must belong to the given workspace.", nameof(documents));
        }

        var columns = DocumentColumns.NameList(DocumentColumns.All);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);

        // RLS forbids COPY FROM into the target for a non-owner role, so stage in a temp table and INSERT ... SELECT,
        // which RLS checks (ADR-015 D7.4.2). The stage has the column types but none of the constraints.
        await using (var stage = tx.Command(
            $"CREATE TEMP TABLE {StageTable} ON COMMIT DROP AS SELECT {columns} FROM opportunity.document WITH NO DATA"))
        {
            await stage.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var importer = await tx.Connection.BeginBinaryImportAsync(
            $"COPY {StageTable} ({columns}) FROM STDIN (FORMAT BINARY)", cancellationToken).ConfigureAwait(false))
        {
            foreach (var document in documents)
            {
                await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                foreach (var column in DocumentColumns.All)
                {
                    var value = column.Get(document);
                    if (value is null)
                    {
                        await importer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await importer.WriteAsync(value, column.Type, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insert = tx.Command(
            $"""
            INSERT INTO opportunity.document ({columns})
                SELECT {columns.Replace(NormColumn, NormalizedNorm, StringComparison.Ordinal)} FROM {StageTable};
            INSERT INTO opportunity.document_projection_state (workspace_id, document_id)
                SELECT workspace_id, document_id FROM {StageTable};
            """))
        {
            insert.CommandTimeout = 0;
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Import-chunk variant of <see cref="InsertManyAsync"/> inside the caller's transaction: a document whose normalized
    /// control number already exists (a concurrent load) is skipped instead of failing the chunk. Returns the ids of the
    /// documents inserted, each at DocumentVersion 1.
    /// </summary>
    internal static async Task<HashSet<Guid>> InsertNewAsync(
        WorkspaceTransaction tx, IReadOnlyCollection<Document> documents, CancellationToken cancellationToken)
    {
        var inserted = new HashSet<Guid>();
        if (documents.Count == 0)
        {
            return inserted;
        }

        const string stage = "import_document_stage";
        var columns = DocumentColumns.NameList(DocumentColumns.All);
        await using (var create = tx.Command(
            $"CREATE TEMP TABLE {stage} ON COMMIT DROP AS SELECT {columns} FROM opportunity.document WITH NO DATA"))
        {
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var importer = await tx.Connection.BeginBinaryImportAsync(
            $"COPY {stage} ({columns}) FROM STDIN (FORMAT BINARY)", cancellationToken).ConfigureAwait(false))
        {
            foreach (var document in documents)
            {
                await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                foreach (var column in DocumentColumns.All)
                {
                    var value = column.Get(document);
                    if (value is null)
                    {
                        await importer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await importer.WriteAsync(value, column.Type, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insert = tx.Command(
            $"""
            WITH d AS (
                INSERT INTO opportunity.document ({columns})
                SELECT {columns.Replace(NormColumn, NormalizedNorm, StringComparison.Ordinal)} FROM {stage}
                ON CONFLICT (workspace_id, control_number_norm) DO NOTHING
                RETURNING workspace_id, document_id)
            INSERT INTO opportunity.document_projection_state (workspace_id, document_id)
            SELECT workspace_id, document_id FROM d
            RETURNING document_id
            """))
        {
            await using var reader = await insert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                inserted.Add(reader.GetGuid(0));
            }
        }

        await using (var drop = tx.Command($"DROP TABLE {stage}"))
        {
            await drop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return inserted;
    }

    public async Task<DocumentWriteResult> UpdateAsync(
        Document document, long? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, document.WorkspaceId, cancellationToken).ConfigureAwait(false);

        long currentVersion;
        bool projectedChanged;
        bool otherChanged;
        await using (var compare = tx.Command(LockAndCompareSql))
        {
            AddParameters(compare, document, [.. DocumentColumns.MutableColumns]);
            AddKey(compare, document.WorkspaceId, document.DocumentId);
            await using var reader = await compare.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetBoolean(1))
            {
                return new DocumentWriteResult(DocumentWriteOutcome.NotFound, null);
            }

            currentVersion = reader.GetInt64(0);
            projectedChanged = reader.GetBoolean(2);
            otherChanged = reader.GetBoolean(3);
        }

        if (expectedVersion is { } expected && expected != currentVersion)
        {
            return new DocumentWriteResult(DocumentWriteOutcome.VersionConflict, currentVersion);
        }

        if (!projectedChanged && !otherChanged)
        {
            return new DocumentWriteResult(DocumentWriteOutcome.Unchanged, currentVersion);
        }

        await using (var update = tx.Command(UpdateDocumentSql))
        {
            AddParameters(update, document, [.. DocumentColumns.MutableColumns]);
            AddKey(update, document.WorkspaceId, document.DocumentId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var newVersion = currentVersion;
        if (projectedChanged)
        {
            await using var bump = tx.Command(BumpVersionSql);
            AddKey(bump, document.WorkspaceId, document.DocumentId);
            newVersion = (long)(await bump.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new DocumentWriteResult(
            projectedChanged ? DocumentWriteOutcome.Updated : DocumentWriteOutcome.Unchanged, newVersion);
    }

    public async Task<long?> GetVersionAsync(Guid workspaceId, Guid documentId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            "SELECT document_version FROM opportunity.document_projection_state WHERE workspace_id = @workspace_id AND document_id = @document_id");
        AddKey(command, workspaceId, documentId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result is long version ? version : null;
    }

    private static void AddParameters(NpgsqlCommand command, Document document, IEnumerable<DocumentColumns.Column> columns)
    {
        foreach (var column in columns)
        {
            command.Parameters.Add(new NpgsqlParameter(column.Name, column.Type) { Value = column.Get(document) ?? DBNull.Value });
        }
    }

    private static void AddKey(NpgsqlCommand command, Guid workspaceId, Guid documentId)
    {
        if (!command.Parameters.Contains("workspace_id"))
        {
            command.Parameters.AddWithValue("workspace_id", workspaceId);
        }

        if (!command.Parameters.Contains("document_id"))
        {
            command.Parameters.AddWithValue("document_id", documentId);
        }
    }
}
