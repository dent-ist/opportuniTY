using Npgsql;

using NpgsqlTypes;

using Opportunity.Core.Documents;

namespace Opportunity.Data.Relationships;

/// <summary>
/// Family reconstruction inside the caller's transaction (E09-T01, ADR-009 §2). An import chunk calls
/// <see cref="LockAsync"/> first, then <see cref="ResolveAsync"/> after its document writes: the documents it wrote seed
/// a closure over every relation that can influence a family (control number named by ParentID, BegAttach or
/// AttachmentIDs; documents pointing at, grouped with, ranging over or ranged over by a member; current family mates).
/// That closed set is resolved by <see cref="FamilyResolver"/>, a pure function, and the differences are written.
/// </summary>
/// <remarks>
/// <para>Resolution is serialized per workspace by a transaction-scoped advisory lock taken before the chunk's first
/// row lock, so the transaction that commits last saw every document committed before it: families spanning chunks
/// or volumes, and members arriving in any order, converge to what a full re-resolution gives
/// (<see cref="ResolveAllAsync"/>).</para>
/// <para>A changed FamilyId, ParentDocumentId, FamilySequence, FamilyStatus or FamilyDate bumps DocumentVersion (R11)
/// except for documents created in the same transaction (still version 1, indexed by the chunk's own task). The
/// family root and parent foreign keys and the sequence uniqueness are deferred to the commit, so whole families can be
/// re-linked in any order. The family report (<c>family_issue</c>) is replaced for every document resolved.</para>
/// </remarks>
internal static class FamilyWriter
{
    private const string Columns =
        """
        d.document_id, d.control_number, d.control_number_norm, d.beg_attach_norm, d.end_attach_norm, d.parent_id_norm,
        d.group_identifier, d.attachment_ids_norm, d.end_bates, d.document_date, d.upstream_family_date, d.family_id,
        d.parent_document_id, d.family_sequence, d.family_status, d.family_date, d.duplicate_group_id, s.is_deleted,
        d.control_number_sort_key
        """;

    private const string From =
        """
        FROM opportunity.document d
        JOIN opportunity.document_projection_state s ON s.workspace_id = d.workspace_id AND s.document_id = d.document_id
        """;

