using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit;
using Opportunity.Application.Productions;
using Opportunity.Core.Productions;
using Opportunity.Data.Audit;
using Opportunity.Data.Coding;

namespace Opportunity.Data.Productions;

/// <summary>
/// Confidentiality designations of productions (E12-T04, V0048). One SQL rule computes a member's designation from the
/// coding store, the production's overrides and its plan, exactly as <see cref="DesignationResolver"/> does: an
/// override wins; under the family rule the highest level coded on any member of the family in the production raises
/// the others; a level with an empty legend stamps nothing. Families are adjacent in production order, so a page of
/// members is computed with all members of its families.
/// </summary>
public sealed partial class ProductionRepository
{
    /// <summary>
    /// <c>page</c> (the members to answer for), <c>levels</c>, <c>members</c> (with their families), <c>fam</c> and
    /// <c>computed</c>(sequence, document_id, own_choice_id, choice_id, legend, source, unlisted, override_reason).
    /// Parameters: ws, id, after, limit (null: all), field, choices, legends, family.
    /// </summary>
    private const string ComputedDesignations =
        $$"""
        page AS (
            SELECT pd.sequence, pd.family_key FROM opportunity.production_document pd
             WHERE pd.workspace_id = @ws AND pd.production_id = @id AND pd.sequence > @after
             ORDER BY pd.sequence
             LIMIT @limit),
        levels AS (
            SELECT l.choice_id, l.rank, l.legend
              FROM unnest(@choices::integer[], @legends::text[]) WITH ORDINALITY AS l(choice_id, legend, rank)),
        members AS (
            SELECT pd.sequence, pd.document_id, pd.family_key, own.choice_id AS own_choice_id, lo.rank AS own_rank,
                   o.document_id IS NOT NULL AS overridden, o.choice_id AS override_choice_id, o.reason AS override_reason
              FROM opportunity.production_document pd
              LEFT JOIN LATERAL ({{CodingRepository.MemberDesignationChoiceSql}}) own ON true
              LEFT JOIN levels lo ON lo.choice_id = own.choice_id
              LEFT JOIN opportunity.production_designation_override o
                ON o.workspace_id = pd.workspace_id AND o.production_id = pd.production_id AND o.document_id = pd.document_id
               AND o.field_id = @field
             WHERE pd.workspace_id = @ws AND pd.production_id = @id
               AND pd.family_key IN (SELECT DISTINCT family_key FROM page)),
        fam AS (
            SELECT family_key, max(own_rank) AS best_rank FROM members WHERE @family AND own_rank IS NOT NULL GROUP BY family_key),
        resolved AS (
            SELECT m.sequence, m.document_id, m.own_choice_id, m.override_reason,
                   (m.own_choice_id IS NOT NULL AND m.own_rank IS NULL) AS unlisted,
                   CASE WHEN m.overridden THEN lv.rank
                        WHEN f.best_rank IS NOT NULL AND (m.own_rank IS NULL OR f.best_rank > m.own_rank) THEN f.best_rank
                        ELSE m.own_rank END AS eff_rank,
                   CASE WHEN m.overridden THEN 3
                        WHEN f.best_rank IS NOT NULL AND (m.own_rank IS NULL OR f.best_rank > m.own_rank) THEN 2
                        ELSE 1 END AS raw_source
              FROM members m
              LEFT JOIN fam f ON f.family_key = m.family_key
              LEFT JOIN levels lv ON lv.choice_id = m.override_choice_id),
        computed AS (
            SELECT r.sequence, r.document_id, r.own_choice_id, le.choice_id, coalesce(le.legend, '') AS legend,
                   CASE WHEN r.raw_source = 3 THEN 3 WHEN coalesce(le.legend, '') = '' THEN 0 ELSE r.raw_source END::smallint AS source,
                   r.unlisted, r.override_reason
              FROM resolved r
              JOIN page p ON p.sequence = r.sequence
              LEFT JOIN levels le ON le.rank = r.eff_rank)
        """;

