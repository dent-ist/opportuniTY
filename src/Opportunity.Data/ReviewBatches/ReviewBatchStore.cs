using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Authorization;
using Opportunity.Application.ReviewBatches;
using Opportunity.Core.ReviewBatches;
using Opportunity.Data.Audit;

namespace Opportunity.Data.ReviewBatches;

/// <summary>
/// PostgreSQL <see cref="IReviewBatchStore"/> over V0048 (<c>review_batch_set</c>, <c>review_batch</c>,
/// <c>review_batch_document</c>, <c>review_batch_checkout</c>; forced RLS). A Batch Set is cut in one transaction by
/// streaming the snapshot's members through a server-side cursor (bounded memory: one open batch and one group), and
/// every status change locks the batch, checks its version and writes its audit event in the same transaction.
/// </summary>
public sealed class ReviewBatchStore(NpgsqlDataSource dataSource) : IReviewBatchStore
{
    /// <summary>Snapshot members read per cursor fetch, and membership rows written per insert.</summary>
    internal const int StreamPage = 5_000;

    private const string SetSelect =
        """
        SELECT s.batch_set_id, s.name, s.batch_prefix, s.max_batch_size, s.keep_families_together, s.keep_threads_together,
               s.review_pass, s.qc_of_batch_set_id, s.reviewer_group, s.snapshot_id, s.batch_count, s.document_count,
               s.created_by, s.created_at, c.available, c.checked_out, c.completed
          FROM opportunity.review_batch_set s
          CROSS JOIN LATERAL (
              SELECT count(*) FILTER (WHERE b.status = 1)::integer AS available,
                     count(*) FILTER (WHERE b.status = 2)::integer AS checked_out,
                     count(*) FILTER (WHERE b.status = 3)::integer AS completed
                FROM opportunity.review_batch b
               WHERE b.workspace_id = s.workspace_id AND b.batch_set_id = s.batch_set_id) c
        """;

    private const string BatchSelect =
        """
        SELECT b.batch_id, b.batch_set_id, s.name, b.ordinal, b.name, b.document_count, b.status, b.assignee_id,
               b.status_changed_at, b.status_changed_by, b.version, s.review_pass, s.reviewer_group
          FROM opportunity.review_batch b
          JOIN opportunity.review_batch_set s ON s.workspace_id = b.workspace_id AND s.batch_set_id = b.batch_set_id
        """;

    public async Task<ReviewBatchSetCreation> CreateSetAsync(NewReviewBatchSet batchSet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batchSet);
        try
        {
            return await CreateSetCoreAsync(batchSet, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == "review_batch_set_prefix_uq")
        {
            return new ReviewBatchSetCreation(ReviewBatchSetCreateStatus.PrefixTaken);
        }
    }

    private async Task<ReviewBatchSetCreation> CreateSetCoreAsync(NewReviewBatchSet set, CancellationToken cancellationToken)
    {
        var ws = set.WorkspaceId;
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, ws, cancellationToken).ConfigureAwait(false);
        await using (var snapshot = tx.Command(
            """
            SELECT 1 FROM opportunity.document_set_snapshot
             WHERE workspace_id = @ws AND snapshot_id = @id AND status = 'Ready' AND purpose = 'ReviewBatch'
               FOR SHARE
            """))
        {
            snapshot.Parameters.AddWithValue("ws", ws);
            snapshot.Parameters.AddWithValue("id", set.SnapshotId);
            if (await snapshot.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                return new ReviewBatchSetCreation(ReviewBatchSetCreateStatus.SnapshotNotReady);
            }
        }

        if (set.QcOfBatchSetId is { } source)
        {
            await using var qc = tx.Command(
                "SELECT 1 FROM opportunity.review_batch_set WHERE workspace_id = @ws AND batch_set_id = @id AND review_pass = 1");
            qc.Parameters.AddWithValue("ws", ws);
            qc.Parameters.AddWithValue("id", source);
            if (await qc.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                return new ReviewBatchSetCreation(ReviewBatchSetCreateStatus.QcSourceNotFound);
            }
        }