    /// <summary>Serializes family resolution (and therefore import chunks) per workspace until the transaction ends.</summary>
    public static async Task LockAsync(WorkspaceTransaction tx, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tx);
        await using var command = tx.Command("SELECT pg_advisory_xact_lock(hashtextextended('opportunity.family ' || @ws::text, 0))");
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the families the <paramref name="seeds"/> belong to, before and after this transaction's writes. The
    /// caller holds <see cref="LockAsync"/>. <paramref name="created"/> are documents inserted by this transaction.
    /// </summary>
    public static async Task<FamilyWriteResult> ResolveAsync(
        WorkspaceTransaction tx, IReadOnlyCollection<Guid> seeds, IReadOnlySet<Guid> created, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentNullException.ThrowIfNull(created);
        if (seeds.Count == 0)
        {
            return FamilyWriteResult.Empty;
        }

        await NormalizeSourcesAsync(tx, [.. seeds.Distinct()], cancellationToken).ConfigureAwait(false);
        var rows = new Dictionary<Guid, Row>();
        var frontier = await ReadAsync(tx, $"SELECT {Columns} {From} WHERE d.workspace_id = @ws AND d.document_id = ANY(@ids)",
            c => c.Parameters.AddWithValue("ids", seeds.Distinct().ToArray()), cancellationToken).ConfigureAwait(false);
        while (frontier.Count > 0)
        {
            foreach (var row in frontier)
            {
                rows[row.DocumentId] = row;
            }

            frontier = [.. (await NeighboursAsync(tx, frontier, rows.Keys, cancellationToken).ConfigureAwait(false)).Where(r => !rows.ContainsKey(r.DocumentId))];
        }

        return await WriteAsync(tx, rows.Values, created, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-resolves every family of the workspace (the repair and determinism reference).</summary>
    public static async Task<FamilyWriteResult> ResolveAllAsync(WorkspaceTransaction tx, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tx);
        await LockAsync(tx, cancellationToken).ConfigureAwait(false);
        await NormalizeSourcesAsync(tx, null, cancellationToken).ConfigureAwait(false);
        var rows = await ReadAsync(tx, $"SELECT {Columns} {From} WHERE d.workspace_id = @ws", _ => { }, cancellationToken).ConfigureAwait(false);
        return await WriteAsync(tx, rows, new HashSet<Guid>(), cancellationToken).ConfigureAwait(false);
    }

    // Hosts run without ICU, where string.Normalize leaves non-ASCII text alone; compare in NFC like control_number_norm.
    private static async Task NormalizeSourcesAsync(WorkspaceTransaction tx, Guid[]? ids, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            $"""
            UPDATE opportunity.document d
            SET beg_attach_norm = normalize(beg_attach_norm, NFC), end_attach_norm = normalize(end_attach_norm, NFC),
                parent_id_norm = normalize(parent_id_norm, NFC),
                attachment_ids_norm = CASE WHEN attachment_ids_norm IS NULL THEN NULL
                    ELSE ARRAY(SELECT normalize(a, NFC) FROM unnest(attachment_ids_norm) WITH ORDINALITY AS u(a, o) ORDER BY o) END
            WHERE d.workspace_id = @ws {(ids is null ? string.Empty : "AND d.document_id = ANY(@ids)")}
              AND (beg_attach_norm IS NOT NFC NORMALIZED OR end_attach_norm IS NOT NFC NORMALIZED OR parent_id_norm IS NOT NFC NORMALIZED
                   OR array_to_string(attachment_ids_norm, ' ') IS NOT NFC NORMALIZED)
            """);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("ids", ids ?? []);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // The reverse AttachmentIDs lookup follows the (now NFC) lists of these documents.
        await using var attachments = tx.Command(
            $"""
            DELETE FROM opportunity.document_family_attachment
            WHERE workspace_id = @ws {(ids is null ? string.Empty : "AND document_id = ANY(@ids)")};
            INSERT INTO opportunity.document_family_attachment (workspace_id, attachment_norm, document_id)
            SELECT DISTINCT d.workspace_id, a, d.document_id
            FROM opportunity.document d CROSS JOIN LATERAL unnest(d.attachment_ids_norm) AS a
            WHERE d.workspace_id = @ws AND d.attachment_ids_norm IS NOT NULL {(ids is null ? string.Empty : "AND d.document_id = ANY(@ids)")};
            """);
        attachments.Parameters.AddWithValue("ws", tx.WorkspaceId);
        attachments.Parameters.AddWithValue("ids", ids ?? []);
        await attachments.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task<List<Row>> NeighboursAsync(WorkspaceTransaction tx, List<Row> frontier, IEnumerable<Guid> known, CancellationToken cancellationToken)
    {
        var norms = frontier.Select(r => r.ControlNumberNorm).ToArray();
        var refs = frontier.SelectMany(r => r.AttachmentIdsNorm.Append(r.ParentIdNorm).Append(r.BegAttachNorm).Append(r.EndAttachNorm))
            .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        var begs = frontier.Select(r => r.BegAttachNorm).OfType<string>().Concat(norms).Distinct(StringComparer.Ordinal).ToArray();
        var groups = frontier.Select(r => r.GroupIdentifier).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        var ranges = frontier.Where(r => r.BegAttachNorm is not null || r.EndAttachNorm is not null)
            .Select(r => (Beg: ControlNumber.SortKey(r.BegAttachNorm ?? r.EndAttachNorm!), End: ControlNumber.SortKey(r.EndAttachNorm ?? r.BegAttachNorm!)))
            .Where(r => string.CompareOrdinal(r.Beg, r.End) <= 0)
            .Distinct().ToList();
        var families = frontier.SelectMany(r => new Guid?[] { r.DocumentId, r.FamilyId, r.ParentDocumentId }).OfType<Guid>().Distinct().ToArray();
        var ws = tx.WorkspaceId.ToString();
        return ReadAsync(tx,
            $"""
            WITH ids AS (
                SELECT d.document_id FROM opportunity.document d WHERE d.workspace_id = @ws AND d.control_number_norm = ANY(@refs)
                UNION SELECT d.document_id FROM opportunity.document d WHERE d.workspace_id = @ws AND d.parent_id_norm = ANY(@norms)
                UNION SELECT d.document_id FROM opportunity.document d WHERE d.workspace_id = @ws AND d.group_identifier = ANY(@groups)
                UNION SELECT d.document_id FROM opportunity.document d WHERE d.workspace_id = @ws AND d.beg_attach_norm = ANY(@begs)
                UNION SELECT a.document_id FROM opportunity.document_family_attachment a
                      WHERE a.workspace_id = @ws AND a.attachment_norm = ANY(@norms)
                UNION SELECT d.document_id FROM opportunity.document d WHERE d.workspace_id = @ws AND d.family_id = ANY(@families)
                UNION SELECT d.document_id FROM unnest(@range_beg, @range_end) AS r(b, e)
                      JOIN opportunity.document d ON d.workspace_id = @ws AND d.control_number_sort_key BETWEEN r.b AND r.e
                UNION SELECT d.document_id FROM unnest(@keys) AS k(key)
                      JOIN opportunity.document d
                        ON d.family_beg_key <= d.family_end_key
                       AND opportunity.control_number_key_range(d.workspace_id::text || ' ' || d.family_beg_key, d.workspace_id::text || ' ' || d.family_end_key, '[]') @> k.key
                       AND d.workspace_id = @ws
                EXCEPT SELECT unnest(@known))
            SELECT {Columns} {From} JOIN ids ON ids.document_id = d.document_id WHERE d.workspace_id = @ws
            """,
            c =>
            {
                c.Parameters.AddWithValue("refs", refs);
                c.Parameters.AddWithValue("norms", norms);
                c.Parameters.AddWithValue("groups", groups);
                c.Parameters.AddWithValue("begs", begs);
                c.Parameters.AddWithValue("families", families);
                c.Parameters.AddWithValue("range_beg", ranges.Select(r => r.Beg).ToArray());
                c.Parameters.AddWithValue("range_end", ranges.Select(r => r.End).ToArray());
                c.Parameters.AddWithValue("keys", frontier.Select(r => ws + " " + r.SortKey).ToArray());
                c.Parameters.AddWithValue("known", known.ToArray());
            }, cancellationToken);
    }

    private static async Task<FamilyWriteResult> WriteAsync(
        WorkspaceTransaction tx, IReadOnlyCollection<Row> rows, IReadOnlySet<Guid> created, CancellationToken cancellationToken)
    {
        var live = rows.Where(r => !r.IsDeleted).ToList();
        var resolution = FamilyResolver.Resolve(live.Select(r => r.Source));
        var targets = new Dictionary<Guid, FamilyAssignment>(resolution.Assignments);

        // A deleted document belongs to no family: it becomes its own, so no live document keeps pointing at it.
        foreach (var deleted in rows.Where(r => r.IsDeleted))
        {
            targets[deleted.DocumentId] = new FamilyAssignment(deleted.DocumentId, null, 0, deleted.FamilyStatus, deleted.FamilyDate);
        }

        var changed = rows.Where(r => targets[r.DocumentId] is var t
            && (t.FamilyId != r.FamilyId || t.ParentDocumentId != r.ParentDocumentId || t.FamilySequence != r.FamilySequence
                || t.Status != r.FamilyStatus || t.FamilyDate != r.FamilyDate))
            .OrderBy(r => r.DocumentId)
            .ToList();

        var bumped = new List<(Guid DocumentId, long DocumentVersion)>();
        if (changed.Count > 0)
        {
            await using (var update = tx.Command(
                """
                SET CONSTRAINTS opportunity.document_family_root_fk, opportunity.document_parent_fk, opportunity.document_family_sequence_uq DEFERRED;
                UPDATE opportunity.document d
                SET family_id = t.family_id, parent_document_id = t.parent_id, family_sequence = t.seq, family_status = t.status,
                    family_date = t.family_date, updated_at = now()
                FROM unnest(@ids, @families, @parents, @seqs, @statuses, @dates) AS t(id, family_id, parent_id, seq, status, family_date)
                WHERE d.workspace_id = @ws AND d.document_id = t.id
                """))
            {
                update.Parameters.AddWithValue("ws", tx.WorkspaceId);
                update.Parameters.AddWithValue("ids", changed.Select(r => r.DocumentId).ToArray());
                update.Parameters.AddWithValue("families", changed.Select(r => targets[r.DocumentId].FamilyId).ToArray());
                update.Parameters.Add(new NpgsqlParameter("parents", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
                {
                    Value = changed.Select(r => targets[r.DocumentId].ParentDocumentId).ToArray(),
                });
                update.Parameters.AddWithValue("seqs", changed.Select(r => targets[r.DocumentId].FamilySequence).ToArray());
                update.Parameters.Add(new NpgsqlParameter("statuses", NpgsqlDbType.Array | NpgsqlDbType.Smallint)
                {
                    Value = changed.Select(r => (short)targets[r.DocumentId].Status).ToArray(),
                });
                update.Parameters.Add(new NpgsqlParameter("dates", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
                {
                    Value = changed.Select(r => targets[r.DocumentId].FamilyDate?.ToUniversalTime()).ToArray(),
                });
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var existing = changed.Where(r => !created.Contains(r.DocumentId)).Select(r => r.DocumentId).ToArray();
            if (existing.Length > 0)
            {
                await using var bump = tx.Command(
                    """
                    UPDATE opportunity.document_projection_state SET document_version = document_version + 1
                    WHERE workspace_id = @ws AND document_id = ANY(@ids)
                    RETURNING document_id, document_version
                    """);
                bump.Parameters.AddWithValue("ws", tx.WorkspaceId);
                bump.Parameters.AddWithValue("ids", existing);
                await using var reader = await bump.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    bumped.Add((reader.GetGuid(0), reader.GetInt64(1)));
                }
            }
        }

        await ReplaceIssuesAsync(tx, rows.Select(r => r.DocumentId).ToArray(), resolution.Issues, cancellationToken).ConfigureAwait(false);
        return new FamilyWriteResult(
            rows.Count,
            [.. changed.Select(r => r.DocumentId)],
            [.. bumped.OrderBy(b => b.DocumentId)],
            [.. changed.Select(r => r.DuplicateGroupId).OfType<Guid>().Distinct().Order()],
            resolution.Issues.Count);
    }

    private static async Task ReplaceIssuesAsync(WorkspaceTransaction tx, Guid[] documents, IReadOnlyList<FamilyIssue> issues, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            """
            DELETE FROM opportunity.family_issue WHERE workspace_id = @ws AND document_id = ANY(@documents);
            INSERT INTO opportunity.family_issue (workspace_id, document_id, issue_no, kind, message, related, missing_count)
            SELECT @ws, u.document_id, u.issue_no, u.kind, u.message, string_to_array(u.related, E'\x1f'), u.missing
            FROM unnest(@ids, @nos, @kinds, @messages, @related, @missing) AS u(document_id, issue_no, kind, message, related, missing);
            """);
        var numbered = issues.GroupBy(i => i.DocumentId).SelectMany(g => g.Select((issue, n) => (Issue: issue, No: (short)(n + 1)))).ToList();
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("documents", documents);
        command.Parameters.AddWithValue("ids", numbered.Select(i => i.Issue.DocumentId).ToArray());
        command.Parameters.AddWithValue("nos", numbered.Select(i => i.No).ToArray());
        command.Parameters.AddWithValue("kinds", numbered.Select(i => (short)i.Issue.Kind).ToArray());
        command.Parameters.AddWithValue("messages", numbered.Select(i => i.Issue.Message.Length <= 2000 ? i.Issue.Message : i.Issue.Message[..2000]).ToArray());
        command.Parameters.AddWithValue("related", numbered.Select(i => string.Join('\u001f', i.Issue.Related.Take(100))).ToArray());
        command.Parameters.Add(new NpgsqlParameter("missing", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = numbered.Select(i => i.Issue.MissingCount).ToArray() });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<Row>> ReadAsync(WorkspaceTransaction tx, string sql, Action<NpgsqlCommand> parameters, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(sql);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        parameters(command);
        var rows = new List<Row>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new Row
            {
                DocumentId = reader.GetGuid(0),
                ControlNumber = reader.GetString(1),
                ControlNumberNorm = reader.GetString(2),
                BegAttachNorm = Text(reader, 3),
                EndAttachNorm = Text(reader, 4),
                ParentIdNorm = Text(reader, 5),
                GroupIdentifier = Text(reader, 6),
                AttachmentIdsNorm = reader.IsDBNull(7) ? [] : reader.GetFieldValue<string[]>(7),
                EndBates = Text(reader, 8),
                DocumentDate = Instant(reader, 9),
                UpstreamFamilyDate = Instant(reader, 10),
                FamilyId = reader.GetGuid(11),
                ParentDocumentId = reader.IsDBNull(12) ? null : reader.GetGuid(12),
                FamilySequence = reader.GetInt32(13),
                FamilyStatus = (FamilyStatus)reader.GetInt16(14),
                FamilyDate = Instant(reader, 15),
                DuplicateGroupId = reader.IsDBNull(16) ? null : reader.GetGuid(16),
                IsDeleted = reader.GetBoolean(17),
                SortKey = reader.GetString(18),
            });
        }

        return rows;
    }

    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? Instant(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private sealed record Row
    {
        public required Guid DocumentId { get; init; }

        public required string ControlNumber { get; init; }

        public required string ControlNumberNorm { get; init; }

        public required string SortKey { get; init; }

        public string? BegAttachNorm { get; init; }

        public string? EndAttachNorm { get; init; }

        public string? ParentIdNorm { get; init; }

        public string? GroupIdentifier { get; init; }

        public required string[] AttachmentIdsNorm { get; init; }

        public string? EndBates { get; init; }

        public DateTimeOffset? DocumentDate { get; init; }

        public DateTimeOffset? UpstreamFamilyDate { get; init; }

        public Guid FamilyId { get; init; }

        public Guid? ParentDocumentId { get; init; }

        public int FamilySequence { get; init; }

        public FamilyStatus FamilyStatus { get; init; }

        public DateTimeOffset? FamilyDate { get; init; }

        public Guid? DuplicateGroupId { get; init; }

        public bool IsDeleted { get; init; }

        public FamilySource Source => new()
        {
            DocumentId = DocumentId,
            ControlNumber = ControlNumber,
            ControlNumberNorm = ControlNumberNorm,
            BegAttachNorm = BegAttachNorm,
            EndAttachNorm = EndAttachNorm,
            ParentIdNorm = ParentIdNorm,
            GroupIdentifier = GroupIdentifier,
            AttachmentIdsNorm = AttachmentIdsNorm,
            EndBates = EndBates,
            DocumentDate = DocumentDate,
            UpstreamFamilyDate = UpstreamFamilyDate,
        };
    }
}

/// <param name="Documents">Documents resolved (the closed set the seeds belong to).</param>
/// <param name="ChangedDocuments">Documents whose family columns changed, including ones created in the transaction.</param>
/// <param name="Bumped">Changed documents that existed before the transaction, with their new DocumentVersion.</param>
/// <param name="DuplicateGroupIds">Duplicate groups of changed documents: their primary depends on FamilyId and FamilyDate.</param>
/// <param name="Issues">Report lines of the resolved documents.</param>
internal sealed record FamilyWriteResult(
    int Documents,
    IReadOnlyList<Guid> ChangedDocuments,
    IReadOnlyList<(Guid DocumentId, long DocumentVersion)> Bumped,
    IReadOnlyList<Guid> DuplicateGroupIds,
    int Issues)
{
    public static FamilyWriteResult Empty { get; } = new(0, [], [], [], 0);
}
