using Opportunity.Core.Documents;

namespace Opportunity.Data.Documents;

/// <summary>
/// The read-only folder tree of a workspace (#179, Q-55, V0068): imports file documents under the folders their Folder
/// Path names, creating any folder (and every folder above it) that does not exist yet, inside the chunk's transaction.
/// </summary>
internal static class DocumentFolderSql
{
    /// <summary>
    /// Ensures a folder for every path in <paramref name="paths"/> (folder names, outermost first) and returns each
    /// path's folder id by <see cref="FolderPath.Key"/>. One read for the existing folders, then one insert and one read
    /// per depth for the missing ones; a folder another transaction created meanwhile is kept (first spelling wins).
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, Guid>> EnsureAsync(
        WorkspaceTransaction tx, IEnumerable<IReadOnlyList<string>> paths, CancellationToken cancellationToken)
    {
        var wanted = new Dictionary<string, (string Path, string Name, int Depth, string? ParentKey)>(StringComparer.Ordinal);
        foreach (var segments in paths)
        {
            string? parentKey = null;
            var prefixes = FolderPath.Prefixes(segments);
            for (var i = 0; i < prefixes.Count; i++)
            {
                var key = FolderPath.Key(prefixes[i]);
                wanted.TryAdd(key, (prefixes[i], segments[i], i + 1, parentKey));
                parentKey = key;
            }
        }

        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            return ids;
        }

        await ReadAsync(tx, [.. wanted.Keys], ids, cancellationToken).ConfigureAwait(false);
        foreach (var level in wanted.Where(w => !ids.ContainsKey(w.Key)).GroupBy(w => w.Value.Depth).OrderBy(g => g.Key))
        {
            var missing = level.Where(w => !ids.ContainsKey(w.Key)).ToList();
            if (missing.Count == 0)
            {
                continue;
            }

            await using (var insert = tx.Command(
                """
                INSERT INTO opportunity.document_folder (workspace_id, folder_id, parent_folder_id, name, path, path_key, depth, ancestor_ids)
                SELECT @ws, u.id, nullif(u.parent, '00000000-0000-0000-0000-000000000000'::uuid), u.name, u.path, u.key, @depth,
                       coalesce(p.ancestor_ids, '{}'::uuid[]) || u.id
                FROM unnest(@ids, @parents, @names, @paths, @keys) AS u(id, parent, name, path, key)
                LEFT JOIN opportunity.document_folder p ON p.workspace_id = @ws AND p.folder_id = u.parent
                ON CONFLICT (workspace_id, path_key) DO NOTHING
                """))
            {
                insert.Parameters.AddWithValue("ws", tx.WorkspaceId);
                insert.Parameters.AddWithValue("depth", level.Key);
                insert.Parameters.AddWithValue("ids", missing.Select(_ => Guid.CreateVersion7()).ToArray());
                insert.Parameters.AddWithValue("parents", missing.Select(m => m.Value.ParentKey is { } p ? ids[p] : Guid.Empty).ToArray());
                insert.Parameters.AddWithValue("names", missing.Select(m => m.Value.Name).ToArray());
                insert.Parameters.AddWithValue("paths", missing.Select(m => m.Value.Path).ToArray());
                insert.Parameters.AddWithValue("keys", missing.Select(m => m.Key).ToArray());
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await ReadAsync(tx, [.. missing.Select(m => m.Key)], ids, cancellationToken).ConfigureAwait(false);
        }

        return ids;
    }

    private static async Task ReadAsync(WorkspaceTransaction tx, string[] keys, Dictionary<string, Guid> ids, CancellationToken cancellationToken)
    {
        await using var read = tx.Command(
            "SELECT path_key, folder_id FROM opportunity.document_folder WHERE workspace_id = @ws AND path_key = ANY(@keys)");
        read.Parameters.AddWithValue("ws", tx.WorkspaceId);
        read.Parameters.AddWithValue("keys", keys);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids[reader.GetString(0)] = reader.GetGuid(1);
        }
    }
}
