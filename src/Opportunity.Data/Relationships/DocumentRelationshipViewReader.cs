using Npgsql;

using Opportunity.Application.Documents;

namespace Opportunity.Data.Relationships;

/// <summary>
/// PostgreSQL read of a document's relationship groups for the "related items" view (E09-T05): the authoritative
/// <c>family_id</c>, <c>duplicate_group_id</c> and <c>email_thread_id</c> columns through their (workspace_id, key)
/// indexes, live documents only, in one transaction. It never authorizes; the service does.
/// </summary>
public sealed class DocumentRelationshipViewReader(NpgsqlDataSource dataSource) : IDocumentRelationshipViewReader
{
    private const string Columns =
        "d.document_id, d.control_number, d.file_name, d.document_date, d.family_id, d.family_sequence, d.parent_document_id, " +
        "d.duplicate_group_id, d.is_duplicate_primary, d.email_thread_id";

    private const string Live =
        "JOIN opportunity.document_projection_state ps ON ps.workspace_id = d.workspace_id AND ps.document_id = d.document_id AND NOT ps.is_deleted";

    public async Task<RelationshipNeighbourhood?> GetAsync(Guid workspaceId, Guid documentId, int memberLimit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(memberLimit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var anchors = await ReadAsync(tx, $"SELECT {Columns} FROM opportunity.document d {Live} WHERE d.workspace_id = @ws AND d.document_id = @id",
            cancellationToken, ("id", documentId)).ConfigureAwait(false);
        if (anchors.Count == 0)
        {
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var anchor = anchors[0];
        var family = await ReadAsync(tx,
            $"""
            SELECT {Columns} FROM opportunity.document d {Live}
            WHERE d.workspace_id = @ws AND d.family_id = @key
            ORDER BY d.family_sequence, d.control_number_sort_key, d.document_id
            LIMIT @limit
            """, cancellationToken, ("key", anchor.FamilyId), ("limit", memberLimit)).ConfigureAwait(false);
        List<RelationshipRow> duplicates = anchor.DuplicateGroupId is { } group
            ? await ReadAsync(tx,
                $"""
                SELECT {Columns} FROM opportunity.document d {Live}
                WHERE d.workspace_id = @ws AND d.duplicate_group_id = @key
                ORDER BY d.is_duplicate_primary DESC, d.control_number_sort_key, d.document_id
                LIMIT @limit
                """, cancellationToken, ("key", group), ("limit", memberLimit)).ConfigureAwait(false)
            : [];
        List<RelationshipRow> thread = anchor.EmailThreadId is { } threadId
            ? await ReadAsync(tx,
                $"""
                SELECT {Columns} FROM opportunity.document d {Live}
                WHERE d.workspace_id = @ws AND d.email_thread_id = @key
                ORDER BY d.document_date NULLS LAST, d.control_number_sort_key, d.document_id
                LIMIT @limit
                """, cancellationToken, ("key", threadId), ("limit", memberLimit + 1)).ConfigureAwait(false)
            : [];
        var truncated = thread.Count > memberLimit;
        if (truncated)
        {
            thread = [.. thread.Take(memberLimit)];
        }

        // Attachments of the other families that duplicates and thread members head, for their parent flags.
        var otherFamilies = duplicates.Concat(thread)
            .Where(r => r.IsTopLevel && r.FamilyId != anchor.FamilyId)
            .Select(r => r.FamilyId)
            .Distinct()
            .ToArray();
        var children = new List<(Guid, Guid)>();
        if (otherFamilies.Length > 0)
        {
            await using var command = tx.Command(
                $"""
                SELECT d.family_id, d.document_id FROM opportunity.document d {Live}
                WHERE d.workspace_id = @ws AND d.family_id = ANY(@families) AND d.family_sequence > 0
                ORDER BY d.family_id, d.family_sequence
                LIMIT @limit
                """);
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("families", otherFamilies);
            command.Parameters.AddWithValue("limit", memberLimit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                children.Add((reader.GetGuid(0), reader.GetGuid(1)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RelationshipNeighbourhood(anchor, family, duplicates, thread, truncated, children);
    }

    private static async Task<List<RelationshipRow>> ReadAsync(
        WorkspaceTransaction tx, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = tx.Command(sql);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var rows = new List<RelationshipRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new RelationshipRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetGuid(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetGuid(6),
                reader.IsDBNull(7) ? null : reader.GetGuid(7),
                reader.GetBoolean(8),
                reader.IsDBNull(9) ? null : reader.GetGuid(9)));
        }

        return rows;
    }
}
