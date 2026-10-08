using System.Collections.Concurrent;
using System.Data;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit;
using Opportunity.Application.Redactions;
using Opportunity.Core.Pages;
using Opportunity.Core.Redactions;
using Opportunity.Data.Audit;

namespace Opportunity.Data.Redactions;

/// <summary>
/// PostgreSQL <see cref="IRedactionStore"/> over <c>redaction_set</c>, <c>redaction_reason</c>,
/// <c>redaction_revision</c> (insert-only) and <c>document_redaction_state</c> (V0045, tenant tables under forced RLS).
/// A save locks the document's state row, checks the version, inserts one revision per change as version + 1,
/// refreshes the cached count and inserts its audit events, all in one transaction. Reads of a document's state run in
/// one REPEATABLE READ snapshot.
/// </summary>
public sealed class RedactionStore(NpgsqlDataSource dataSource) : IRedactionStore
{
    private static readonly ConcurrentDictionary<Guid, bool> Seeded = new();

    private static readonly short[] RedactablePurposes = [(short)PageImagePurpose.Review, (short)PageImagePurpose.Original];

    private static readonly short[] InlineFormats = [(short)PageImageFormat.Jpeg, (short)PageImageFormat.Png, (short)PageImageFormat.WebP];

    private const string SetSelect =
        """
        SELECT s.redaction_set_id, s.name, s.description, s.is_retired, s.modified_by,
               coalesce(u.display_name, u.email, s.modified_by::text), s.modified_at, s.version
          FROM opportunity.redaction_set s
          LEFT JOIN opportunity.app_user u ON u.user_id = s.modified_by
        """;

    private const string ReasonSelect =
        "SELECT code, name, category, box_label, is_active, sort_order, version FROM opportunity.redaction_reason";

    public async Task<IReadOnlyList<RedactionSetRecord>> ListSetsAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await EnsureDefaultsAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var sets = await ReadSetsAsync(tx, $"{SetSelect} WHERE s.workspace_id = @ws ORDER BY s.is_retired, lower(s.name), s.redaction_set_id",
            _ => { }, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return sets;
    }

    public async Task<RedactionSetRecord?> GetSetAsync(Guid workspaceId, Guid redactionSetId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var set = await ReadSetAsync(tx, redactionSetId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return set;
    }

    public async Task<RedactionSetWriteResult> CreateSetAsync(
        Guid workspaceId, Guid redactionSetId, Guid actorId, string name, string? description, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await EnsureDefaultsAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.redaction_set (workspace_id, redaction_set_id, name, description, created_by, modified_by)
            VALUES (@ws, @id, @name, @description, @actor, @actor)
            """))
        {
            insert.Parameters.AddWithValue("ws", workspaceId);
            insert.Parameters.AddWithValue("id", redactionSetId);
            insert.Parameters.AddWithValue("actor", actorId);
            insert.Parameters.AddWithValue("name", name);
            insert.Parameters.Add(new NpgsqlParameter("description", NpgsqlDbType.Text) { Value = (object?)description ?? DBNull.Value });
            if (!await TryExecuteAsync(insert, cancellationToken).ConfigureAwait(false))
            {
                return new RedactionSetWriteResult(RedactionWriteStatus.NameTaken);
            }
        }

        await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        var saved = await ReadSetAsync(tx, redactionSetId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RedactionSetWriteResult(RedactionWriteStatus.Ok, saved);
    }

    public async Task<RedactionSetWriteResult> UpdateSetAsync(
        Guid workspaceId, Guid redactionSetId, long expectedVersion, Guid actorId, string name, string? description, bool retired, AuditEvent audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var status = await LockVersionAsync(tx,
            "SELECT version FROM opportunity.redaction_set WHERE workspace_id = @ws AND redaction_set_id = @id FOR UPDATE",
            p => p.AddWithValue("id", redactionSetId), expectedVersion, cancellationToken).ConfigureAwait(false);
        if (status != RedactionWriteStatus.Ok)
        {
            return new RedactionSetWriteResult(status);
        }

        await using (var update = tx.Command(
            """
            UPDATE opportunity.redaction_set
               SET name = @name, description = @description, is_retired = @retired, modified_by = @actor, modified_at = now(), version = version + 1
             WHERE workspace_id = @ws AND redaction_set_id = @id
            """))
        {
            update.Parameters.AddWithValue("ws", workspaceId);
            update.Parameters.AddWithValue("id", redactionSetId);
            update.Parameters.AddWithValue("actor", actorId);
            update.Parameters.AddWithValue("name", name);
            update.Parameters.Add(new NpgsqlParameter("description", NpgsqlDbType.Text) { Value = (object?)description ?? DBNull.Value });
            update.Parameters.AddWithValue("retired", retired);
            if (!await TryExecuteAsync(update, cancellationToken).ConfigureAwait(false))
            {
                return new RedactionSetWriteResult(RedactionWriteStatus.NameTaken);
            }
        }

        await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        var saved = await ReadSetAsync(tx, redactionSetId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RedactionSetWriteResult(RedactionWriteStatus.Ok, saved);
    }

    public async Task<IReadOnlyList<RedactionReasonRecord>> ListReasonsAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await EnsureDefaultsAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var reasons = await ReadReasonsAsync(tx, $"{ReasonSelect} WHERE workspace_id = @ws ORDER BY sort_order, lower(name), code", _ => { },
            cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return reasons;
    }

    public async Task<RedactionReasonWriteResult> CreateReasonAsync(
        Guid workspaceId, RedactionReasonRecord reason, Guid actorId, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.redaction_reason (workspace_id, code, name, category, box_label, is_active, sort_order, modified_by)
            VALUES (@ws, @code, @name, @category, @label, @active, @order, @actor)
            """))
        {
            BindReason(insert.Parameters, workspaceId, reason, actorId);
            if (!await TryExecuteAsync(insert, cancellationToken).ConfigureAwait(false))
            {
                return new RedactionReasonWriteResult(RedactionWriteStatus.NameTaken);
            }
        }

