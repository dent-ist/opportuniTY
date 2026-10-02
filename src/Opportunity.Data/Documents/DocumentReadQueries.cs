using Dapper;

using Npgsql;

namespace Opportunity.Data.Documents;

/// <summary>Dapper read model: one row of a document list.</summary>
public sealed class DocumentListItem
{
    public Guid DocumentId { get; init; }

    public string ControlNumber { get; init; } = string.Empty;

    public string ControlNumberSortKey { get; init; } = string.Empty;

    public Guid FamilyId { get; init; }

    public int FamilySequence { get; init; }

    public string? FileName { get; init; }

    public long DocumentVersion { get; init; }
}

/// <summary>Keyset position in natural ControlNumber order.</summary>
public readonly record struct ControlNumberCursor(string SortKey, Guid DocumentId);

/// <summary>Read-side queries over the document core schema (Dapper).</summary>
public sealed class DocumentReadQueries(NpgsqlDataSource dataSource)
{
    private const string ListSql =
        """
        SELECT d.document_id AS DocumentId, d.control_number AS ControlNumber,
               d.control_number_sort_key AS ControlNumberSortKey, d.family_id AS FamilyId,
               d.family_sequence AS FamilySequence, d.file_name AS FileName,
               s.document_version AS DocumentVersion
        FROM opportunity.document d
        JOIN opportunity.document_projection_state s
          ON s.workspace_id = d.workspace_id AND s.document_id = d.document_id
        WHERE d.workspace_id = @WorkspaceId
          AND NOT s.is_deleted
          {0}
        ORDER BY d.control_number_sort_key, d.document_id
        LIMIT @Limit
        """;

    // Row comparison so PostgreSQL can seek the (workspace_id, control_number_sort_key, document_id) index.
    private const string AfterPredicate =
        "AND (d.control_number_sort_key, d.document_id) > (@AfterKey COLLATE \"C\", @AfterId)";

    private static readonly string FirstPageSql = ListSql.Replace("{0}", string.Empty, StringComparison.Ordinal);

    private static readonly string NextPageSql = ListSql.Replace("{0}", AfterPredicate, StringComparison.Ordinal);

    /// <summary>
    /// Documents in natural ControlNumber order (<c>ABC9</c> before <c>ABC10</c>), DocumentId as tie-breaker, using
    /// keyset pagination (ADR-009 R5, ADR-019 §2.8).
    /// </summary>
    public async Task<IReadOnlyList<DocumentListItem>> ListByControlNumberAsync(
        Guid workspaceId, ControlNumberCursor? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 500);

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var sql = after is null ? FirstPageSql : NextPageSql;
        var rows = await tx.Connection.QueryAsync<DocumentListItem>(new CommandDefinition(
            sql,
            new { WorkspaceId = workspaceId, AfterKey = after?.SortKey, AfterId = after?.DocumentId, Limit = limit },
            tx.Transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var list = rows.AsList();
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return list;
    }
}