        await using (var prefix = tx.Command(
            "SELECT 1 FROM opportunity.review_batch_set WHERE workspace_id = @ws AND lower(batch_prefix) = lower(@prefix)"))
        {
            prefix.Parameters.AddWithValue("ws", ws);
            prefix.Parameters.AddWithValue("prefix", set.BatchPrefix);
            if (await prefix.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                return new ReviewBatchSetCreation(ReviewBatchSetCreateStatus.PrefixTaken);
            }
        }

        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.review_batch_set
                (workspace_id, batch_set_id, name, batch_prefix, max_batch_size, keep_families_together, keep_threads_together,
                 review_pass, qc_of_batch_set_id, reviewer_group, snapshot_id, batch_count, document_count, created_by)
            VALUES (@ws, @id, @name, @prefix, @size, @families, @threads, @pass, @qc, @group, @snapshot, 0, 0, @by)
            """))
        {
            insert.Parameters.AddWithValue("ws", ws);
            insert.Parameters.AddWithValue("id", set.BatchSetId);
            insert.Parameters.AddWithValue("name", set.Name);
            insert.Parameters.AddWithValue("prefix", set.BatchPrefix);
            insert.Parameters.AddWithValue("size", set.MaxBatchSize);
            insert.Parameters.AddWithValue("families", set.KeepFamiliesTogether);
            insert.Parameters.AddWithValue("threads", set.KeepThreadsTogether);
            insert.Parameters.AddWithValue("pass", (short)set.Pass);
            insert.Parameters.Add(new NpgsqlParameter("qc", NpgsqlDbType.Uuid) { Value = (object?)set.QcOfBatchSetId ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("group", NpgsqlDbType.Text) { Value = (object?)set.ReviewerGroup ?? DBNull.Value });
            insert.Parameters.AddWithValue("snapshot", set.SnapshotId);
            insert.Parameters.AddWithValue("by", set.CreatedBy);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var (batchCount, documentCount) = await CutAsync(tx, set, cancellationToken).ConfigureAwait(false);
        await using (var counts = tx.Command(
            "UPDATE opportunity.review_batch_set SET batch_count = @batches, document_count = @documents WHERE workspace_id = @ws AND batch_set_id = @id"))
        {
            counts.Parameters.AddWithValue("ws", ws);
            counts.Parameters.AddWithValue("id", set.BatchSetId);
            counts.Parameters.AddWithValue("batches", batchCount);
            counts.Parameters.AddWithValue("documents", documentCount);
            await counts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var stored = (await ReadSetsAsync(tx, $"{SetSelect} WHERE s.workspace_id = @ws AND s.batch_set_id = @id",
            p => p.AddWithValue("id", set.BatchSetId), cancellationToken).ConfigureAwait(false)).Single();
        await AuditSql.InsertAsync(tx, set.Audit(stored), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ReviewBatchSetCreation(ReviewBatchSetCreateStatus.Created, stored);
    }

    /// <summary>
    /// Streams the snapshot's members in review order with their keep-together group (the document; its family; or the
    /// email thread of its family, falling back to the family), groups ordered by their first member, and packs them.
    /// </summary>
    private static async Task<(int Batches, long Documents)> CutAsync(WorkspaceTransaction tx, NewReviewBatchSet set, CancellationToken cancellationToken)
    {
        await using (var declare = tx.Command(
            """
            DECLARE review_batch_members NO SCROLL CURSOR FOR
            WITH m AS (
                SELECT p.first_ordinal + u.i - 1 AS ordinal, u.document_id
                  FROM opportunity.document_set_snapshot_page p
                  CROSS JOIN LATERAL unnest(p.document_ids) WITH ORDINALITY AS u(document_id, i)
                 WHERE p.workspace_id = @ws AND p.snapshot_id = @snapshot
            ), g AS (
                SELECT m.ordinal, m.document_id,
                       CASE WHEN @threads THEN coalesce(
                                first_value(d.email_thread_id) OVER (PARTITION BY d.family_id ORDER BY d.email_thread_id IS NULL, m.ordinal),
                                d.family_id)
                            WHEN @families THEN d.family_id
                            ELSE m.document_id END AS group_key
                  FROM m
                  JOIN opportunity.document d ON d.workspace_id = @ws AND d.document_id = m.document_id
            )
            SELECT document_id, group_key FROM (
                SELECT g.document_id, g.group_key, g.ordinal, min(g.ordinal) OVER (PARTITION BY g.group_key) AS group_first FROM g) x
             ORDER BY group_first, ordinal
            """))
        {
            declare.Parameters.AddWithValue("ws", set.WorkspaceId);
            declare.Parameters.AddWithValue("snapshot", set.SnapshotId);
            declare.Parameters.AddWithValue("threads", set.KeepThreadsTogether);
            declare.Parameters.AddWithValue("families", set.KeepFamiliesTogether);
            await declare.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var cutter = new BatchCutter(set);
        var group = new List<Guid>();
        Guid? groupKey = null;
        while (true)
        {
            var rows = new List<(Guid DocumentId, Guid GroupKey)>(StreamPage);
            await using (var fetch = tx.Command($"FETCH {StreamPage} FROM review_batch_members"))
            await using (var reader = await fetch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add((reader.GetGuid(0), reader.GetGuid(1)));
                }
            }

            if (rows.Count == 0)
            {
                break;
            }

            foreach (var (documentId, key) in rows)
            {
                if (groupKey != key && group.Count > 0)
                {
                    cutter.Place(group);
                    group.Clear();
                }

                groupKey = key;
                group.Add(documentId);
            }

            await cutter.FlushAsync(tx, final: false, cancellationToken).ConfigureAwait(false);
        }

        if (group.Count > 0)
        {
            cutter.Place(group);
        }

        await cutter.FlushAsync(tx, final: true, cancellationToken).ConfigureAwait(false);
        await using (var close = tx.Command("CLOSE review_batch_members"))
        {
            await close.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return (cutter.Batches, cutter.Documents);
    }

    public async Task<ReviewBatchSetRecord?> GetSetAsync(Guid workspaceId, Guid batchSetId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var sets = await ReadSetsAsync(tx, $"{SetSelect} WHERE s.workspace_id = @ws AND s.batch_set_id = @id",
            p => p.AddWithValue("id", batchSetId), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return sets.SingleOrDefault();
    }

    public async Task<IReadOnlyList<ReviewBatchSetRecord>> ListSetsAsync(Guid workspaceId, Guid? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var sets = await ReadSetsAsync(tx,
            $"{SetSelect} WHERE s.workspace_id = @ws AND (@after::uuid IS NULL OR s.batch_set_id < @after) ORDER BY s.batch_set_id DESC LIMIT @limit",
            p =>
            {
                p.Add(new NpgsqlParameter("after", NpgsqlDbType.Uuid) { Value = (object?)after ?? DBNull.Value });
                p.AddWithValue("limit", limit);
            }, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return sets;
    }

    public async Task<ReviewBatchRecord?> GetBatchAsync(Guid workspaceId, Guid batchId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var batch = await ReadBatchAsync(tx, batchId, forUpdate: false, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return batch;
    }

    public async Task<IReadOnlyList<ReviewBatchRecord>> ListBatchesAsync(
        Guid workspaceId, ReviewBatchFilter filter, ReviewBatchCursor? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            {BatchSelect}
             WHERE b.workspace_id = @ws
               AND (@set::uuid IS NULL OR b.batch_set_id = @set)
               AND (@status::smallint IS NULL OR b.status = @status)
               AND (@assignee::uuid IS NULL OR b.assignee_id = @assignee)
               AND (@after_set::uuid IS NULL OR (b.batch_set_id, b.ordinal) > (@after_set, @after_ordinal))
             ORDER BY b.batch_set_id, b.ordinal
             LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.Add(new NpgsqlParameter("set", NpgsqlDbType.Uuid) { Value = (object?)filter.BatchSetId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Smallint) { Value = filter.Status is { } s ? (short)s : DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("assignee", NpgsqlDbType.Uuid) { Value = (object?)filter.AssigneeId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("after_set", NpgsqlDbType.Uuid) { Value = after is { } a ? a.BatchSetId : DBNull.Value });
        command.Parameters.AddWithValue("after_ordinal", after?.Ordinal ?? 0);
        command.Parameters.AddWithValue("limit", limit);
        var batches = new List<ReviewBatchRecord>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                batches.Add(ReadBatch(reader));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return batches;
    }

    public async Task<IReadOnlyDictionary<Guid, long>> CountVisibleAsync(
        Guid workspaceId, IReadOnlyCollection<Guid> ids, bool bySet, VisibilityFilter visibility, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(visibility);
        var counts = ids.Distinct().ToDictionary(id => id, _ => 0L);
        if (counts.Count == 0)
        {
            return counts;
        }

        var key = bySet ? "batch_set_id" : "batch_id";
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var command = tx.Command(
            $"""
            SELECT bd.{key}, count(*)
              FROM opportunity.review_batch_document bd
              LEFT JOIN opportunity.document_projection_state s ON s.workspace_id = bd.workspace_id AND s.document_id = bd.document_id
             WHERE bd.workspace_id = @ws AND bd.{key} = ANY (@ids) AND s.is_deleted IS NOT TRUE
               AND NOT EXISTS (SELECT 1 FROM opportunity.document_restriction r
                                WHERE r.workspace_id = bd.workspace_id AND r.document_id = bd.document_id AND r.class_key = ANY (@denied))
               AND NOT EXISTS (SELECT 1 FROM opportunity.document_wall w
                                WHERE w.workspace_id = bd.workspace_id AND w.document_id = bd.document_id AND w.wall_id = ANY (@walls))
             GROUP BY bd.{key}
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("ids", counts.Keys.ToArray());
            command.Parameters.AddWithValue("denied", visibility.DeniedClasses.ToArray());
            command.Parameters.AddWithValue("walls", visibility.WallIds.ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                counts[reader.GetGuid(0)] = reader.GetInt64(1);
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return counts;
    }

