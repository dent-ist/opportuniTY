using Opportunity.Application.Coding;

namespace Opportunity.Data.Security;

/// <summary>
/// Writes <c>opportunity.document_restriction</c> (V0012) inside the caller's transaction: the coding store calls it so
/// that a document's classes change only together with the coding that drives them (ADR-015 D6.1, §24 rule 1).
/// </summary>
internal static class DocumentRestrictionSql
{
    /// <summary>
    /// Makes each document's classes within <paramref name="bound"/> equal to <paramref name="desired"/> (classes outside
    /// it are untouched). A derived class the workspace has not defined is ignored. Returns the changes made.
    /// </summary>
    public static async Task<IReadOnlyList<RestrictionClassChange>> SyncAsync(
        WorkspaceTransaction tx,
        Guid workspaceId,
        IReadOnlyDictionary<Guid, IReadOnlySet<string>> desired,
        IReadOnlySet<string> bound,
        CancellationToken cancellationToken)
    {
        var documentIds = desired.Keys.Order().ToArray();
        var boundKeys = bound.Order(StringComparer.Ordinal).ToArray();
        var current = new HashSet<(Guid DocumentId, string ClassKey)>();
        await using (var command = tx.Command(
            """
            SELECT document_id, class_key FROM opportunity.document_restriction
            WHERE workspace_id = @ws AND document_id = ANY(@docs) AND class_key = ANY(@bound)
            ORDER BY document_id, class_key
            FOR UPDATE
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("docs", documentIds);
            command.Parameters.AddWithValue("bound", boundKeys);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                current.Add((reader.GetGuid(0), reader.GetString(1)));
            }
        }

        var target = desired
            .SelectMany(d => d.Value.Where(bound.Contains).Select(c => (DocumentId: d.Key, ClassKey: c)))
            .ToHashSet();
        var removed = current.Except(target).OrderBy(r => r.DocumentId).ThenBy(r => r.ClassKey, StringComparer.Ordinal).ToList();
        var added = target.Except(current).OrderBy(r => r.DocumentId).ThenBy(r => r.ClassKey, StringComparer.Ordinal).ToList();
        var changes = new List<RestrictionClassChange>(removed.Count + added.Count);

        if (removed.Count > 0)
        {
            await using var command = tx.Command(
                """
                DELETE FROM opportunity.document_restriction r
                USING unnest(@docs::uuid[], @classes::text[]) AS u(document_id, class_key)
                WHERE r.workspace_id = @ws AND r.document_id = u.document_id AND r.class_key = u.class_key
                """);
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("docs", removed.Select(r => r.DocumentId).ToArray());
            command.Parameters.AddWithValue("classes", removed.Select(r => r.ClassKey).ToArray());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            changes.AddRange(removed.Select(r => new RestrictionClassChange(r.DocumentId, r.ClassKey, false)));
        }

        if (added.Count > 0)
        {
            await using var command = tx.Command(
                """
                INSERT INTO opportunity.document_restriction (workspace_id, document_id, class_key)
                SELECT @ws, u.document_id, u.class_key
                FROM unnest(@docs::uuid[], @classes::text[]) AS u(document_id, class_key)
                JOIN opportunity.restriction_class c ON c.workspace_id = @ws AND c.class_key = u.class_key
                ON CONFLICT DO NOTHING
                RETURNING document_id, class_key
                """);
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("docs", added.Select(r => r.DocumentId).ToArray());
            command.Parameters.AddWithValue("classes", added.Select(r => r.ClassKey).ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                changes.Add(new RestrictionClassChange(reader.GetGuid(0), reader.GetString(1), true));
            }
        }

        return changes;
    }
}
