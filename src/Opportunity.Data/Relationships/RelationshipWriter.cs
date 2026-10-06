using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Documents;
using Opportunity.Core.Documents;

namespace Opportunity.Data.Relationships;

/// <summary>
/// Records duplicate groups and email threads inside the caller's transaction (E09-T02): the import chunk that writes
/// documents calls <see cref="SyncAsync"/> after its document writes and before its search work, in the same
/// <see cref="WorkspaceTransaction"/>. The documents' group and thread foreign keys are checked at commit, so a chunk
/// that sets <c>DuplicateGroupId</c>/<c>EmailThreadId</c> without this call fails to commit.
/// </summary>
/// <remarks>
/// <para>Group and thread rows are locked in id order in one statement, so concurrent chunks serialize per group
/// without deadlocking each other, and each recompute sees the members committed before it.</para>
/// <para>Recomputing a group sets <c>member_count</c> (live members) and the ADR-009 R15 primary (earliest FamilyDate,
/// then lowest ControlNumberSortKey, then DocumentId); <c>IsDuplicatePrimary</c> is set on every member of the primary
/// family. Each document whose flag changes gets a new DocumentVersion; the ones outside
/// <see cref="RelationshipSync.CoveredDocumentIds"/> are returned so the caller adds search work for them.</para>
/// </remarks>
internal static class RelationshipWriter
{

    public static async Task<RelationshipSyncResult> SyncAsync(WorkspaceTransaction tx, RelationshipSync sync, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(sync);
        var groups = await WithPreviousGroupsAsync(tx, sync, cancellationToken).ConfigureAwait(false);
        var threads = await WithPreviousThreadsAsync(tx, sync, cancellationToken).ConfigureAwait(false);
        var groupIds = groups.Select(g => g.DuplicateGroupId).ToArray();
        var threadIds = threads.Select(t => t.EmailThreadId).ToArray();

        await UpsertGroupsAsync(tx, groups, cancellationToken).ConfigureAwait(false);
        await UpsertThreadsAsync(tx, threads, cancellationToken).ConfigureAwait(false);

        var changed = new List<(Guid DocumentId, long DocumentVersion)>();
        if (groupIds.Length > 0)
        {
            await RecomputeGroupsAsync(tx, groupIds, cancellationToken).ConfigureAwait(false);
            changed.AddRange(await UpdatePrimaryFlagsAsync(tx, groupIds, cancellationToken).ConfigureAwait(false));
        }

        // A covered document that left its group keeps no stale primary flag.
        changed.AddRange(await ClearPrimaryWithoutGroupAsync(tx, [.. sync.CoveredDocumentIds], cancellationToken).ConfigureAwait(false));
        if (threadIds.Length > 0)
        {
            await RecomputeThreadsAsync(tx, threadIds, cancellationToken).ConfigureAwait(false);
        }

        await DeleteUnreferencedAsync(tx, groupIds, threadIds, cancellationToken).ConfigureAwait(false);
        var covered = sync.CoveredDocumentIds as IReadOnlySet<Guid> ?? sync.CoveredDocumentIds.ToHashSet();
        return new RelationshipSyncResult(
            groupIds.Length, threadIds.Length, changed.Count, [.. changed.Where(c => !covered.Contains(c.DocumentId)).OrderBy(c => c.DocumentId)]);
    }

    /// <summary>Every group and thread of the workspace: recount, re-elect primaries, drop rows nothing references.</summary>
    public static async Task<RelationshipSyncResult> RecomputeAllAsync(WorkspaceTransaction tx, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tx);
        await using (var lockRows = tx.Command(
            """
            SELECT count(*) FROM (
                SELECT 1 FROM opportunity.duplicate_group WHERE workspace_id = @ws ORDER BY duplicate_group_id FOR UPDATE) g;
            SELECT count(*) FROM (
                SELECT 1 FROM opportunity.email_thread WHERE workspace_id = @ws ORDER BY email_thread_id FOR UPDATE) t;
            """))
        {
            lockRows.Parameters.AddWithValue("ws", tx.WorkspaceId);
            await lockRows.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var groups = await RecomputeGroupsAsync(tx, null, cancellationToken).ConfigureAwait(false);
        var changed = await UpdatePrimaryFlagsAsync(tx, null, cancellationToken).ConfigureAwait(false);
        changed.AddRange(await ClearPrimaryWithoutGroupAsync(tx, null, cancellationToken).ConfigureAwait(false));
        var threads = await RecomputeThreadsAsync(tx, null, cancellationToken).ConfigureAwait(false);
        await DeleteUnreferencedAsync(tx, null, null, cancellationToken).ConfigureAwait(false);
        return new RelationshipSyncResult(groups, threads, changed.Count, [.. changed.OrderBy(c => c.DocumentId)]);
    }