    public async Task<IReadOnlyList<ReviewBatchMember>> ListMembersAsync(
        Guid workspaceId, Guid batchId, int afterPosition, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var members = new List<ReviewBatchMember>();
        await using (var command = tx.Command(
            """
            SELECT bd.document_id, d.control_number, bd.position
              FROM opportunity.review_batch_document bd
              JOIN opportunity.document d ON d.workspace_id = bd.workspace_id AND d.document_id = bd.document_id
             WHERE bd.workspace_id = @ws AND bd.batch_id = @batch AND bd.position > @after
             ORDER BY bd.position
             LIMIT @limit
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("batch", batchId);
            command.Parameters.AddWithValue("after", afterPosition);
            command.Parameters.AddWithValue("limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                members.Add(new ReviewBatchMember(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return members;
    }

    public async Task<ReviewBatchTransitionResult> TransitionAsync(ReviewBatchTransition transition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transition);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, transition.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var before = await ReadBatchAsync(tx, transition.BatchId, forUpdate: true, cancellationToken).ConfigureAwait(false);
        if (before is null)
        {
            return new ReviewBatchTransitionResult(ReviewBatchTransitionStatus.NotFound, null);
        }

        if (before.Version != transition.ExpectedVersion)
        {
            return new ReviewBatchTransitionResult(ReviewBatchTransitionStatus.VersionConflict, before);
        }

        (ReviewBatchStatus Status, Guid? Assignee, ReviewCheckoutEnd? End, Guid? Open)? next = (transition.Kind, before.Status) switch
        {
            (ReviewBatchTransitionKind.CheckOut, ReviewBatchStatus.Available) =>
                (ReviewBatchStatus.CheckedOut, transition.ActorId, null, transition.ActorId),
            (ReviewBatchTransitionKind.Return, ReviewBatchStatus.CheckedOut) =>
                (ReviewBatchStatus.Available, null, ReviewCheckoutEnd.Returned, null),
            (ReviewBatchTransitionKind.Complete, ReviewBatchStatus.CheckedOut) =>
                (ReviewBatchStatus.Completed, before.AssigneeId, ReviewCheckoutEnd.Completed, null),
            (ReviewBatchTransitionKind.Assign, _) when transition.AssigneeId is { } assignee =>
                (ReviewBatchStatus.CheckedOut, assignee, ReviewCheckoutEnd.Reassigned, assignee),
            (ReviewBatchTransitionKind.Assign, not ReviewBatchStatus.Available) =>
                (ReviewBatchStatus.Available, null, ReviewCheckoutEnd.Reassigned, null),
            _ => null,
        };
        if (next is not { } change)
        {
            return new ReviewBatchTransitionResult(ReviewBatchTransitionStatus.InvalidState, before);
        }

        // Assigning the batch to the reviewer who already holds it changes nothing.
        if (before.Status == ReviewBatchStatus.CheckedOut && change.Status == ReviewBatchStatus.CheckedOut && before.AssigneeId == change.Assignee)
        {
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ReviewBatchTransitionResult(ReviewBatchTransitionStatus.Ok, before);
        }

        await using (var command = tx.Command(
            """
            UPDATE opportunity.review_batch_checkout
               SET checked_in_at = now(), checked_in_by = @actor, end_kind = @end
             WHERE workspace_id = @ws AND batch_id = @batch AND checked_in_at IS NULL;
            INSERT INTO opportunity.review_batch_checkout (workspace_id, batch_id, checkout_id, user_id, checked_out_at, checked_out_by)
            SELECT @ws, @batch, @checkout, @open, now(), @actor WHERE @open::uuid IS NOT NULL;
            UPDATE opportunity.review_batch
               SET status = @status, assignee_id = @assignee, status_changed_at = now(), status_changed_by = @actor, version = version + 1
             WHERE workspace_id = @ws AND batch_id = @batch;
            """))
        {
            command.Parameters.AddWithValue("ws", transition.WorkspaceId);
            command.Parameters.AddWithValue("batch", transition.BatchId);
            command.Parameters.AddWithValue("actor", transition.ActorId);
            command.Parameters.Add(new NpgsqlParameter("end", NpgsqlDbType.Smallint) { Value = change.End is { } e ? (short)e : DBNull.Value });
            command.Parameters.AddWithValue("checkout", Guid.CreateVersion7());
            command.Parameters.Add(new NpgsqlParameter("open", NpgsqlDbType.Uuid) { Value = (object?)change.Open ?? DBNull.Value });
            command.Parameters.AddWithValue("status", (short)change.Status);
            command.Parameters.Add(new NpgsqlParameter("assignee", NpgsqlDbType.Uuid) { Value = (object?)change.Assignee ?? DBNull.Value });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var after = (await ReadBatchAsync(tx, transition.BatchId, forUpdate: false, cancellationToken).ConfigureAwait(false))!;
        await AuditSql.InsertAsync(tx, transition.Audit(before, after), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ReviewBatchTransitionResult(ReviewBatchTransitionStatus.Ok, after);
    }

    private static async Task<ReviewBatchRecord?> ReadBatchAsync(WorkspaceTransaction tx, Guid batchId, bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            $"{BatchSelect} WHERE b.workspace_id = @ws AND b.batch_id = @id{(forUpdate ? " FOR UPDATE OF b" : string.Empty)}");
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("id", batchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadBatch(reader) : null;
    }

    private static ReviewBatchRecord ReadBatch(NpgsqlDataReader reader) => new()
    {
        BatchId = reader.GetGuid(0),
        BatchSetId = reader.GetGuid(1),
        BatchSetName = reader.GetString(2),
        Ordinal = reader.GetInt32(3),
        Name = reader.GetString(4),
        DocumentCount = reader.GetInt32(5),
        Status = (ReviewBatchStatus)reader.GetInt16(6),
        AssigneeId = reader.IsDBNull(7) ? null : reader.GetGuid(7),
        StatusChangedAt = reader.GetFieldValue<DateTimeOffset>(8),
        StatusChangedBy = reader.IsDBNull(9) ? null : reader.GetGuid(9),
        Version = reader.GetInt64(10),
        Pass = (ReviewPass)reader.GetInt16(11),
        ReviewerGroup = reader.IsDBNull(12) ? null : reader.GetString(12),
    };

    private static async Task<List<ReviewBatchSetRecord>> ReadSetsAsync(
        WorkspaceTransaction tx, string sql, Action<NpgsqlParameterCollection> parameters, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(sql);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        parameters(command.Parameters);
        var sets = new List<ReviewBatchSetRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sets.Add(new ReviewBatchSetRecord
            {
                BatchSetId = reader.GetGuid(0),
                Name = reader.GetString(1),
                BatchPrefix = reader.GetString(2),
                MaxBatchSize = reader.GetInt32(3),
                KeepFamiliesTogether = reader.GetBoolean(4),
                KeepThreadsTogether = reader.GetBoolean(5),
                Pass = (ReviewPass)reader.GetInt16(6),
                QcOfBatchSetId = reader.IsDBNull(7) ? null : reader.GetGuid(7),
                ReviewerGroup = reader.IsDBNull(8) ? null : reader.GetString(8),
                SnapshotId = reader.GetGuid(9),
                BatchCount = reader.GetInt32(10),
                DocumentCount = reader.GetInt64(11),
                CreatedBy = reader.GetGuid(12),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(13),
                Available = reader.GetInt32(14),
                CheckedOut = reader.GetInt32(15),
                Completed = reader.GetInt32(16),
            });
        }

        return sets;
    }

    /// <summary>Packs groups into batches and writes closed batches with their members in bulk.</summary>
    private sealed class BatchCutter(NewReviewBatchSet set)
    {
        private readonly ReviewBatchPacker _packer = new(set.MaxBatchSize);
        private readonly List<(Guid BatchId, int Ordinal, List<Guid> Members)> _closed = [];
        private (Guid BatchId, int Ordinal, List<Guid> Members)? _open;
        private int _closedMembers;

        public int Batches { get; private set; }

        public long Documents { get; private set; }

        public void Place(List<Guid> group)
        {
            var ordinal = _packer.Place(group.Count);
            if (_open is not { } open || open.Ordinal != ordinal)
            {
                if (_open is { } previous)
                {
                    _closed.Add(previous);
                    _closedMembers += previous.Members.Count;
                }

                _open = open = (Guid.CreateVersion7(), ordinal, []);
                Batches++;
            }

            open.Members.AddRange(group);
            Documents += group.Count;
        }

        public async Task FlushAsync(WorkspaceTransaction tx, bool final, CancellationToken cancellationToken)
        {
            if (final && _open is { } last)
            {
                _closed.Add(last);
                _closedMembers += last.Members.Count;
                _open = null;
            }

            if (_closed.Count == 0 || (!final && _closedMembers < StreamPage))
            {
                return;
            }

            await using (var batches = tx.Command(
                """
                INSERT INTO opportunity.review_batch (workspace_id, batch_id, batch_set_id, ordinal, name, document_count)
                SELECT @ws, t.id, @set, t.ordinal, t.name, t.count FROM unnest(@ids, @ordinals, @names, @counts) AS t(id, ordinal, name, count)
                """))
            {
                batches.Parameters.AddWithValue("ws", set.WorkspaceId);
                batches.Parameters.AddWithValue("set", set.BatchSetId);
                batches.Parameters.AddWithValue("ids", _closed.Select(b => b.BatchId).ToArray());
                batches.Parameters.AddWithValue("ordinals", _closed.Select(b => b.Ordinal).ToArray());
                batches.Parameters.AddWithValue("names", _closed.Select(b => ReviewBatchRules.BatchName(set.BatchPrefix, b.Ordinal)).ToArray());
                batches.Parameters.AddWithValue("counts", _closed.Select(b => b.Members.Count).ToArray());
                await batches.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var chunk in _closed.SelectMany(b => b.Members.Select((d, i) => (Batch: b.BatchId, Document: d, Position: i + 1))).Chunk(StreamPage))
            {
                await using var members = tx.Command(
                    """
                    INSERT INTO opportunity.review_batch_document (workspace_id, batch_set_id, document_id, batch_id, position)
                    SELECT @ws, @set, t.document_id, t.batch_id, t.position FROM unnest(@documents, @batches, @positions) AS t(document_id, batch_id, position)
                    """);
                members.Parameters.AddWithValue("ws", set.WorkspaceId);
                members.Parameters.AddWithValue("set", set.BatchSetId);
                members.Parameters.AddWithValue("documents", chunk.Select(c => c.Document).ToArray());
                members.Parameters.AddWithValue("batches", chunk.Select(c => c.Batch).ToArray());
                members.Parameters.AddWithValue("positions", chunk.Select(c => c.Position).ToArray());
                await members.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            _closed.Clear();
            _closedMembers = 0;
        }
    }
}

public static class ReviewBatchStoreRegistration
{
    public static IServiceCollection AddPostgresReviewBatchStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IReviewBatchStore, ReviewBatchStore>();
        return services;
    }
}