    public async Task<IReadOnlyList<DesignationRow>> ReadDesignationsAsync(
        Guid workspaceId, Guid productionId, DesignationPlan plan, long afterSequence, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var production = await ReadOneAsync(tx, productionId, forUpdate: false, cancellationToken).ConfigureAwait(false);
        if (production is null)
        {
            return [];
        }

        var frozen = production.Status is ProductionStatus.Finalized or ProductionStatus.Voided;
        await using var command = tx.Command(frozen
            ? $$"""
              SELECT pd.sequence, pd.document_id, d.control_number, pd.prod_beg_bates, pd.prod_end_bates,
                     ({{CodingRepository.MemberDesignationChoiceSql}}),
                     pd.designation_choice_id, coalesce(pd.designation, ''), coalesce(pd.designation_source, 0), o.reason
                FROM opportunity.production_document pd
                LEFT JOIN opportunity.document d ON d.workspace_id = pd.workspace_id AND d.document_id = pd.document_id
                LEFT JOIN opportunity.production_designation_override o
                  ON o.workspace_id = pd.workspace_id AND o.production_id = pd.production_id AND o.document_id = pd.document_id
               WHERE pd.workspace_id = @ws AND pd.production_id = @id AND pd.sequence > @after
               ORDER BY pd.sequence
               LIMIT @limit
              """
            : $"""
              WITH {ComputedDesignations}
              SELECT c.sequence, c.document_id, d.control_number, pd.prod_beg_bates, pd.prod_end_bates, c.own_choice_id, c.choice_id, c.legend,
                     c.source, c.override_reason
                FROM computed c
                JOIN opportunity.production_document pd ON pd.workspace_id = @ws AND pd.production_id = @id AND pd.sequence = c.sequence
                LEFT JOIN opportunity.document d ON d.workspace_id = @ws AND d.document_id = c.document_id
               ORDER BY c.sequence
              """);
        AddDesignationParameters(command, workspaceId, productionId, plan, afterSequence, limit);
        var rows = new List<DesignationRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new DesignationRow(
                    reader.GetInt64(0), reader.GetGuid(1), NullableString(reader, 2), NullableString(reader, 3), NullableString(reader, 4),
                    NullableInt32(reader, 5), NullableInt32(reader, 6), reader.GetString(7), (DesignationSource)reader.GetInt16(8), NullableString(reader, 9)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }

    public async Task<RedesignationPage> ReadRedesignationsAsync(
        Guid workspaceId, Guid productionId, DesignationPlan plan, long afterSequence, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        long? lastScanned;
        await using (var last = tx.Command(
            """
            SELECT max(sequence) FROM (SELECT pd.sequence FROM opportunity.production_document pd
                                        WHERE pd.workspace_id = @ws AND pd.production_id = @id AND pd.sequence > @after
                                        ORDER BY pd.sequence LIMIT @limit) s
            """))
        {
            last.Parameters.AddWithValue("ws", workspaceId);
            last.Parameters.AddWithValue("id", productionId);
            last.Parameters.AddWithValue("after", afterSequence);
            last.Parameters.AddWithValue("limit", (long)limit);
            lastScanned = await last.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long l ? l : null;
        }

        await using var command = tx.Command(
            $"""
            WITH {ComputedDesignations}
            SELECT c.sequence, c.document_id, d.control_number, pd.prod_beg_bates, pd.prod_end_bates, pd.designation_choice_id, pd.designation,
                   c.choice_id, c.legend
              FROM computed c
              JOIN opportunity.production_document pd ON pd.workspace_id = @ws AND pd.production_id = @id AND pd.sequence = c.sequence
              LEFT JOIN opportunity.document d ON d.workspace_id = @ws AND d.document_id = c.document_id
             WHERE pd.designation_source IS NOT NULL AND pd.prod_beg_bates IS NOT NULL
               AND (pd.designation IS DISTINCT FROM c.legend OR pd.designation_choice_id IS DISTINCT FROM c.choice_id)
             ORDER BY c.sequence
            """);
        AddDesignationParameters(command, workspaceId, productionId, plan, afterSequence, limit);
        var rows = new List<RedesignationRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new RedesignationRow(
                    reader.GetInt64(0), reader.GetGuid(1), NullableString(reader, 2), reader.GetString(3), reader.GetString(4),
                    NullableInt32(reader, 5), reader.GetString(6), NullableInt32(reader, 7), reader.GetString(8)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RedesignationPage(rows, lastScanned);
    }

    public async Task<DesignationOverrideRow?> GetDesignationOverrideAsync(
        Guid workspaceId, Guid productionId, Guid documentId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var row = await ReadOverrideAsync(tx, productionId, documentId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    public async Task<DesignationOverrideStatus> SetDesignationOverrideAsync(
        Guid workspaceId, Guid productionId, Guid documentId, int fieldId, int? choiceId, string reason, Guid userId, bool remove, AuditEvent audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var production = await ReadOneAsync(tx, productionId, forUpdate: true, cancellationToken).ConfigureAwait(false);
        if (production is null)
        {
            return DesignationOverrideStatus.NotFound;
        }

        if (production.Status != ProductionStatus.Draft)
        {
            return DesignationOverrideStatus.Frozen;
        }

        if (remove)
        {
            await using var delete = tx.Command(
                "DELETE FROM opportunity.production_designation_override WHERE workspace_id = @ws AND production_id = @id AND document_id = @doc");
            delete.Parameters.AddWithValue("ws", workspaceId);
            delete.Parameters.AddWithValue("id", productionId);
            delete.Parameters.AddWithValue("doc", documentId);
            if (await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return DesignationOverrideStatus.NotFound;
            }
        }
        else
        {
            await using (var member = tx.Command(
                """
                SELECT EXISTS (SELECT FROM opportunity.document_set_snapshot_page p
                                WHERE p.workspace_id = @ws AND p.snapshot_id = @snapshot AND @doc = ANY (p.document_ids))
                """))
            {
                member.Parameters.AddWithValue("ws", workspaceId);
                member.Parameters.AddWithValue("snapshot", production.SnapshotId);
                member.Parameters.AddWithValue("doc", documentId);
                if (!(bool)(await member.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
                {
                    return DesignationOverrideStatus.NotMember;
                }
            }

            await using var upsert = tx.Command(
                """
                INSERT INTO opportunity.production_designation_override
                    (workspace_id, production_id, document_id, field_id, choice_id, reason, created_by)
                VALUES (@ws, @id, @doc, @field, @choice, @reason, @by)
                ON CONFLICT (workspace_id, production_id, document_id) DO UPDATE
                   SET field_id = EXCLUDED.field_id, choice_id = EXCLUDED.choice_id, reason = EXCLUDED.reason,
                       created_by = EXCLUDED.created_by, created_at = now()
                """);
            upsert.Parameters.AddWithValue("ws", workspaceId);
            upsert.Parameters.AddWithValue("id", productionId);
            upsert.Parameters.AddWithValue("doc", documentId);
            upsert.Parameters.AddWithValue("field", fieldId);
            upsert.Parameters.Add(Nullable("choice", NpgsqlDbType.Integer, choiceId));
            upsert.Parameters.AddWithValue("reason", reason.Trim());
            upsert.Parameters.AddWithValue("by", userId);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // An override changes what the draft would produce, like a specification change.
        await using (var touch = tx.Command(
            "UPDATE opportunity.production SET row_version = row_version + 1, modified_at = now() WHERE workspace_id = @ws AND production_id = @id"))
        {
            touch.Parameters.AddWithValue("ws", workspaceId);
            touch.Parameters.AddWithValue("id", productionId);
            await touch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AuditSql.InsertAsync(tx, audit with
        {
            WorkspaceId = workspaceId,
            ResourceType = AuditTaxonomy.Production.ResourceType,
            ResourceId = productionId.ToString(),
            SnapshotId = production.SnapshotId,
        }, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return DesignationOverrideStatus.Applied;
    }

    /// <summary>
    /// Freezes every member's designation inside the finalization transaction; null when they were frozen, else the
    /// reason the finalization is refused (nothing is counted in it, Q-52).
    /// </summary>
    private static async Task<(string? Refusal, DesignationFreezeSummary Summary)> FreezeDesignationsAsync(
        WorkspaceTransaction tx, Guid productionId, DesignationPlan plan, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            $"""
            WITH {ComputedDesignations},
            frozen AS (
                UPDATE opportunity.production_document pd
                   SET designation_choice_id = c.choice_id, designation = c.legend, designation_source = c.source
                  FROM computed c
                 WHERE pd.workspace_id = @ws AND pd.production_id = @id AND pd.sequence = c.sequence
                RETURNING c.source, c.unlisted, c.legend)
            SELECT count(*) FILTER (WHERE unlisted), count(*) FILTER (WHERE legend <> ''),
                   count(*) FILTER (WHERE source = 1), count(*) FILTER (WHERE source = 2), count(*) FILTER (WHERE source = 3)
              FROM frozen
            """);
        AddDesignationParameters(command, tx.WorkspaceId, productionId, plan, 0, null);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var unlisted = reader.GetInt64(0);
        var summary = new DesignationFreezeSummary(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
        if (unlisted > 0)
        {
            return ("Documents in this production carry a confidentiality designation that the specification's designation levels do not list. "
                + "Update the specification so it lists every designation, then finalize.", summary);
        }

        if (summary.Designated > 0 && !plan.StampsDesignation)
        {
            return ("Documents in this production are designated, but no endorsement stamps the designation. Add an endorsement with "
                + "{confidentiality} so every page of a designated document carries it.", summary);
        }

        return (null, summary);
    }

    private static void AddDesignationParameters(
        NpgsqlCommand command, Guid workspaceId, Guid productionId, DesignationPlan plan, long afterSequence, int? limit)
    {
        var levels = plan.Levels.OrderBy(l => l.Rank).ToList();
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", productionId);
        command.Parameters.AddWithValue("after", afterSequence);
        command.Parameters.Add(Nullable("limit", NpgsqlDbType.Bigint, limit is { } l ? (long)l : null));
        command.Parameters.Add(Nullable("field", NpgsqlDbType.Integer, plan.FieldId));
        command.Parameters.Add(new NpgsqlParameter("choices", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = levels.Select(l => l.ChoiceId).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("legends", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = levels.Select(l => l.Legend).ToArray() });
        command.Parameters.AddWithValue("family", plan.Rule == DesignationFamilyRule.HighestInFamily);
    }

    private static async Task<DesignationOverrideRow?> ReadOverrideAsync(
        WorkspaceTransaction tx, Guid productionId, Guid documentId, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            """
            SELECT document_id, field_id, choice_id, reason, created_by, created_at FROM opportunity.production_designation_override
             WHERE workspace_id = @ws AND production_id = @id AND document_id = @doc
            """);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("id", productionId);
        command.Parameters.AddWithValue("doc", documentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new DesignationOverrideRow(reader.GetGuid(0), reader.GetInt32(1), NullableInt32(reader, 2), reader.GetString(3), reader.GetGuid(4),
                reader.GetFieldValue<DateTimeOffset>(5))
            : null;
    }

    private static int? NullableInt32(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
}
