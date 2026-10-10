using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit;
using Opportunity.Application.Productions;
using Opportunity.Core.Pages;
using Opportunity.Core.Redactions;
using Opportunity.Core.Storage;
using Opportunity.Data.Audit;
using Opportunity.Data.Coding;

namespace Opportunity.Data.Productions;

/// <summary>
/// The production QC gate's checks in PostgreSQL (E12-T07, V0058). Every check is one set-based statement over the
/// production's members as they are now (the state finalization is about to freeze) that writes its document-level
/// exceptions into <c>production_qc_exception</c>; the run's counts come from those rows, the statuses and the report
/// from <see cref="ProductionQcRules.Evaluate"/>, and the run row is written last (the reference is deferred). The
/// privilege checks live with the coding store (<see cref="CodingRepository"/>), the only code that names its tables.
/// </summary>
public sealed partial class ProductionRepository
{
    private static readonly short[] ImagedPurposes = [(short)PageImagePurpose.Original, (short)PageImagePurpose.Review];

    public async Task<ProductionWriteResult> RunQcAsync(
        Guid workspaceId, Guid productionId, ProductionQcRequest qc, AuditEvent qcAudit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(qc);
        ArgumentNullException.ThrowIfNull(qcAudit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var production = await ReadOneAsync(tx, productionId, forUpdate: false, cancellationToken).ConfigureAwait(false);
        if (production is null)
        {
            return new ProductionWriteResult(ProductionWriteStatus.NotFound);
        }

        if (production.Status != ProductionStatus.Draft || production.BatesState != BatesAllocationState.Allocated)
        {
            return new ProductionWriteResult(ProductionWriteStatus.InvalidState, production,
                Reason: production.Status == ProductionStatus.Draft
                    ? "Allocate Bates numbers before running the QC checks."
                    : "The production is not a draft; its finalization's QC report is kept with it.");
        }

        var (set, refusal) = await ResolveRedactionSetAsync(tx, qc.RedactionSetId, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return new ProductionWriteResult(ProductionWriteStatus.InvalidState, production, Reason: refusal);
        }

        var result = await RunQcInTransactionAsync(tx, production, qc, set, qcAudit, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ProductionWriteResult(ProductionWriteStatus.Applied, production, Qc: result);
    }

    public async Task<ProductionQcRunRecord?> GetLatestQcRunAsync(Guid workspaceId, Guid productionId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT qc_run_id, production_id, purpose, outcome, run_by, run_at, report, report_sha256
              FROM opportunity.production_qc_run
             WHERE workspace_id = @ws AND production_id = @id
             ORDER BY run_at DESC, qc_run_id DESC
             LIMIT 1
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", productionId);
        ProductionQcRunRecord? run = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                run = new ProductionQcRunRecord(reader.GetGuid(0), reader.GetGuid(1), (ProductionQcPurpose)reader.GetInt16(2),
                    (ProductionQcOutcome)reader.GetInt16(3), reader.GetGuid(4), reader.GetFieldValue<DateTimeOffset>(5), reader.GetString(6),
                    reader.GetFieldValue<byte[]>(7));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return run;
    }

    public async Task<IReadOnlyList<ProductionQcExceptionRow>> ReadQcExceptionsAsync(
        Guid workspaceId, Guid qcRunId, ProductionQcCheck? check, ProductionQcExceptionCursor? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT e.check_code, e.sequence, e.document_id, d.control_number, pd.prod_beg_bates, pd.prod_end_bates, e.detail
              FROM opportunity.production_qc_exception e
              JOIN opportunity.production_qc_run r ON r.workspace_id = e.workspace_id AND r.qc_run_id = e.qc_run_id
              LEFT JOIN opportunity.production_document pd
                     ON pd.workspace_id = e.workspace_id AND pd.production_id = r.production_id AND pd.sequence = e.sequence
                    AND pd.document_id = e.document_id
              LEFT JOIN opportunity.document d ON d.workspace_id = e.workspace_id AND d.document_id = e.document_id
             WHERE e.workspace_id = @ws AND e.qc_run_id = @run
               AND (@check::smallint IS NULL OR e.check_code = @check)
               AND (e.check_code, e.sequence) > (@afterCheck, @afterSequence)
             ORDER BY e.check_code, e.sequence
             LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("run", qcRunId);
        command.Parameters.Add(Nullable("check", NpgsqlDbType.Smallint, check is { } c ? (short)c : null));
        command.Parameters.AddWithValue("afterCheck", after?.Check ?? (short)0);
        command.Parameters.AddWithValue("afterSequence", after?.Sequence ?? 0L);
        command.Parameters.AddWithValue("limit", limit);
        var rows = new List<ProductionQcExceptionRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new ProductionQcExceptionRow((ProductionQcCheck)reader.GetInt16(0), reader.GetInt64(1), reader.GetGuid(2), NullableString(reader, 3),
                    NullableString(reader, 4), NullableString(reader, 5), NullableString(reader, 6)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }

    /// <summary>
    /// The production's Redaction Set: the named one, else the workspace's Default set (null when the workspace has none);
    /// a refusal when the named set does not exist.
    /// </summary>
    private static async Task<(Guid? Set, string? Refusal)> ResolveRedactionSetAsync(WorkspaceTransaction tx, Guid? redactionSetId, CancellationToken cancellationToken)
    {
        await using var resolve = tx.Command(
            """
            SELECT redaction_set_id FROM opportunity.redaction_set
             WHERE workspace_id = @ws AND (redaction_set_id = @set OR (@set::uuid IS NULL AND lower(name) = lower(@default)))
             ORDER BY created_at
             LIMIT 1
            """);
        resolve.Parameters.AddWithValue("ws", tx.WorkspaceId);
        resolve.Parameters.Add(Nullable("set", NpgsqlDbType.Uuid, redactionSetId));
        resolve.Parameters.AddWithValue("default", RedactionDefaults.SetName);
        var set = await resolve.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
        return redactionSetId is not null && set is null
            ? (null, "The Redaction Set named by the specification does not exist. Choose an existing Redaction Set, then finalize.")
            : (set, null);
    }

    /// <summary>
    /// Runs every check of the gate in the caller's transaction, writes the exceptions, the run row (with the canonical
    /// report) and <c>Production.QcRun</c>, and returns the result.
    /// </summary>
    private static async Task<ProductionQcResult> RunQcInTransactionAsync(
        WorkspaceTransaction tx, ProductionRecord production, ProductionQcRequest qc, Guid? redactionSet, AuditEvent qcAudit, CancellationToken cancellationToken)
    {
        var productionId = production.ProductionId;
        await CodingRepository.InsertProductionQcPrivilegeExceptionsAsync(tx, productionId, qc.QcRunId, redactionSet, cancellationToken).ConfigureAwait(false);

        await using (var members = tx.Command(
            """
            WITH members AS (
                SELECT pd.sequence, pd.document_id, pd.output, pd.page_count, pd.beg_number, pd.end_number, d.active_page_set_id, d.family_id,
                       d.native_object_id, d.text_object_id, coalesce(st.active_count, 0) AS redactions, st.current_version
                  FROM opportunity.production_document pd
                  JOIN opportunity.document d ON d.workspace_id = pd.workspace_id AND d.document_id = pd.document_id
                  LEFT JOIN opportunity.document_redaction_state st
                         ON st.workspace_id = pd.workspace_id AND st.document_id = pd.document_id AND st.redaction_set_id = @set
                 WHERE pd.workspace_id = @ws AND pd.production_id = @id),
            exceptions AS (
                -- 4 RedactedNative (burn-in NativeShipped, Q-22).
                SELECT 4 AS check_code, m.sequence, m.document_id, @nativeShipped AS detail FROM members m
                 WHERE m.output = 2 AND m.redactions > 0
                UNION ALL
                -- 5 RedactionsNotProducible: a current redaction on another page set, or beyond the page set (burn-in PageMissing).
                SELECT 5, m.sequence, m.document_id,
                       CASE WHEN bool_or(r.page_set_id IS DISTINCT FROM m.active_page_set_id) THEN 'AnotherPageSet' ELSE @pageMissing END
                  FROM members m
                  LEFT JOIN opportunity.page_set ps ON ps.workspace_id = @ws AND ps.page_set_id = m.active_page_set_id
                  CROSS JOIN LATERAL (
                      SELECT DISTINCT ON (rr.redaction_id) rr.page_set_id, rr.ordinal, rr.operation
                        FROM opportunity.redaction_revision rr
                       WHERE rr.workspace_id = @ws AND rr.redaction_set_id = @set AND rr.document_id = m.document_id
                         AND rr.redaction_version <= m.current_version
                       ORDER BY rr.redaction_id, rr.redaction_version DESC) r
                 WHERE m.output = 1 AND m.redactions > 0 AND r.operation <> 3
                   AND (r.page_set_id IS DISTINCT FROM m.active_page_set_id OR r.ordinal > coalesce(ps.page_count, 0))
                 GROUP BY m.sequence, m.document_id
                UNION ALL
                -- 6 RenderFailure: pages without a stored image (Technical Issue pages), or a native member without its native.
                SELECT 6, m.sequence, m.document_id,
                       CASE WHEN ps.page_set_id IS NULL OR ps.page_count = 0 THEN 'NoPages'
                            ELSE 'PagesWithoutImage:' || (ps.page_count - img.imaged)::text END
                  FROM members m
                  LEFT JOIN opportunity.page_set ps ON ps.workspace_id = @ws AND ps.page_set_id = m.active_page_set_id
                  LEFT JOIN LATERAL (
                      SELECT count(*) AS imaged FROM opportunity.page p
                       WHERE p.workspace_id = @ws AND p.page_set_id = ps.page_set_id AND NOT p.image_missing
                         AND EXISTS (SELECT FROM opportunity.page_image pi
                                       JOIN opportunity.stored_object so
                                         ON so.workspace_id = pi.workspace_id AND so.object_id = pi.object_id AND so.state = @committed
                                      WHERE pi.workspace_id = p.workspace_id AND pi.page_set_id = p.page_set_id AND pi.ordinal = p.ordinal
                                        AND pi.purpose = ANY(@purposes))) img ON true
                 WHERE m.output = 1 AND (ps.page_set_id IS NULL OR ps.page_count = 0 OR img.imaged < ps.page_count)
                UNION ALL
                SELECT 6, m.sequence, m.document_id, 'NativeMissing' FROM members m
                 WHERE m.output = 2 AND NOT EXISTS (
                     SELECT FROM opportunity.stored_object n
                      WHERE n.workspace_id = @ws AND n.object_id = m.native_object_id AND n.area = @native AND n.state = @committed)
                UNION ALL
                -- 7 PageCountChanged: the Bates span was planned for another page count.
                SELECT 7, m.sequence, m.document_id, 'Allocated:' || m.page_count::text || ';Now:' || coalesce(ps.page_count, 0)::text
                  FROM members m
                  LEFT JOIN opportunity.page_set ps ON ps.workspace_id = @ws AND ps.page_set_id = m.active_page_set_id
                 WHERE m.output = 1 AND coalesce(ps.page_count, 0) <> m.page_count
                UNION ALL
                -- 8 BatesOverlap: the member's numbers lie in another production's live range of the prefix.
                SELECT 8, m.sequence, m.document_id, left(string_agg(DISTINCT p.name, '; '), 500)
                  FROM members m
                  JOIN opportunity.bates_range r
                    ON r.workspace_id = @ws AND r.bates_prefix_key = @key AND r.state <> 4 AND r.production_id <> @id
                   AND r.first_number <= m.end_number AND r.last_number >= m.beg_number
                  JOIN opportunity.production p ON p.workspace_id = r.workspace_id AND p.production_id = r.production_id
                 WHERE m.beg_number IS NOT NULL
                 GROUP BY m.sequence, m.document_id
                UNION ALL
                -- 11 Inaccessible: the person finalizing failed the per-document re-check (Q-15).
                SELECT 11, u.sequence, u.document_id, NULL FROM unnest(@deniedSequences, @deniedDocuments) AS u(sequence, document_id)
                UNION ALL
                -- 12 IncompleteFamily (warning): a live family member is not in the production.
                SELECT 12, m.sequence, m.document_id, NULL FROM members m
                 WHERE m.family_id IS NOT NULL AND EXISTS (
                     SELECT FROM opportunity.document f
                       JOIN opportunity.document_projection_state fs
                         ON fs.workspace_id = f.workspace_id AND fs.document_id = f.document_id AND NOT fs.is_deleted
                      WHERE f.workspace_id = @ws AND f.family_id = m.family_id
                        AND NOT EXISTS (SELECT FROM opportunity.production_document x
                                         WHERE x.workspace_id = @ws AND x.production_id = @id AND x.document_id = f.document_id))
                UNION ALL
                -- 13 TextMissing (warning): text is delivered, the member has none and would ship its own (not redacted, not a placeholder).
                SELECT 13, m.sequence, m.document_id, NULL FROM members m
                 WHERE @includeText AND m.output IN (1, 2) AND m.redactions = 0 AND NOT EXISTS (
                     SELECT FROM opportunity.stored_object t
                      WHERE t.workspace_id = @ws AND t.object_id = m.text_object_id AND t.area = @text AND t.state = @committed))
            INSERT INTO opportunity.production_qc_exception (workspace_id, qc_run_id, check_code, sequence, document_id, detail)
            SELECT @ws, @run, e.check_code, e.sequence, e.document_id, e.detail FROM exceptions e
            ON CONFLICT DO NOTHING
            """))
        {
            members.Parameters.AddWithValue("ws", tx.WorkspaceId);
            members.Parameters.AddWithValue("id", productionId);
            members.Parameters.AddWithValue("run", qc.QcRunId);
            members.Parameters.AddWithValue("key", production.BatesPrefixKey);
            members.Parameters.Add(Nullable("set", NpgsqlDbType.Uuid, redactionSet));
            members.Parameters.AddWithValue("nativeShipped", BurnInCodes.NativeShipped);
            members.Parameters.AddWithValue("pageMissing", BurnInCodes.PageMissing);
            members.Parameters.AddWithValue("committed", (short)StoredObjectState.Committed);
            members.Parameters.AddWithValue("native", (short)ObjectArea.Native);
            members.Parameters.AddWithValue("text", (short)ObjectArea.Text);
            members.Parameters.Add(new NpgsqlParameter<short[]>("purposes", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { TypedValue = ImagedPurposes });
            members.Parameters.AddWithValue("includeText", qc.IncludeText);
            members.Parameters.AddWithValue("deniedSequences", qc.Inaccessible.Select(m => m.Sequence).ToArray());
            members.Parameters.AddWithValue("deniedDocuments", qc.Inaccessible.Select(m => m.DocumentId).ToArray());
            await members.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // 10 DesignationNotProducible and 14 BlankConfidentiality (warning), from the designation rule finalization freezes.
        await using (var designations = tx.Command(
            $"""
            WITH {ComputedDesignations}
            INSERT INTO opportunity.production_qc_exception (workspace_id, qc_run_id, check_code, sequence, document_id, detail)
            SELECT @ws, @run, 10, c.sequence, c.document_id, CASE WHEN c.unlisted THEN 'UnlistedLevel' ELSE 'NotStamped' END
              FROM computed c
             WHERE c.unlisted OR (c.legend <> '' AND NOT @stamps)
            UNION ALL
            SELECT @ws, @run, 14, c.sequence, c.document_id, NULL
              FROM computed c
             WHERE @field::integer IS NOT NULL AND c.choice_id IS NULL AND NOT c.unlisted
            """))
        {
            AddDesignationParameters(designations, tx.WorkspaceId, productionId, qc.Designations, 0, null);
            designations.Parameters.AddWithValue("run", qc.QcRunId);
            designations.Parameters.AddWithValue("stamps", qc.Designations.StampsDesignation);
            await designations.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var counts = new Dictionary<ProductionQcCheck, long>();
        await using (var count = tx.Command(
            "SELECT check_code, count(*) FROM opportunity.production_qc_exception WHERE workspace_id = @ws AND qc_run_id = @run GROUP BY check_code"))
        {
            count.Parameters.AddWithValue("ws", tx.WorkspaceId);
            count.Parameters.AddWithValue("run", qc.QcRunId);
            await using var reader = await count.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                counts[(ProductionQcCheck)reader.GetInt16(0)] = reader.GetInt64(1);
            }
        }

        var result = ProductionQcRules.Evaluate(production, qc, counts);
        await using (var run = tx.Command(
            """
            INSERT INTO opportunity.production_qc_run (workspace_id, qc_run_id, production_id, purpose, outcome, run_by, run_at, report, report_sha256)
            VALUES (@ws, @run, @id, @purpose, @outcome, @by, @at, @report, @sha)
            """))
        {
            run.Parameters.AddWithValue("ws", tx.WorkspaceId);
            run.Parameters.AddWithValue("run", qc.QcRunId);
            run.Parameters.AddWithValue("id", productionId);
            run.Parameters.AddWithValue("purpose", (short)qc.Purpose);
            run.Parameters.AddWithValue("outcome", (short)result.Outcome);
            run.Parameters.AddWithValue("by", qc.RunBy);
            run.Parameters.AddWithValue("at", qc.RunAt);
            run.Parameters.AddWithValue("report", result.ReportJson);
            run.Parameters.AddWithValue("sha", result.ReportSha256);
            await run.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AuditSql.InsertAsync(tx, Resource(qcAudit, tx.WorkspaceId, productionId, AuditTaxonomy.Production.QcRun) with
        {
            Details = ProductionQcRules.AuditDetails(result),
            Outcome = result.Passed ? AuditOutcome.Success : AuditOutcome.Failure,
            ReasonCode = result.Passed ? null : "QcBlocked",
            SnapshotId = production.SnapshotId,
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }
}