        return await FinishReasonAsync(tx, workspaceId, reason.Code, audit, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RedactionReasonWriteResult> UpdateReasonAsync(
        Guid workspaceId, RedactionReasonRecord reason, long expectedVersion, Guid actorId, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var status = await LockVersionAsync(tx, "SELECT version FROM opportunity.redaction_reason WHERE workspace_id = @ws AND code = @code FOR UPDATE",
            p => p.AddWithValue("code", reason.Code), expectedVersion, cancellationToken).ConfigureAwait(false);
        if (status != RedactionWriteStatus.Ok)
        {
            return new RedactionReasonWriteResult(status);
        }

        await using (var update = tx.Command(
            """
            UPDATE opportunity.redaction_reason
               SET name = @name, category = @category, box_label = @label, is_active = @active, sort_order = @order, modified_by = @actor,
                   modified_at = now(), version = version + 1
             WHERE workspace_id = @ws AND code = @code
            """))
        {
            BindReason(update.Parameters, workspaceId, reason, actorId);
            if (!await TryExecuteAsync(update, cancellationToken).ConfigureAwait(false))
            {
                return new RedactionReasonWriteResult(RedactionWriteStatus.NameTaken);
            }
        }

        return await FinishReasonAsync(tx, workspaceId, reason.Code, audit, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DocumentRedactionState?> GetDocumentAsync(
        Guid workspaceId, Guid documentId, Guid redactionSetId, long? asOfVersion, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);
        var state = await ReadStateAsync(tx, documentId, redactionSetId, asOfVersion, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return state;
    }

    public async Task<IReadOnlyList<RedactionRevisionRecord>> HistoryAsync(
        Guid workspaceId, Guid documentId, Guid redactionSetId, int limit, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var items = new List<RedactionRevisionRecord>();
        await using (var command = tx.Command(
            """
            SELECT r.redaction_version, r.redaction_id, r.operation, r.page_set_id, r.ordinal, r.x, r.y, r.w, r.h, r.redaction_type, r.reason_code,
                   r.note, r.actor_id, coalesce(u.display_name, u.email, r.actor_id::text), r.created_at
              FROM opportunity.redaction_revision r
              LEFT JOIN opportunity.app_user u ON u.user_id = r.actor_id
             WHERE r.workspace_id = @ws AND r.document_id = @doc AND r.redaction_set_id = @set
             ORDER BY r.redaction_version DESC, r.redaction_id
             LIMIT @limit
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("doc", documentId);
            command.Parameters.AddWithValue("set", redactionSetId);
            command.Parameters.AddWithValue("limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new RedactionRevisionRecord(
                    reader.GetInt64(0),
                    reader.GetGuid(1),
                    (RedactionOperation)reader.GetInt16(2),
                    reader.GetGuid(3),
                    reader.GetInt32(4),
                    new NormalizedRect(reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8)),
                    (RedactionType)reader.GetInt16(9),
                    reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    new RedactionActor(reader.GetGuid(12), reader.GetString(13)),
                    reader.GetFieldValue<DateTimeOffset>(14)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return items;
    }

    public async Task<RedactionWriteStatus> SaveAsync(
        Guid workspaceId,
        Guid documentId,
        Guid redactionSetId,
        long expectedVersion,
        Guid? activePageSetId,
        Guid actorId,
        IReadOnlyList<PlannedRevision> revisions,
        IReadOnlyList<AuditEvent> audits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        ArgumentNullException.ThrowIfNull(audits);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var ensure = tx.Command(
            """
            INSERT INTO opportunity.document_redaction_state (workspace_id, document_id, redaction_set_id)
            SELECT @ws, @doc, @set
             WHERE EXISTS (SELECT 1 FROM opportunity.redaction_set s WHERE s.workspace_id = @ws AND s.redaction_set_id = @set)
            ON CONFLICT DO NOTHING
            """))
        {
            Bind(ensure.Parameters, workspaceId, documentId, redactionSetId);
            await ensure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var lockState = tx.Command(
            """
            SELECT current_version FROM opportunity.document_redaction_state
             WHERE workspace_id = @ws AND document_id = @doc AND redaction_set_id = @set
               FOR UPDATE
            """))
        {
            Bind(lockState.Parameters, workspaceId, documentId, redactionSetId);
            switch (await lockState.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))
            {
                case long current when current == expectedVersion:
                    break;
                case long:
                    return RedactionWriteStatus.VersionConflict;
                default:
                    return RedactionWriteStatus.NotFound;
            }
        }

        if (activePageSetId is { } pageSetId)
        {
            await using var page = tx.Command(
                """
                SELECT d.active_page_set_id = @ps AND ps.status = @ready
                  FROM opportunity.document d
                  JOIN opportunity.page_set ps
                    ON ps.workspace_id = d.workspace_id AND ps.document_id = d.document_id AND ps.page_set_id = d.active_page_set_id
                 WHERE d.workspace_id = @ws AND d.document_id = @doc
                   FOR SHARE OF d
                """);
            Bind(page.Parameters, workspaceId, documentId, redactionSetId);
            page.Parameters.AddWithValue("ps", pageSetId);
            page.Parameters.AddWithValue("ready", (short)PageSetStatus.Ready);
            if (await page.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                return RedactionWriteStatus.PageSetChanged;
            }
        }

        var version = expectedVersion + 1;
        var batch = tx.Batch();
        await using (batch.ConfigureAwait(false))
        {
            foreach (var revision in revisions)
            {
                var insert = new NpgsqlBatchCommand(
                    """
                    INSERT INTO opportunity.redaction_revision
                        (workspace_id, document_id, redaction_set_id, redaction_version, redaction_id, operation, page_set_id, ordinal, x, y, w, h,
                         redaction_type, reason_code, note, actor_id, actor_type)
                    VALUES (@ws, @doc, @set, @version, @id, @operation, @ps, @ordinal, @x, @y, @w, @h, @type, @reason, @note, @actor, @actorType)
                    """);
                Bind(insert.Parameters, workspaceId, documentId, redactionSetId);
                insert.Parameters.AddWithValue("version", version);
                insert.Parameters.AddWithValue("id", revision.RedactionId);
                insert.Parameters.AddWithValue("operation", (short)revision.Operation);
                insert.Parameters.AddWithValue("ps", revision.PageSetId);
                insert.Parameters.AddWithValue("ordinal", revision.Ordinal);
                insert.Parameters.AddWithValue("x", revision.Rect.X);
                insert.Parameters.AddWithValue("y", revision.Rect.Y);
                insert.Parameters.AddWithValue("w", revision.Rect.W);
                insert.Parameters.AddWithValue("h", revision.Rect.H);
                insert.Parameters.AddWithValue("type", (short)revision.Type);
                insert.Parameters.AddWithValue("reason", revision.ReasonCode);
                insert.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Text) { Value = (object?)revision.Note ?? DBNull.Value });
                insert.Parameters.AddWithValue("actor", actorId);
                insert.Parameters.AddWithValue("actorType", (short)RedactionActorType.Human);
                batch.BatchCommands.Add(insert);
            }

            var update = new NpgsqlBatchCommand(
                """
                UPDATE opportunity.document_redaction_state st
                   SET current_version = @version,
                       active_count = (SELECT count(*) FROM (
                           SELECT DISTINCT ON (r.redaction_id) r.operation
                             FROM opportunity.redaction_revision r
                            WHERE r.workspace_id = st.workspace_id AND r.document_id = st.document_id AND r.redaction_set_id = st.redaction_set_id
                            ORDER BY r.redaction_id, r.redaction_version DESC) latest
                          WHERE latest.operation <> 3),
                       page_set_id = coalesce(@ps, st.page_set_id),
                       modified_by = @actor,
                       modified_at = now()
                 WHERE st.workspace_id = @ws AND st.document_id = @doc AND st.redaction_set_id = @set
                """);
            Bind(update.Parameters, workspaceId, documentId, redactionSetId);
            update.Parameters.AddWithValue("version", version);
            update.Parameters.Add(new NpgsqlParameter("ps", NpgsqlDbType.Uuid) { Value = (object?)activePageSetId ?? DBNull.Value });
            update.Parameters.AddWithValue("actor", actorId);
            batch.BatchCommands.Add(update);
            await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var audit in audits)
        {
            await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return RedactionWriteStatus.Ok;
    }

    /// <summary>
    /// Creates the default Redaction Set and reasons of a workspace that has none (idempotent; concurrent callers
    /// insert nothing twice). Remembered per process once done.
    /// </summary>
    private async Task EnsureDefaultsAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        if (Seeded.ContainsKey(workspaceId))
        {
            return;
        }

        var reasons = RedactionDefaults.Reasons;
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var command = tx.Command(
            """
            INSERT INTO opportunity.redaction_set (workspace_id, redaction_set_id, name)
            SELECT @ws, @set, @setName
             WHERE NOT EXISTS (SELECT 1 FROM opportunity.redaction_set WHERE workspace_id = @ws)
            ON CONFLICT DO NOTHING;
            INSERT INTO opportunity.redaction_reason (workspace_id, code, name, category, box_label, sort_order)
            SELECT @ws, d.code, d.name, d.category, d.label, d.sort_order
              FROM unnest(@codes, @names, @categories, @labels, @orders) AS d(code, name, category, label, sort_order)
             WHERE NOT EXISTS (SELECT 1 FROM opportunity.redaction_reason WHERE workspace_id = @ws)
            ON CONFLICT DO NOTHING;
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("set", Guid.CreateVersion7());
            command.Parameters.AddWithValue("setName", RedactionDefaults.SetName);
            command.Parameters.AddWithValue("codes", reasons.Select(r => r.Code).ToArray());
            command.Parameters.AddWithValue("names", reasons.Select(r => r.Name).ToArray());
            command.Parameters.AddWithValue("categories", reasons.Select(r => (short)r.Category).ToArray());
            command.Parameters.AddWithValue("labels", reasons.Select(r => r.BoxLabel).ToArray());
            command.Parameters.AddWithValue("orders", reasons.Select(r => r.SortOrder).ToArray());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        Seeded.TryAdd(workspaceId, true);
    }

    private static async Task<DocumentRedactionState?> ReadStateAsync(
        WorkspaceTransaction tx, Guid documentId, Guid redactionSetId, long? asOfVersion, CancellationToken cancellationToken)
    {
        var ws = tx.WorkspaceId;
        var set = await ReadSetAsync(tx, redactionSetId, cancellationToken).ConfigureAwait(false);
        if (set is null)
        {
            return null;
        }

        long currentVersion = 0;
        RedactionVersionChange? lastChange = null;
        await using (var command = tx.Command(
            """
            SELECT st.current_version, st.modified_by, coalesce(u.display_name, u.email, st.modified_by::text), st.modified_at
              FROM opportunity.document_redaction_state st
              LEFT JOIN opportunity.app_user u ON u.user_id = st.modified_by
             WHERE st.workspace_id = @ws AND st.document_id = @doc AND st.redaction_set_id = @set
            """))
        {
            Bind(command.Parameters, ws, documentId, redactionSetId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                currentVersion = reader.GetInt64(0);
                if (currentVersion > 0 && !reader.IsDBNull(1))
                {
                    lastChange = new RedactionVersionChange(
                        new RedactionActor(reader.GetGuid(1), reader.GetString(2)), reader.GetFieldValue<DateTimeOffset>(3), currentVersion);
                }
            }
        }

        Guid? activePageSetId = null;
        PageSetStatus? activeStatus = null;
        await using (var command = tx.Command(
            """
            SELECT d.active_page_set_id, ps.status
              FROM opportunity.document d
              LEFT JOIN opportunity.page_set ps
                ON ps.workspace_id = d.workspace_id AND ps.document_id = d.document_id AND ps.page_set_id = d.active_page_set_id
             WHERE d.workspace_id = @ws AND d.document_id = @doc
            """))
        {
            Bind(command.Parameters, ws, documentId, redactionSetId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && !reader.IsDBNull(0))
            {
                activePageSetId = reader.GetGuid(0);
                activeStatus = reader.IsDBNull(1) ? null : (PageSetStatus)reader.GetInt16(1);
            }
        }

        var pages = new Dictionary<int, RedactionPage>();
        if (activePageSetId is { } pageSetId)
        {
            await using var command = tx.Command(
                """
                SELECT p.ordinal, p.width_pt, p.height_pt,
                       EXISTS (SELECT 1 FROM opportunity.page_image pi
                                WHERE pi.workspace_id = p.workspace_id AND pi.page_set_id = p.page_set_id AND pi.ordinal = p.ordinal
                                  AND pi.purpose = ANY (@purposes) AND pi.format = ANY (@formats))
                  FROM opportunity.page p
                 WHERE p.workspace_id = @ws AND p.page_set_id = @ps AND p.document_id = @doc
                 ORDER BY p.ordinal
                """);
            Bind(command.Parameters, ws, documentId, redactionSetId);
            command.Parameters.AddWithValue("ps", pageSetId);
            command.Parameters.Add(new NpgsqlParameter<short[]>("purposes", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { TypedValue = RedactablePurposes });
            command.Parameters.Add(new NpgsqlParameter<short[]>("formats", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { TypedValue = InlineFormats });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var ordinal = reader.GetInt32(0);
                pages[ordinal] = new RedactionPage(ordinal, reader.GetDecimal(1), reader.GetDecimal(2), reader.GetBoolean(3));
            }
        }

        var version = Math.Min(asOfVersion ?? currentVersion, currentVersion);
        var redactions = new List<RedactionRecord>();
        if (version > 0)
        {
            await using var command = tx.Command(
                """
                WITH latest AS (
                    SELECT DISTINCT ON (r.redaction_id) r.*
                      FROM opportunity.redaction_revision r
                     WHERE r.workspace_id = @ws AND r.document_id = @doc AND r.redaction_set_id = @set AND r.redaction_version <= @version
                     ORDER BY r.redaction_id, r.redaction_version DESC
                ), added AS (
                    SELECT DISTINCT ON (a.redaction_id) a.redaction_id, a.actor_id, a.created_at
                      FROM opportunity.redaction_revision a
                     WHERE a.workspace_id = @ws AND a.document_id = @doc AND a.redaction_set_id = @set AND a.redaction_version <= @version
                       AND a.operation = 1
                     ORDER BY a.redaction_id, a.redaction_version DESC
                )
                SELECT l.redaction_id, l.page_set_id, l.ordinal, l.x, l.y, l.w, l.h, l.redaction_type, l.reason_code, l.note,
                       a.actor_id, coalesce(ua.display_name, ua.email, a.actor_id::text), a.created_at,
                       l.actor_id, coalesce(um.display_name, um.email, l.actor_id::text), l.created_at, l.redaction_version
                  FROM latest l
                  JOIN added a ON a.redaction_id = l.redaction_id
                  LEFT JOIN opportunity.app_user ua ON ua.user_id = a.actor_id
                  LEFT JOIN opportunity.app_user um ON um.user_id = l.actor_id
                 WHERE l.operation <> 3
                 ORDER BY l.ordinal, l.y, l.x, l.redaction_id
                """);
            Bind(command.Parameters, ws, documentId, redactionSetId);
            command.Parameters.AddWithValue("version", version);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                redactions.Add(new RedactionRecord(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetInt32(2),
                    new NormalizedRect(reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6)),
                    (RedactionType)reader.GetInt16(7),
                    reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    new RedactionActor(reader.GetGuid(10), reader.GetString(11)),
                    reader.GetFieldValue<DateTimeOffset>(12),
                    new RedactionActor(reader.GetGuid(13), reader.GetString(14)),
                    reader.GetFieldValue<DateTimeOffset>(15),
                    reader.GetInt64(16)));
            }
        }

        var reasons = await ReadReasonsAsync(tx, $"{ReasonSelect} WHERE workspace_id = @ws", _ => { }, cancellationToken).ConfigureAwait(false);
        return new DocumentRedactionState
        {
            Reasons = reasons.ToDictionary(r => r.Code, StringComparer.Ordinal),
            DocumentId = documentId,
            Set = set,
            Version = version,
            CurrentVersion = currentVersion,
            ActivePageSetId = activePageSetId,
            ActivePageSetStatus = activeStatus,
            Pages = pages,
            Redactions = redactions,
            LastChange = lastChange,
        };
    }

    private static async Task<RedactionSetRecord?> ReadSetAsync(WorkspaceTransaction tx, Guid redactionSetId, CancellationToken cancellationToken)
    {
        var sets = await ReadSetsAsync(tx, $"{SetSelect} WHERE s.workspace_id = @ws AND s.redaction_set_id = @id",
            p => p.AddWithValue("id", redactionSetId), cancellationToken).ConfigureAwait(false);
        return sets.Count > 0 ? sets[0] : null;
    }

    private static async Task<List<RedactionSetRecord>> ReadSetsAsync(
        WorkspaceTransaction tx, string sql, Action<NpgsqlParameterCollection> bind, CancellationToken cancellationToken)
    {
        var sets = new List<RedactionSetRecord>();
        await using var command = tx.Command(sql);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        bind(command.Parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sets.Add(new RedactionSetRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetBoolean(3),
                reader.IsDBNull(4) ? null : new RedactionActor(reader.GetGuid(4), reader.GetString(5)),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetInt64(7)));
        }

        return sets;
    }

    private static async Task<List<RedactionReasonRecord>> ReadReasonsAsync(
        WorkspaceTransaction tx, string sql, Action<NpgsqlParameterCollection> bind, CancellationToken cancellationToken)
    {
        var reasons = new List<RedactionReasonRecord>();
        await using var command = tx.Command(sql);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        bind(command.Parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            reasons.Add(new RedactionReasonRecord(
                reader.GetString(0),
                reader.GetString(1),
                (RedactionReasonCategory)reader.GetInt16(2),
                reader.GetString(3),
                reader.GetBoolean(4),
                reader.GetInt32(5),
                reader.GetInt64(6)));
        }

        return reasons;
    }

    private static async Task<RedactionReasonWriteResult> FinishReasonAsync(
        WorkspaceTransaction tx, Guid workspaceId, string code, AuditEvent audit, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        var saved = await ReadReasonsAsync(tx, $"{ReasonSelect} WHERE workspace_id = @ws AND code = @code", p => p.AddWithValue("code", code),
            cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RedactionReasonWriteResult(RedactionWriteStatus.Ok, saved[0]);
    }

    private static void BindReason(NpgsqlParameterCollection parameters, Guid workspaceId, RedactionReasonRecord reason, Guid actorId)
    {
        parameters.AddWithValue("ws", workspaceId);
        parameters.AddWithValue("code", reason.Code);
        parameters.AddWithValue("name", reason.Name);
        parameters.AddWithValue("category", (short)reason.Category);
        parameters.AddWithValue("label", reason.BoxLabel);
        parameters.AddWithValue("active", reason.Active);
        parameters.AddWithValue("order", reason.SortOrder);
        parameters.AddWithValue("actor", actorId);
    }

    private static void Bind(NpgsqlParameterCollection parameters, Guid workspaceId, Guid documentId, Guid redactionSetId)
    {
        parameters.AddWithValue("ws", workspaceId);
        parameters.AddWithValue("doc", documentId);
        parameters.AddWithValue("set", redactionSetId);
    }

    /// <summary>Locks a versioned row: Ok when it exists at <paramref name="expectedVersion"/>.</summary>
    private static async Task<RedactionWriteStatus> LockVersionAsync(
        WorkspaceTransaction tx, string sql, Action<NpgsqlParameterCollection> bind, long expectedVersion, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(sql);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        bind(command.Parameters);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) switch
        {
            long version when version == expectedVersion => RedactionWriteStatus.Ok,
            long => RedactionWriteStatus.VersionConflict,
            _ => RedactionWriteStatus.NotFound,
        };
    }

    /// <summary>Runs a statement inside a savepoint; false when it violated a unique constraint (name or code).</summary>
    private static async Task<bool> TryExecuteAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await command.Transaction!.SaveAsync("unique_key", cancellationToken).ConfigureAwait(false);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await command.Transaction.ReleaseAsync("unique_key", cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await command.Transaction.RollbackAsync("unique_key", cancellationToken).ConfigureAwait(false);
            return false;
        }
    }
}

public static class RedactionStoreRegistration
{
    /// <summary>Registers the PostgreSQL <see cref="IRedactionStore"/> (needs an <see cref="NpgsqlDataSource"/>).</summary>
    public static IServiceCollection AddPostgresRedactionStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IRedactionStore, RedactionStore>();
        return services;
    }
}