    /// <summary>
    /// Existing duplicate groups by id (computed grouping, E09-T04): locks them in id order, recounts, re-elects the
    /// primaries, updates the members' primary flags (version bumps returned) and removes the ones nothing references.
    /// </summary>
    public static async Task<List<(Guid DocumentId, long DocumentVersion)>> RecomputeDuplicateGroupsAsync(
        WorkspaceTransaction tx, Guid[] groupIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(groupIds);
        if (groupIds.Length == 0)
        {
            return [];
        }

        var ids = groupIds.Distinct().Order().ToArray();
        await using (var lockRows = tx.Command(
            """
            SELECT count(*) FROM (
                SELECT 1 FROM opportunity.duplicate_group
                WHERE workspace_id = @ws AND duplicate_group_id = ANY(@ids) ORDER BY duplicate_group_id FOR UPDATE) g
            """))
        {
            lockRows.Parameters.AddWithValue("ws", tx.WorkspaceId);
            lockRows.Parameters.AddWithValue("ids", ids);
            await lockRows.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await RecomputeGroupsAsync(tx, ids, cancellationToken).ConfigureAwait(false);
        var changed = await UpdatePrimaryFlagsAsync(tx, ids, cancellationToken).ConfigureAwait(false);
        await DeleteUnreferencedAsync(tx, ids, [], cancellationToken).ConfigureAwait(false);
        return changed;
    }

    // Previous groups of an overlay are recomputed too; their stored key lets them join the one ordered upsert.
    private static async Task<List<DuplicateGroupKey>> WithPreviousGroupsAsync(WorkspaceTransaction tx, RelationshipSync sync, CancellationToken cancellationToken)
    {
        var keys = new Dictionary<Guid, DuplicateGroupKey>();
        foreach (var group in sync.DuplicateGroups)
        {
            if (keys.TryGetValue(group.DuplicateGroupId, out var known) && known != group)
            {
                throw new ArgumentException($"Duplicate group {group.DuplicateGroupId} is given with two different keys.", nameof(sync));
            }

            keys[group.DuplicateGroupId] = group;
        }

        var previous = sync.PreviousDuplicateGroupIds?.Where(id => !keys.ContainsKey(id)).Distinct().ToArray() ?? [];
        if (previous.Length > 0)
        {
            await using var command = tx.Command(
                """
                SELECT duplicate_group_id, source, hash_kind, hash_value FROM opportunity.duplicate_group
                WHERE workspace_id = @ws AND duplicate_group_id = ANY(@ids)
                """);
            command.Parameters.AddWithValue("ws", tx.WorkspaceId);
            command.Parameters.AddWithValue("ids", previous);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var key = new DuplicateGroupKey(
                    reader.GetGuid(0), (DuplicateGroupSource)reader.GetInt16(1), (DuplicateHashKind)reader.GetInt16(2), reader.GetString(3));
                keys[key.DuplicateGroupId] = key;
            }
        }

        return [.. keys.Values.OrderBy(k => k.DuplicateGroupId)];
    }

    private static async Task<List<EmailThreadKey>> WithPreviousThreadsAsync(WorkspaceTransaction tx, RelationshipSync sync, CancellationToken cancellationToken)
    {
        var keys = new Dictionary<Guid, EmailThreadKey>();
        foreach (var thread in sync.EmailThreads)
        {
            if (keys.TryGetValue(thread.EmailThreadId, out var known) && known != thread)
            {
                throw new ArgumentException($"Email thread {thread.EmailThreadId} is given with two different keys.", nameof(sync));
            }

            keys[thread.EmailThreadId] = thread;
        }

        var previous = sync.PreviousEmailThreadIds?.Where(id => !keys.ContainsKey(id)).Distinct().ToArray() ?? [];
        if (previous.Length > 0)
        {
            await using var command = tx.Command(
                "SELECT email_thread_id, source, thread_key FROM opportunity.email_thread WHERE workspace_id = @ws AND email_thread_id = ANY(@ids)");
            command.Parameters.AddWithValue("ws", tx.WorkspaceId);
            command.Parameters.AddWithValue("ids", previous);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var key = new EmailThreadKey(reader.GetGuid(0), (EmailThreadSource)reader.GetInt16(1), reader.GetString(2));
                keys[key.EmailThreadId] = key;
            }
        }

