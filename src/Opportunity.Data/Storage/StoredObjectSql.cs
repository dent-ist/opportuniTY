using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Import;
using Opportunity.Application.Storage;

namespace Opportunity.Data.Storage;

/// <summary>
/// Registers objects in the <c>StoredObject</c> registry (ADR-011 §2.3–2.4) inside the transaction that writes the
/// domain rows referencing them. Content-addressed keys make it idempotent: a key already registered (an identical
/// re-load of the same document) keeps its row and id. Used by the import chunk for natives and text (E08-T04) and
/// page images (E08-T05).
/// </summary>
internal static class StoredObjectSql
{
    /// <summary>The object id of every given key, registering the ones not yet registered.</summary>
    /// <param name="createdByJobId">The job that uploaded the objects (<c>created_by_job_id</c>).</param>
    public static async Task<Dictionary<string, Guid>> RegisterAsync(
        WorkspaceTransaction tx, IReadOnlyCollection<ImportObject> objects, Guid? createdByJobId, CancellationToken cancellationToken)
    {
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (objects.Count == 0)
        {
            return ids;
        }

        var distinct = objects.DistinctBy(o => o.LogicalKey, StringComparer.Ordinal).ToList();
        foreach (var o in distinct)
        {
            var key = ObjectKey.Parse(o.LogicalKey);
            if (key.WorkspaceId != tx.WorkspaceId || key.DocumentId != o.DocumentId || key.ContentSha256 is not { } sha
                || !sha.ToBytes().AsSpan().SequenceEqual(o.Sha256))
            {
                throw new ArgumentException($"Object key {o.LogicalKey} does not belong to document {o.DocumentId} with the given SHA-256.", nameof(objects));
            }
        }

        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.stored_object
                (workspace_id, object_id, logical_key, area, document_id, sha256, size_bytes, content_type, key_id, encryption_scheme, state, created_by_job_id)
            SELECT @ws, u.object_id, u.logical_key, u.area, u.document_id, u.sha256, u.size_bytes, u.content_type, u.key_id, u.scheme, 1, @job
            FROM unnest(@object_ids, @keys, @areas, @documents, @shas, @sizes, @types, @key_ids, @schemes)
                AS u(object_id, logical_key, area, document_id, sha256, size_bytes, content_type, key_id, scheme)
            ON CONFLICT (workspace_id, logical_key) DO NOTHING
            """))
        {
            insert.Parameters.AddWithValue("ws", tx.WorkspaceId);
            insert.Parameters.Add(new NpgsqlParameter("job", NpgsqlDbType.Uuid) { Value = (object?)createdByJobId ?? DBNull.Value });
            insert.Parameters.AddWithValue("object_ids", distinct.Select(_ => Guid.CreateVersion7()).ToArray());
            insert.Parameters.AddWithValue("keys", distinct.Select(o => o.LogicalKey).ToArray());
            insert.Parameters.AddWithValue("areas", distinct.Select(o => (short)o.Area).ToArray());
            insert.Parameters.AddWithValue("documents", distinct.Select(o => o.DocumentId).ToArray());
            insert.Parameters.AddWithValue("shas", distinct.Select(o => o.Sha256).ToArray());
            insert.Parameters.AddWithValue("sizes", distinct.Select(o => o.SizeBytes).ToArray());
            insert.Parameters.AddWithValue("types", distinct.Select(o => o.ContentType).ToArray());
            insert.Parameters.AddWithValue("key_ids", distinct.Select(o => o.KeyId).ToArray());
            insert.Parameters.AddWithValue("schemes", distinct.Select(o => (short)o.EncryptionScheme).ToArray());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var select = tx.Command(
            "SELECT logical_key, object_id FROM opportunity.stored_object WHERE workspace_id = @ws AND logical_key = ANY(@keys)"))
        {
            select.Parameters.AddWithValue("ws", tx.WorkspaceId);
            select.Parameters.AddWithValue("keys", distinct.Select(o => o.LogicalKey).ToArray());
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids[reader.GetString(0)] = reader.GetGuid(1);
            }
        }

        return ids;
    }

    /// <summary>
    /// Removes registry rows of documents the transaction ended up not creating (the deferred document foreign key
    /// would refuse the commit). Their objects become orphans for the reconciler (ADR-011 §2.4).
    /// </summary>
    public static async Task UnregisterDocumentsAsync(WorkspaceTransaction tx, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken)
    {
        if (documentIds.Count == 0)
        {
            return;
        }

        await using var delete = tx.Command("DELETE FROM opportunity.stored_object WHERE workspace_id = @ws AND document_id = ANY(@ids)");
        delete.Parameters.AddWithValue("ws", tx.WorkspaceId);
        delete.Parameters.AddWithValue("ids", documentIds.ToArray());
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