        return [.. keys.Values.OrderBy(k => k.EmailThreadId)];
    }

    private static async Task UpsertGroupsAsync(WorkspaceTransaction tx, List<DuplicateGroupKey> groups, CancellationToken cancellationToken)
    {
        if (groups.Count == 0)
        {
            return;
        }

        // DO UPDATE (not DO NOTHING) so existing rows are locked too, all in id order.
        await using var command = tx.Command(
            """
            INSERT INTO opportunity.duplicate_group AS g (workspace_id, duplicate_group_id, source, hash_kind, hash_value)
            SELECT @ws, k.id, k.source, k.kind, k.value
            FROM unnest(@ids, @sources, @kinds, @values) AS k(id, source, kind, value)
            ORDER BY k.id
            ON CONFLICT (workspace_id, duplicate_group_id) DO UPDATE SET updated_at = now()
            RETURNING g.duplicate_group_id, g.source, g.hash_kind, g.hash_value
            """);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("ids", groups.Select(g => g.DuplicateGroupId).ToArray());
        command.Parameters.Add(new NpgsqlParameter("sources", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = groups.Select(g => (short)g.Source).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("kinds", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = groups.Select(g => (short)g.HashKind).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("values", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = groups.Select(g => g.HashValue).ToArray() });
        var expected = groups.ToDictionary(g => g.DuplicateGroupId);
        await EnsureSameKeysAsync(command, "duplicate group", r =>
            expected[r.GetGuid(0)] == new DuplicateGroupKey(r.GetGuid(0), (DuplicateGroupSource)r.GetInt16(1), (DuplicateHashKind)r.GetInt16(2), r.GetString(3)),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertThreadsAsync(WorkspaceTransaction tx, List<EmailThreadKey> threads, CancellationToken cancellationToken)
    {
        if (threads.Count == 0)
        {
            return;
        }

        await using var command = tx.Command(
            """
            INSERT INTO opportunity.email_thread AS t (workspace_id, email_thread_id, source, thread_key)
            SELECT @ws, k.id, k.source, k.thread_key
            FROM unnest(@ids, @sources, @keys) AS k(id, source, thread_key)
            ORDER BY k.id
            ON CONFLICT (workspace_id, email_thread_id) DO UPDATE SET updated_at = now()
            RETURNING t.email_thread_id, t.source, t.thread_key
            """);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("ids", threads.Select(t => t.EmailThreadId).ToArray());
        command.Parameters.Add(new NpgsqlParameter("sources", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = threads.Select(t => (short)t.Source).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = threads.Select(t => t.ThreadKey).ToArray() });
        var expected = threads.ToDictionary(t => t.EmailThreadId);
        await EnsureSameKeysAsync(command, "email thread", r =>
            expected[r.GetGuid(0)] == new EmailThreadKey(r.GetGuid(0), (EmailThreadSource)r.GetInt16(1), r.GetString(2)),
            cancellationToken).ConfigureAwait(false);
    }

    // Ids are UUIDv5 of the key, so a stored row with another key means a caller computed the id differently.
    private static async Task EnsureSameKeysAsync(
        NpgsqlCommand command, string what, Func<NpgsqlDataReader, bool> sameKey, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!sameKey(reader))
            {
                throw new InvalidOperationException($"A stored {what} has the same id but a different key; ids must come from RelationshipIds.");
            }
        }
    }

    private static async Task<int> RecomputeGroupsAsync(WorkspaceTransaction tx, Guid[]? ids, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            $"""
            WITH members AS (
                SELECT d.duplicate_group_id AS id, d.document_id, d.family_id, d.family_date, d.control_number_sort_key
                FROM opportunity.document d
                JOIN opportunity.document_projection_state s ON s.workspace_id = d.workspace_id AND s.document_id = d.document_id
                WHERE d.workspace_id = @ws AND {Scope("d.duplicate_group_id", ids)} AND NOT s.is_deleted),
            counts AS (SELECT id, count(*)::integer AS n FROM members GROUP BY id),
            primaries AS (
                SELECT DISTINCT ON (id) id, family_id, document_id FROM members
                ORDER BY id, family_date NULLS LAST, control_number_sort_key, document_id)
            UPDATE opportunity.duplicate_group g
            SET member_count = coalesce((SELECT c.n FROM counts c WHERE c.id = g.duplicate_group_id), 0),
                primary_family_id = (SELECT p.family_id FROM primaries p WHERE p.id = g.duplicate_group_id),
                primary_document_id = (SELECT p.document_id FROM primaries p WHERE p.id = g.duplicate_group_id),
                updated_at = now()
            WHERE g.workspace_id = @ws AND {Scope("g.duplicate_group_id", ids)}
            """);
        AddScope(command, tx, ids);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<(Guid DocumentId, long DocumentVersion)>> UpdatePrimaryFlagsAsync(WorkspaceTransaction tx, Guid[]? ids, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            $"""
            WITH target AS (
                SELECT d.document_id, coalesce(d.family_id = g.primary_family_id, false) AS flag
                FROM opportunity.document d
                JOIN opportunity.duplicate_group g ON g.workspace_id = d.workspace_id AND g.duplicate_group_id = d.duplicate_group_id
                WHERE d.workspace_id = @ws AND {Scope("d.duplicate_group_id", ids)}),
            changed AS (
                UPDATE opportunity.document d SET is_duplicate_primary = t.flag, updated_at = now()
                FROM target t
                WHERE d.workspace_id = @ws AND d.document_id = t.document_id AND d.is_duplicate_primary <> t.flag
                RETURNING d.document_id)
            {BumpVersions}
            """);
        AddScope(command, tx, ids);
        return await ReadVersionsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<(Guid DocumentId, long DocumentVersion)>> ClearPrimaryWithoutGroupAsync(WorkspaceTransaction tx, Guid[]? documentIds, CancellationToken cancellationToken)
    {
        if (documentIds is { Length: 0 })
        {
            return [];
        }

        await using var command = tx.Command(
            $"""
            WITH changed AS (
                UPDATE opportunity.document d SET is_duplicate_primary = false, updated_at = now()
                WHERE d.workspace_id = @ws AND d.duplicate_group_id IS NULL AND d.is_duplicate_primary
                  {(documentIds is null ? string.Empty : "AND d.document_id = ANY(@ids)")}
                RETURNING d.document_id)
            {BumpVersions}
            """);
        AddScope(command, tx, documentIds);
        return await ReadVersionsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private const string BumpVersions =
        """
        UPDATE opportunity.document_projection_state s SET document_version = s.document_version + 1
        FROM changed c
        WHERE s.workspace_id = @ws AND s.document_id = c.document_id
        RETURNING s.document_id, s.document_version
        """;

    private static async Task<int> RecomputeThreadsAsync(WorkspaceTransaction tx, Guid[]? ids, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            $"""
            WITH counts AS (
                SELECT d.email_thread_id AS id, count(*)::integer AS n
                FROM opportunity.document d
                JOIN opportunity.document_projection_state s ON s.workspace_id = d.workspace_id AND s.document_id = d.document_id
                WHERE d.workspace_id = @ws AND {Scope("d.email_thread_id", ids)} AND NOT s.is_deleted
                GROUP BY d.email_thread_id)
            UPDATE opportunity.email_thread t
            SET member_count = coalesce((SELECT c.n FROM counts c WHERE c.id = t.email_thread_id), 0), updated_at = now()
            WHERE t.workspace_id = @ws AND {Scope("t.email_thread_id", ids)}
            """);
        AddScope(command, tx, ids);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteUnreferencedAsync(WorkspaceTransaction tx, Guid[]? groupIds, Guid[]? threadIds, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            $"""
            DELETE FROM opportunity.duplicate_group g
            WHERE g.workspace_id = @ws {(groupIds is null ? string.Empty : "AND g.duplicate_group_id = ANY(@groups)")}
              AND NOT EXISTS (SELECT FROM opportunity.document d
                              WHERE d.workspace_id = @ws AND d.duplicate_group_id = g.duplicate_group_id);
            DELETE FROM opportunity.email_thread t
            WHERE t.workspace_id = @ws {(threadIds is null ? string.Empty : "AND t.email_thread_id = ANY(@threads)")}
              AND NOT EXISTS (SELECT FROM opportunity.document d
                              WHERE d.workspace_id = @ws AND d.email_thread_id = t.email_thread_id);
            """);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("groups", groupIds ?? []);
        command.Parameters.AddWithValue("threads", threadIds ?? []);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // A set of ids, or (null) every row that has one.
    private static string Scope(string column, Guid[]? ids) => ids is null ? column + " IS NOT NULL" : column + " = ANY(@ids)";

    private static void AddScope(NpgsqlCommand command, WorkspaceTransaction tx, Guid[]? ids)
    {
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("ids", ids ?? []);
    }

    private static async Task<List<(Guid DocumentId, long DocumentVersion)>> ReadVersionsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var rows = new List<(Guid DocumentId, long DocumentVersion)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add((reader.GetGuid(0), reader.GetInt64(1)));
        }

        return rows;
    }
}
