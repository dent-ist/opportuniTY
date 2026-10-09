using System.Globalization;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit;
using Opportunity.Application.Jobs;
using Opportunity.Application.Productions;
using Opportunity.Core.Jobs;
using Opportunity.Core.Productions;
using Opportunity.Data.Audit;
using Opportunity.Data.Coding;
using Opportunity.Data.Jobs;

namespace Opportunity.Data.Productions;

/// <summary>
/// PostgreSQL implementation of <see cref="IProductionStore"/> over <c>production</c>, <c>production_document</c> and
/// <c>bates_range</c> (V0038). Every write that checks or reserves Bates numbers first takes
/// <c>pg_advisory_xact_lock</c> on (workspace, prefix key), so two allocations of one prefix serialize and live ranges
/// never overlap; the database itself refuses changes to finalized productions (triggers).
/// </summary>
public sealed partial class ProductionRepository(NpgsqlDataSource dataSource) : IProductionStore
{
    private const int PlanBatch = 5_000;

    private const string Columns =
        """
        p.workspace_id, p.production_id, p.lineage_id, p.version, p.name, p.snapshot_id, p.specification, p.specification_sha256,
        p.bates_prefix, p.bates_suffix, p.bates_padding, p.bates_start, p.status, p.row_version, p.bates_state, p.bates_job_id,
        p.bates_reason, p.bates_first, p.bates_last, p.bates_documents, p.bates_units, p.assignments_sha256, p.integrity::text,
        p.manifest, p.manifest_sha256, p.created_by, p.created_by_display, p.created_by_groups, p.created_at, p.modified_at,
        p.finalized_at, p.finalized_by, p.voided_at, p.void_reason, p.discarded_at
        """;

    /// <summary>Productions whose numbers are issued or held: produced, voided, or an allocated (or allocating) draft.</summary>
    private const string LiveProduction = "(p.status IN (2, 3) OR (p.status = 1 AND p.bates_state IN (1, 2)))";

    public async Task<ProductionWriteResult> CreateAsync(NewProduction request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ws = request.WorkspaceId;
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, ws, cancellationToken).ConfigureAwait(false);
        var lineage = request.ProductionId;
        var version = 1;
        if (request.PreviousVersionId is { } previousId)
        {
            var previous = await ReadOneAsync(tx, previousId, forUpdate: true, cancellationToken).ConfigureAwait(false);
            if (previous is null)
            {
                return new ProductionWriteResult(ProductionWriteStatus.NotFound);
            }

            if (previous.Status is not (ProductionStatus.Finalized or ProductionStatus.Voided))
            {
                return new ProductionWriteResult(ProductionWriteStatus.InvalidState, Reason: "Only a finalized or voided production gets a new version; edit the draft instead.");
            }

            lineage = previous.LineageId;
            await using var max = tx.Command("SELECT max(version) FROM opportunity.production WHERE workspace_id = @ws AND lineage_id = @lineage");
            max.Parameters.AddWithValue("ws", ws);
            max.Parameters.AddWithValue("lineage", lineage);
            version = (int)(await max.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! + 1;
        }

        var spec = request.Specification;
        var conflicts = await LockAndFindConflictsAsync(tx, request.ProductionId, BatesFormat.KeyOf(spec.BatesPrefix), spec.BatesStart,
            spec.BatesStart + Math.Max(1, spec.MinimumNumbers) - 1, cancellationToken).ConfigureAwait(false);
        if (conflicts.Count > 0)
        {
            return new ProductionWriteResult(ProductionWriteStatus.BatesConflict, Conflicts: conflicts);
        }

        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.production
                (workspace_id, production_id, lineage_id, version, name, snapshot_id, specification, specification_sha256, bates_prefix,
                 bates_prefix_key, bates_suffix, bates_padding, bates_start, created_by, created_by_display, created_by_groups)
            VALUES (@ws, @id, @lineage, @version, @name, @snapshot, @spec, @sha, @prefix, @key, @suffix, @padding, @start, @by, @display, @groups)
            """))
        {
            insert.Parameters.AddWithValue("ws", ws);
            insert.Parameters.AddWithValue("id", request.ProductionId);
            insert.Parameters.AddWithValue("lineage", lineage);
            insert.Parameters.AddWithValue("version", version);
            insert.Parameters.AddWithValue("name", request.Name.Trim());
            insert.Parameters.AddWithValue("snapshot", request.SnapshotId);
            AddSpecification(insert, spec);
            insert.Parameters.AddWithValue("by", request.CreatedBy);
            insert.Parameters.AddWithValue("display", Truncate(request.CreatedByDisplay, 512));
            insert.Parameters.AddWithValue("groups", request.CreatedByGroups.ToArray());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AuditSql.InsertAsync(tx, Resource(request.AuditTemplate, ws, request.ProductionId, AuditTaxonomy.Production.Created), cancellationToken)
            .ConfigureAwait(false);
        var record = (await ReadOneAsync(tx, request.ProductionId, forUpdate: false, cancellationToken).ConfigureAwait(false))!;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ProductionWriteResult(ProductionWriteStatus.Applied, record);
    }

    public async Task<ProductionRecord?> GetAsync(Guid workspaceId, Guid productionId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var record = await ReadOneAsync(tx, productionId, forUpdate: false, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<ProductionRecord?> GetByBatesJobAsync(Guid workspaceId, Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command($"SELECT {Columns} FROM opportunity.production p WHERE p.workspace_id = @ws AND p.bates_job_id = @job");
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("job", jobId);
        var records = await ReadManyAsync(command, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return records.Count == 1 ? records[0] : null;
    }

    public async Task<IReadOnlyList<ProductionRecord>> ListAsync(
        Guid workspaceId, ProductionListCursor? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            SELECT {Columns} FROM opportunity.production p
            WHERE p.workspace_id = @ws
              AND (@after_at::timestamptz IS NULL OR (p.created_at, p.production_id) < (@after_at, @after_id))
            ORDER BY p.created_at DESC, p.production_id DESC
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.Add(Nullable("after_at", NpgsqlDbType.TimestampTz, after?.CreatedAt));
        command.Parameters.Add(Nullable("after_id", NpgsqlDbType.Uuid, after?.ProductionId));
        command.Parameters.AddWithValue("limit", limit);
        var records = await ReadManyAsync(command, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return records;
    }

    public async Task<ProductionWriteResult> UpdateDraftAsync(
        Guid workspaceId, Guid productionId, long expectedRowVersion, string name, Guid snapshotId, ProductionSpecificationRow specification,
        AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        if (await EditableDraftAsync(tx, productionId, expectedRowVersion, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var conflicts = await LockAndFindConflictsAsync(tx, productionId, BatesFormat.KeyOf(specification.BatesPrefix), specification.BatesStart,
            specification.BatesStart + Math.Max(1, specification.MinimumNumbers) - 1, cancellationToken).ConfigureAwait(false);
        if (conflicts.Count > 0)
        {
            return new ProductionWriteResult(ProductionWriteStatus.BatesConflict, Conflicts: conflicts);
        }

        await ReleaseAllocationAsync(tx, productionId, cancellationToken).ConfigureAwait(false);
        await using (var update = tx.Command(
            """
            UPDATE opportunity.production
               SET name = @name, snapshot_id = @snapshot, specification = @spec, specification_sha256 = @sha, bates_prefix = @prefix,
                   bates_prefix_key = @key, bates_suffix = @suffix, bates_padding = @padding, bates_start = @start,
                   row_version = row_version + 1, modified_at = now()
             WHERE workspace_id = @ws AND production_id = @id
            """))
        {
            update.Parameters.AddWithValue("ws", workspaceId);
            update.Parameters.AddWithValue("id", productionId);
            update.Parameters.AddWithValue("name", name.Trim());
            update.Parameters.AddWithValue("snapshot", snapshotId);
            AddSpecification(update, specification);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AuditSql.InsertAsync(tx, Resource(audit, workspaceId, productionId, AuditTaxonomy.Production.Modified), cancellationToken).ConfigureAwait(false);
        var record = (await ReadOneAsync(tx, productionId, forUpdate: false, cancellationToken).ConfigureAwait(false))!;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ProductionWriteResult(ProductionWriteStatus.Applied, record);
    }

    public async Task<(ProductionWriteResult Result, JobInfo? Job)> StartAllocationAsync(
        Guid workspaceId, Guid productionId, NewJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var current = await ReadOneAsync(tx, productionId, forUpdate: true, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return (new ProductionWriteResult(ProductionWriteStatus.NotFound), null);
        }

        if (current.Status != ProductionStatus.Draft)
        {
            return (new ProductionWriteResult(ProductionWriteStatus.InvalidState, current, Reason: "Only a draft production is allocated."), null);
        }

        if (current.BatesState == BatesAllocationState.Allocating)
        {
            // Already allocating: the caller reads the running job.
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (new ProductionWriteResult(ProductionWriteStatus.Applied, current), null);
        }

        await using (var snapshot = tx.Command(
            "SELECT status, document_count FROM opportunity.document_set_snapshot WHERE workspace_id = @ws AND snapshot_id = @id"))
        {
            snapshot.Parameters.AddWithValue("ws", workspaceId);
            snapshot.Parameters.AddWithValue("id", current.SnapshotId);
            await using var reader = await snapshot.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetString(0) != "Ready")
            {
                return (new ProductionWriteResult(ProductionWriteStatus.InvalidState, current, Reason: "The frozen set is not Ready yet."), null);
            }

            if (reader.GetInt64(1) == 0)
            {
                return (new ProductionWriteResult(ProductionWriteStatus.InvalidState, current, Reason: "The frozen set has no documents to number."), null);
            }
        }

        var created = await JobRepository.CreateInTransactionAsync(tx, job with { TargetSnapshotId = current.SnapshotId }, cancellationToken)
            .ConfigureAwait(false);
        if (!created.Created)
        {
            // A retried request (same Idempotency-Key) whose job already exists: report it as it is.
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (new ProductionWriteResult(ProductionWriteStatus.Applied, current), created.Job);
        }

        await ReleaseAllocationAsync(tx, productionId, cancellationToken).ConfigureAwait(false);
        await using (var update = tx.Command(
            """
            UPDATE opportunity.production
               SET bates_state = 1, bates_job_id = @job, row_version = row_version + 1, modified_at = now()
             WHERE workspace_id = @ws AND production_id = @id
            """))
        {
            update.Parameters.AddWithValue("ws", workspaceId);
            update.Parameters.AddWithValue("id", productionId);
            update.Parameters.AddWithValue("job", created.Job.JobId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var record = (await ReadOneAsync(tx, productionId, forUpdate: false, cancellationToken).ConfigureAwait(false))!;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (new ProductionWriteResult(ProductionWriteStatus.Applied, record), created.Job);
    }

    public async Task<IReadOnlyList<ActiveBatesAllocation>> GetActiveAllocationsAsync(Guid workspaceId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            SELECT {Columns}, j.status, j.chunks_failed
            FROM opportunity.production p
            JOIN opportunity.job j ON j.workspace_id = p.workspace_id AND j.job_id = p.bates_job_id
            WHERE p.workspace_id = @ws AND p.bates_state = 1
            ORDER BY p.created_at, p.production_id
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("limit", limit);
        var active = new List<ActiveBatesAllocation>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                active.Add(new ActiveBatesAllocation(Read(reader), Enum.Parse<JobStatus>(reader.GetString(35)), reader.GetInt32(36)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return active;
    }

    public async Task<bool> TryClaimAsync(Guid workspaceId, Guid productionId, string owner, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            UPDATE opportunity.production
            SET claimed_by = @owner, claimed_until = now() + @lease
            WHERE workspace_id = @ws AND production_id = @id AND status = 1 AND bates_state = 1
              AND (claimed_by IS NULL OR claimed_by = @owner OR claimed_until < now())
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", productionId);
        command.Parameters.AddWithValue("owner", Truncate(owner, 200));
        command.Parameters.AddWithValue("lease", lease);
        var claimed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return claimed;
    }

    public async Task ReleaseClaimAsync(Guid workspaceId, Guid productionId, string owner, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            UPDATE opportunity.production SET claimed_by = NULL, claimed_until = NULL
            WHERE workspace_id = @ws AND production_id = @id AND claimed_by = @owner AND status = 1
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", productionId);
        command.Parameters.AddWithValue("owner", Truncate(owner, 200));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<BatesPlanResult> PlanAllocationAsync(
        Guid workspaceId, Guid productionId, Guid jobId, BatesPlanRequest plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var ws = workspaceId;
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, ws, cancellationToken).ConfigureAwait(false);
        var production = await ReadOneAsync(tx, productionId, forUpdate: true, cancellationToken).ConfigureAwait(false);
        if (production is null || production.Status != ProductionStatus.Draft || production.BatesState != BatesAllocationState.Allocating
            || production.BatesJobId != jobId)
        {
            return new BatesPlanResult([], 0, 0, [], "The production is not allocating with this job.");
        }

        if (production.BatesFirst is not null)
        {
            // Planned before (a worker died before starting the job): the stored plan is the plan.
            var stored = await ChunkRangesAsync(tx, productionId, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new BatesPlanResult(stored, production.BatesDocuments ?? 0, production.BatesUnits ?? 0, []);
        }

        await LockPrefixAsync(tx, production.BatesPrefixKey, cancellationToken).ConfigureAwait(false);
        await using (var order = tx.Command(
            """
            DELETE FROM opportunity.production_document WHERE workspace_id = @ws AND production_id = @id;

            CREATE TEMP TABLE production_plan ON COMMIT DROP AS
            SELECT row_number() OVER (ORDER BY m.family_first, m.family_sequence, m.ordinal) AS sequence,
                   m.document_id, m.ordinal, m.family_key, m.family_sequence, m.document_version, m.page_count, m.file_extension
              FROM (SELECT s.ordinal, s.document_id, s.baseline_version AS document_version, d.family_id AS family_key,
                           d.family_sequence, lower(d.file_extension) AS file_extension, coalesce(ps.page_count, 0) AS page_count,
                           min(s.ordinal) OVER (PARTITION BY d.family_id) AS family_first
                      FROM (SELECT p.first_ordinal + u.i - 1 AS ordinal, u.document_id, u.baseline_version
                              FROM opportunity.document_set_snapshot_page p
                             CROSS JOIN LATERAL unnest(p.document_ids, p.baseline_versions)
                                   WITH ORDINALITY AS u(document_id, baseline_version, i)
                             WHERE p.workspace_id = @ws AND p.snapshot_id = @snapshot) s
                      JOIN opportunity.document d ON d.workspace_id = @ws AND d.document_id = s.document_id
                      LEFT JOIN opportunity.page_set ps ON ps.workspace_id = d.workspace_id AND ps.page_set_id = d.active_page_set_id) m;

            DECLARE production_plan_cursor NO SCROLL CURSOR FOR
            SELECT sequence, document_id, ordinal, family_key, family_sequence, document_version, page_count, file_extension
              FROM production_plan ORDER BY sequence;
            """))
        {
            order.Parameters.AddWithValue("ws", ws);
            order.Parameters.AddWithValue("id", productionId);
            order.Parameters.AddWithValue("snapshot", production.SnapshotId);
            await order.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var planner = new BatesPlanner(plan.Level, plan.DocumentsPerChunk, plan.UnitsPerChunk);
        while (true)
        {
            var batch = new List<(BatesMember Member, long Ordinal, int FamilySequence, long Version, string? Extension, BatesPlannedMember Planned)>(PlanBatch);
            await using (var fetch = tx.Command($"FETCH {PlanBatch} FROM production_plan_cursor"))
            await using (var reader = await fetch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var extension = reader.IsDBNull(7) ? null : reader.GetString(7);
                    var member = new BatesMember(reader.GetInt64(0), reader.GetGuid(1), reader.GetGuid(3), plan.OutputFor(extension), reader.GetInt32(6));
                    batch.Add((member, reader.GetInt64(2), reader.GetInt32(4), reader.GetInt64(5), extension, planner.Add(member)));
                }
            }

            if (batch.Count > 0)
            {
                await using var insert = tx.Command(
                    """
                    INSERT INTO opportunity.production_document
                        (workspace_id, production_id, sequence, document_id, snapshot_ordinal, family_key, family_sequence, document_version,
                         page_count, file_extension, output, units, first_offset, chunk_sequence)
                    SELECT @ws, @id, u.sequence, u.document_id, u.ordinal, u.family_key, u.family_sequence, u.document_version,
                           u.page_count, u.file_extension, u.output, u.units, u.first_offset, u.chunk_sequence
                      FROM unnest(@sequence, @document, @ordinal, @family, @family_sequence, @version, @pages, @extension, @output, @units,
                                  @offset, @chunk)
                           AS u(sequence, document_id, ordinal, family_key, family_sequence, document_version, page_count, file_extension,
                                output, units, first_offset, chunk_sequence)
                    """);
                insert.Parameters.AddWithValue("ws", ws);
                insert.Parameters.AddWithValue("id", productionId);
                insert.Parameters.AddWithValue("sequence", batch.Select(b => b.Member.Sequence).ToArray());
                insert.Parameters.AddWithValue("document", batch.Select(b => b.Member.DocumentId).ToArray());
                insert.Parameters.AddWithValue("ordinal", batch.Select(b => b.Ordinal).ToArray());
                insert.Parameters.AddWithValue("family", batch.Select(b => b.Member.FamilyKey).ToArray());
                insert.Parameters.AddWithValue("family_sequence", batch.Select(b => b.FamilySequence).ToArray());
                insert.Parameters.AddWithValue("version", batch.Select(b => b.Version).ToArray());
                insert.Parameters.AddWithValue("pages", batch.Select(b => b.Member.PageCount).ToArray());
                insert.Parameters.Add(new NpgsqlParameter<string?[]>("extension", NpgsqlDbType.Array | NpgsqlDbType.Text)
                {
                    TypedValue = [.. batch.Select(b => b.Extension)],
                });
                insert.Parameters.AddWithValue("output", batch.Select(b => (short)b.Member.Output).ToArray());
                insert.Parameters.AddWithValue("units", batch.Select(b => b.Planned.Units).ToArray());
                insert.Parameters.AddWithValue("offset", batch.Select(b => b.Planned.FirstOffset).ToArray());
                insert.Parameters.AddWithValue("chunk", batch.Select(b => b.Planned.ChunkSequence).ToArray());
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (batch.Count < PlanBatch)
            {
                break;
            }
        }

        if (planner.Documents == 0)
        {
            return new BatesPlanResult([], 0, 0, [], "None of the frozen set's documents exist any more; there is nothing to number.");
        }

        var first = production.BatesStart;
        var last = first + planner.TotalUnits - 1;
        if (last > plan.MaxNumber)
        {
            return new BatesPlanResult([], planner.Documents, planner.TotalUnits, [], string.Create(CultureInfo.InvariantCulture,
                $"The production needs {planner.TotalUnits} numbers from {first}; they do not fit {production.BatesPadding} digits."));
        }

        var conflicts = await FindConflictsAsync(tx, productionId, production.BatesPrefixKey, first, last, cancellationToken).ConfigureAwait(false);
        if (conflicts.Count > 0)
        {
            return new BatesPlanResult([], planner.Documents, planner.TotalUnits, conflicts);
        }

        await using (var reserve = tx.Command(
            """
            INSERT INTO opportunity.bates_range (workspace_id, range_id, production_id, bates_prefix_key, first_number, last_number)
            VALUES (@ws, @range, @id, @key, @first, @last);

            UPDATE opportunity.production
               SET bates_first = @first, bates_last = @last, bates_documents = @documents, bates_units = @units
             WHERE workspace_id = @ws AND production_id = @id;
            """))
        {
            reserve.Parameters.AddWithValue("ws", ws);
            reserve.Parameters.AddWithValue("range", Guid.CreateVersion7());
            reserve.Parameters.AddWithValue("id", productionId);
            reserve.Parameters.AddWithValue("key", production.BatesPrefixKey);
            reserve.Parameters.AddWithValue("first", first);
            reserve.Parameters.AddWithValue("last", last);
            reserve.Parameters.AddWithValue("documents", planner.Documents);
            reserve.Parameters.AddWithValue("units", planner.TotalUnits);
            await reserve.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var chunks = await ChunkRangesAsync(tx, productionId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new BatesPlanResult(chunks, planner.Documents, planner.TotalUnits, []);
    }

    public async Task<IReadOnlyList<ProductionSliceRow>> ReadSliceAsync(
        Guid workspaceId, Guid productionId, long sequenceFrom, long sequenceTo, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT sequence, document_id, family_key, units, first_offset
            FROM opportunity.production_document
            WHERE workspace_id = @ws AND production_id = @id AND sequence BETWEEN @from AND @to
            ORDER BY sequence
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", productionId);
        command.Parameters.AddWithValue("from", sequenceFrom);
        command.Parameters.AddWithValue("to", sequenceTo);
        var rows = new List<ProductionSliceRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new ProductionSliceRow(reader.GetInt64(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetInt32(3), reader.GetInt64(4)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }

    public async Task<ChunkCommitResult> ApplyChunkAsync(
        ClaimedChunk chunk, Guid productionId, IReadOnlyList<BatesAssignment> assignments, ChunkCompletion completion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(completion);
        var lease = chunk.Lease;
        var membership = chunk.Membership;
        if (chunk.JobType != JobType.Production || membership.Kind != ChunkMembershipKind.SnapshotRange
            || assignments.Any(a => a.Sequence < membership.RangeFrom || a.Sequence > membership.RangeTo)
            || assignments.Select(a => a.Sequence).Distinct().Count() != assignments.Count)
        {
            throw new ArgumentException("A Bates chunk write covers distinct members of a leased Production chunk.", nameof(assignments));
        }

        ChunkCommitResult commit;
        await using (var tx = await WorkspaceTransaction.BeginAsync(dataSource, lease.WorkspaceId, cancellationToken).ConfigureAwait(false))
        {
            var production = await ReadOneAsync(tx, productionId, forUpdate: false, cancellationToken).ConfigureAwait(false);
            if (production is null || production.BatesJobId != lease.JobId)
            {
                throw new ArgumentException("The chunk's production does not exist or is allocated by another job.", nameof(productionId));
            }

            if (production.Status == ProductionStatus.Draft && production.BatesState == BatesAllocationState.Allocating && assignments.Count > 0)
            {
                await using var update = tx.Command(
                    """
                    UPDATE opportunity.production_document d
                       SET beg_number = u.beg, end_number = u.end_number, prod_beg_bates = u.beg_bates, prod_end_bates = u.end_bates,
                           prod_beg_attach = u.beg_attach, prod_end_attach = u.end_attach
                      FROM unnest(@sequence, @document, @beg, @end, @beg_bates, @end_bates, @beg_attach, @end_attach)
                           AS u(sequence, document_id, beg, end_number, beg_bates, end_bates, beg_attach, end_attach)
                     WHERE d.workspace_id = @ws AND d.production_id = @id AND d.sequence = u.sequence AND d.document_id = u.document_id
                    """);
                update.Parameters.AddWithValue("ws", lease.WorkspaceId);
                update.Parameters.AddWithValue("id", productionId);
                update.Parameters.AddWithValue("sequence", assignments.Select(a => a.Sequence).ToArray());
                update.Parameters.AddWithValue("document", assignments.Select(a => a.DocumentId).ToArray());
                update.Parameters.AddWithValue("beg", assignments.Select(a => a.BegNumber).ToArray());
                update.Parameters.AddWithValue("end", assignments.Select(a => a.EndNumber).ToArray());
                update.Parameters.AddWithValue("beg_bates", assignments.Select(a => a.ProdBegBates).ToArray());
                update.Parameters.AddWithValue("end_bates", assignments.Select(a => a.ProdEndBates).ToArray());
                update.Parameters.AddWithValue("beg_attach", assignments.Select(a => a.ProdBegAttach).ToArray());
                update.Parameters.AddWithValue("end_attach", assignments.Select(a => a.ProdEndAttach).ToArray());
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != assignments.Count)
                {
                    throw new InvalidOperationException("The chunk's members no longer match the production's plan.");
                }
            }

            // Fence F3 after the chunk's own writes.
            commit = await JobChunkRepository.CommitInTransactionAsync(tx, lease, completion, cancellationToken).ConfigureAwait(false);
            if (commit.Committed)
            {
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                return commit;
            }
        }

        // Fence F3 refused: everything rolled back with the transaction; record the fence outcome on the chunk.
        if (commit.Outcome != ChunkCommitOutcome.LeaseLost)
        {
            var release = await new JobChunkRepository(dataSource).ReleaseAsync(lease, cancellationToken).ConfigureAwait(false);
            commit = release switch
            {
                ChunkReleaseOutcome.Cancelled => commit with { Outcome = ChunkCommitOutcome.Cancelled },
                ChunkReleaseOutcome.ReturnedToPending => commit with { Outcome = ChunkCommitOutcome.JobNotRunning },
                _ => commit with { Outcome = ChunkCommitOutcome.LeaseLost },
            };
        }

        return commit;
    }

    public async Task<BatesIntegrityReport> CheckIntegrityAsync(Guid workspaceId, Guid productionId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, System.Data.IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);
        var production = await ReadOneAsync(tx, productionId, forUpdate: false, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Production {productionId} does not exist.");
        var problems = new List<string>();
        long documents, numbers, placeholders, natives, unassigned, gaps, overlaps, misplaced;
        long? firstBeg, lastEnd;
        await using (var totals = tx.Command(
            """
            SELECT count(*), coalesce(sum(units), 0), count(*) FILTER (WHERE output = 3), count(*) FILTER (WHERE output = 2),
                   count(*) FILTER (WHERE beg_number IS NULL),
                   count(*) FILTER (WHERE prev_end IS NOT NULL AND beg_number > prev_end + 1),
                   count(*) FILTER (WHERE prev_end IS NOT NULL AND beg_number <= prev_end),
                   count(*) FILTER (WHERE beg_number IS DISTINCT FROM @start + first_offset),
                   min(beg_number) FILTER (WHERE sequence = 1), max(end_number)
              FROM (SELECT d.*, lag(d.end_number) OVER (ORDER BY d.sequence) AS prev_end
                      FROM opportunity.production_document d
                     WHERE d.workspace_id = @ws AND d.production_id = @id) x
            """))
        {
            totals.Parameters.AddWithValue("ws", workspaceId);
            totals.Parameters.AddWithValue("id", productionId);
            totals.Parameters.AddWithValue("start", production.BatesStart);
            await using var reader = await totals.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            documents = reader.GetInt64(0);
            numbers = reader.GetInt64(1);
            placeholders = reader.GetInt64(2);
            natives = reader.GetInt64(3);
            unassigned = reader.GetInt64(4);
            gaps = reader.GetInt64(5);
            overlaps = reader.GetInt64(6);
            misplaced = reader.GetInt64(7);
            firstBeg = reader.IsDBNull(8) ? null : reader.GetInt64(8);
            lastEnd = reader.IsDBNull(9) ? null : reader.GetInt64(9);
        }

        void Problem(long count, string what)
        {
            if (count > 0)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{count} {what}"));
            }
        }

        Problem(unassigned, "document(s) have no Bates number.");
        Problem(gaps, "unattributed gap(s) between consecutive documents.");
        Problem(overlaps, "document(s) overlap the previous document's numbers.");
        Problem(misplaced, "document(s) are not numbered at their planned position.");
        if (documents != production.BatesDocuments || numbers != production.BatesUnits)
        {
            problems.Add("The members do not match the planned document and number counts.");
        }

        if (documents > 0 && (firstBeg != production.BatesFirst || lastEnd != production.BatesLast))
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"The numbers run {firstBeg}–{lastEnd}, not the reserved {production.BatesFirst}–{production.BatesLast}."));
        }

        await using (var families = tx.Command(
            """
            SELECT (SELECT count(*) FROM (SELECT family_key FROM opportunity.production_document
                                           WHERE workspace_id = @ws AND production_id = @id
                                           GROUP BY family_key HAVING max(sequence) - min(sequence) + 1 <> count(*)) f),
                   (SELECT count(*) FROM (SELECT d.prod_beg_attach, d.prod_end_attach,
                                                 first_value(d.prod_beg_bates) OVER w AS family_beg,
                                                 last_value(d.prod_end_bates) OVER w AS family_end
                                            FROM opportunity.production_document d
                                           WHERE d.workspace_id = @ws AND d.production_id = @id
                                          WINDOW w AS (PARTITION BY d.family_key ORDER BY d.sequence
                                                       ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING)) a
                     WHERE a.prod_beg_attach IS DISTINCT FROM a.family_beg OR a.prod_end_attach IS DISTINCT FROM a.family_end)
            """))
        {
            families.Parameters.AddWithValue("ws", workspaceId);
            families.Parameters.AddWithValue("id", productionId);
            await using var reader = await families.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            Problem(reader.GetInt64(0), "family(ies) are not adjacent in production order.");
            Problem(reader.GetInt64(1), "document(s) have attachment ranges that do not span their family.");
        }

        // No Bates number twice in the matter: the reserved range against every other live range of the prefix, and each
        // document against the nearest document of every other live production of the prefix.
        if (production.BatesFirst is { } first && production.BatesLast is { } last)
        {
            foreach (var conflict in await FindConflictsAsync(tx, productionId, production.BatesPrefixKey, first, last, cancellationToken).ConfigureAwait(false))
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture,
                    $"The range overlaps production '{conflict.ProductionName}' ({conflict.FirstNumber}–{conflict.LastNumber})."));
            }
        }

        await using (var duplicates = tx.Command(
            $"""
            SELECT count(*)
              FROM opportunity.production_document a
              JOIN opportunity.production p ON p.workspace_id = a.workspace_id AND p.bates_prefix_key = @key AND p.production_id <> a.production_id
                   AND {LiveProduction}
             CROSS JOIN LATERAL (SELECT b.end_number FROM opportunity.production_document b
                                  WHERE b.workspace_id = p.workspace_id AND b.production_id = p.production_id
                                    AND b.beg_number IS NOT NULL AND b.beg_number <= a.end_number
                                  ORDER BY b.beg_number DESC LIMIT 1) b
             WHERE a.workspace_id = @ws AND a.production_id = @id AND a.beg_number IS NOT NULL AND b.end_number >= a.beg_number
            """))
        {
            duplicates.Parameters.AddWithValue("ws", workspaceId);
            duplicates.Parameters.AddWithValue("id", productionId);
            duplicates.Parameters.AddWithValue("key", production.BatesPrefixKey);
            Problem((long)(await duplicates.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!, "document(s) share Bates numbers with another production.");
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new BatesIntegrityReport(problems.Count == 0, documents, numbers, placeholders, natives, gaps, problems);
    }

    public async Task<IReadOnlyList<ProductionDocumentRow>> ReadDocumentsAsync(
        Guid workspaceId, Guid productionId, long afterSequence, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT pd.sequence, pd.document_id, pd.document_version, pd.output, pd.units, pd.beg_number, pd.end_number, pd.prod_beg_bates,
                   pd.prod_end_bates, pd.prod_beg_attach, pd.prod_end_attach, d.control_number, pd.family_key, pd.first_offset,
                   pd.page_count, pd.designation, pd.designation_choice_id, pd.designation_source,
                   pd.page_set_id, pd.redaction_set_id, pd.redaction_version, pd.redaction_count
              FROM opportunity.production_document pd
              LEFT JOIN opportunity.document d ON d.workspace_id = pd.workspace_id AND d.document_id = pd.document_id
             WHERE pd.workspace_id = @ws AND pd.production_id = @id AND pd.sequence > @after
             ORDER BY pd.sequence
             LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", productionId);
        command.Parameters.AddWithValue("after", afterSequence);
        command.Parameters.AddWithValue("limit", limit);
        var rows = new List<ProductionDocumentRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new ProductionDocumentRow(
                    reader.GetInt64(0), reader.GetGuid(1), reader.GetGuid(12), reader.GetInt64(13), reader.GetInt64(2), (ProductionOutputKind)reader.GetInt16(3), reader.GetInt32(4),
                    NullableInt64(reader, 5), NullableInt64(reader, 6), NullableString(reader, 7), NullableString(reader, 8),
                    NullableString(reader, 9), NullableString(reader, 10), NullableString(reader, 11),
                    reader.GetInt32(14), NullableString(reader, 15), reader.IsDBNull(16) ? null : reader.GetInt32(16),
                    reader.IsDBNull(17) ? null : (DesignationSource)reader.GetInt16(17),
                    reader.IsDBNull(18) ? null : reader.GetGuid(18), reader.IsDBNull(19) ? null : reader.GetGuid(19),
                    NullableInt64(reader, 20), reader.IsDBNull(21) ? null : reader.GetInt32(21)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }

    public async Task<bool> CompleteAllocationAsync(
        Guid workspaceId, Guid productionId, Guid jobId, BatesIntegrityReport integrity, byte[]? assignmentsSha256, IReadOnlyList<AuditEvent> audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrity);
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var update = tx.Command(
            """
            UPDATE opportunity.production
               SET bates_state = CASE WHEN @passed THEN 2 ELSE 3 END, integrity = @integrity::jsonb, assignments_sha256 = @sha,
                   bates_reason = CASE WHEN @passed THEN NULL ELSE @reason END, claimed_by = NULL, claimed_until = NULL,
                   row_version = row_version + 1, modified_at = now()
             WHERE workspace_id = @ws AND production_id = @id AND status = 1 AND bates_state = 1 AND bates_job_id = @job
            """))
        {
            update.Parameters.AddWithValue("ws", workspaceId);
            update.Parameters.AddWithValue("id", productionId);
            update.Parameters.AddWithValue("job", jobId);
            update.Parameters.AddWithValue("passed", integrity.Passed);
            update.Parameters.AddWithValue("integrity", integrity.ToJson().ToJsonString());
            update.Parameters.Add(Nullable("sha", NpgsqlDbType.Bytea, integrity.Passed ? assignmentsSha256 : null));
            update.Parameters.AddWithValue("reason", Truncate("The integrity check failed: " + string.Join(" ", integrity.Problems), 2000));
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return false;
            }
        }

        if (!integrity.Passed)
        {
            await ReleaseRangeAsync(tx, productionId, cancellationToken).ConfigureAwait(false);
        }

        foreach (var auditEvent in audit)
        {
            await AuditSql.InsertAsync(tx, auditEvent, cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> FailAllocationAsync(
        Guid workspaceId, Guid productionId, Guid jobId, string reason, IReadOnlyList<AuditEvent> audit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var update = tx.Command(
            """
            UPDATE opportunity.production
               SET bates_state = 3, bates_reason = @reason, bates_first = NULL, bates_last = NULL, bates_documents = NULL, bates_units = NULL,
                   assignments_sha256 = NULL, claimed_by = NULL, claimed_until = NULL, row_version = row_version + 1, modified_at = now()
             WHERE workspace_id = @ws AND production_id = @id AND status = 1 AND bates_state = 1 AND bates_job_id = @job
            """))
        {
            update.Parameters.AddWithValue("ws", workspaceId);
            update.Parameters.AddWithValue("id", productionId);
            update.Parameters.AddWithValue("job", jobId);
            update.Parameters.AddWithValue("reason", Truncate(reason, 2000));
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return false;
            }
        }

        await ReleaseRangeAsync(tx, productionId, cancellationToken).ConfigureAwait(false);
        await DeleteMembersAsync(tx, productionId, cancellationToken).ConfigureAwait(false);
        foreach (var auditEvent in audit)
        {
            await AuditSql.InsertAsync(tx, auditEvent, cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<ProductionWriteResult> FinalizeAsync(
        Guid workspaceId, Guid productionId, long expectedRowVersion, string manifest, byte[] manifestSha256, Guid finalizedBy, DateTimeOffset finalizedAt,
        IReadOnlyList<AuditEvent> audit, DesignationPlan designations, AuditEvent designationAudit, PrivilegeConflictOverrideWrite? conflictOverride = null,
        Guid? redactionSetId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(manifest);
        ArgumentNullException.ThrowIfNull(manifestSha256);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(designations);
        ArgumentNullException.ThrowIfNull(designationAudit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var current = await ReadOneAsync(tx, productionId, forUpdate: true, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return new ProductionWriteResult(ProductionWriteStatus.NotFound);
        }

        if (current.RowVersion != expectedRowVersion)
        {
            return new ProductionWriteResult(ProductionWriteStatus.VersionConflict, current);
        }

        if (current.Status != ProductionStatus.Draft || current.BatesState != BatesAllocationState.Allocated)
        {
            return new ProductionWriteResult(ProductionWriteStatus.InvalidState, current,
                Reason: current.Status == ProductionStatus.Draft ? "Allocate Bates numbers before finalizing." : "Only a draft production is finalized.");
        }

        var conflicts = await LockAndFindConflictsAsync(tx, productionId, current.BatesPrefixKey, current.BatesFirst!.Value, current.BatesLast!.Value, cancellationToken)
            .ConfigureAwait(false);
        if (conflicts.Count > 0)
        {
            return new ProductionWriteResult(ProductionWriteStatus.BatesConflict, current, conflicts);
        }

        // E13-T01 AC 3: a member coded Privilege Status = Withhold blocks the finalization, read from the coding store
        // after every Privilege Status change before this point has committed (never from the search index).
        await PrivilegeGateSql.EnterExclusiveAsync(tx, cancellationToken).ConfigureAwait(false);
        // The answer gives no count: members the caller may not see must not be counted anywhere (Q-52).
        if (await CodingRepository.AnyProductionMemberWithheldAsync(tx, productionId, cancellationToken).ConfigureAwait(false))
        {
            return new ProductionWriteResult(ProductionWriteStatus.PrivilegeWithheld, current,
                Reason: "Documents in this production are coded Privilege Status = Withhold. Take them out of the frozen set or change their "
                    + "privilege call, then allocate Bates numbers again.");
        }

        // E13-T02 AC 2: unresolved family or duplicate privilege conflicts block too, unless an authorized override with a
        // reason was given; then the manifest and audit events that record the override are written instead.
        if (await CodingRepository.AnyProductionPrivilegeConflictAsync(tx, productionId, cancellationToken).ConfigureAwait(false))
        {
            if (conflictOverride is null)
            {
                return new ProductionWriteResult(ProductionWriteStatus.PrivilegeConflicts, current,
                    Reason: "Documents in this production have unresolved family or duplicate privilege conflicts. Resolve them (see the "
                        + "privilege conflicts report for this production) or finalize with an override and a reason.");
            }

            (manifest, manifestSha256, audit) = (conflictOverride.Manifest, conflictOverride.ManifestSha256, conflictOverride.Audit);
        }

        // E12-T04: each member's designation is frozen from the coding store under the same gate (confidentiality
        // designation changes enter it too), so the stamp and the load-file value come from this one value.
        var (refusal, frozen) = await FreezeDesignationsAsync(tx, productionId, designations, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return new ProductionWriteResult(ProductionWriteStatus.DesignationRefused, current, Reason: refusal);
        }

        // E12-T05: the page set and redaction version every volume run of this production reads (Q-08).
        if (await FreezeRedactionsAsync(tx, productionId, redactionSetId, cancellationToken).ConfigureAwait(false) is { } redactionRefusal)
        {
            return new ProductionWriteResult(ProductionWriteStatus.InvalidState, current, Reason: redactionRefusal);
        }

        await using (var update = tx.Command(
            """
            UPDATE opportunity.production
               SET status = 2, manifest = @manifest, manifest_sha256 = @sha, finalized_at = @at, finalized_by = @by,
                   claimed_by = NULL, claimed_until = NULL, row_version = row_version + 1, modified_at = now()
             WHERE workspace_id = @ws AND production_id = @id;

            UPDATE opportunity.bates_range SET state = 2, state_changed_at = now()
             WHERE workspace_id = @ws AND production_id = @id AND state = 1;
            """))
        {
            update.Parameters.AddWithValue("ws", workspaceId);
            update.Parameters.AddWithValue("id", productionId);
            update.Parameters.AddWithValue("manifest", manifest);
            update.Parameters.AddWithValue("sha", manifestSha256);
            update.Parameters.AddWithValue("at", finalizedAt);
            update.Parameters.AddWithValue("by", finalizedBy);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var auditEvent in audit)
        {
            await AuditSql.InsertAsync(tx, auditEvent, cancellationToken).ConfigureAwait(false);
        }

        var details = new Dictionary<string, string?>(designationAudit.Details, StringComparer.Ordinal)
        {
            ["Designated"] = frozen.Designated.ToString(CultureInfo.InvariantCulture),
            ["ByDocument"] = frozen.ByDocument.ToString(CultureInfo.InvariantCulture),
            ["ByFamily"] = frozen.ByFamily.ToString(CultureInfo.InvariantCulture),
            ["ByOverride"] = frozen.ByOverride.ToString(CultureInfo.InvariantCulture),
        };
        await AuditSql.InsertAsync(tx, Resource(designationAudit, workspaceId, productionId, AuditTaxonomy.Production.DesignationsFrozen) with
        {
            Details = details,
            SnapshotId = current.SnapshotId,
        }, cancellationToken).ConfigureAwait(false);

        var record = (await ReadOneAsync(tx, productionId, forUpdate: false, cancellationToken).ConfigureAwait(false))!;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ProductionWriteResult(ProductionWriteStatus.Applied, record);
    }

    public async Task<ProductionWriteResult> VoidAsync(
        Guid workspaceId, Guid productionId, long expectedRowVersion, string reason, Guid voidedBy, AuditEvent audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var current = await ReadOneAsync(tx, productionId, forUpdate: true, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return new ProductionWriteResult(ProductionWriteStatus.NotFound);
        }

        if (current.RowVersion != expectedRowVersion)
        {
            return new ProductionWriteResult(ProductionWriteStatus.VersionConflict, current);
        }

        if (current.Status != ProductionStatus.Finalized)
        {
            return new ProductionWriteResult(ProductionWriteStatus.InvalidState, current,
                Reason: current.Status == ProductionStatus.Draft ? "A draft is discarded, not voided." : "Only a finalized production is voided.");
        }

        await using (var update = tx.Command(
            """
            UPDATE opportunity.production
               SET status = 3, voided_at = now(), voided_by = @by, void_reason = @reason, row_version = row_version + 1, modified_at = now()
             WHERE workspace_id = @ws AND production_id = @id;

            UPDATE opportunity.bates_range SET state = 3, state_changed_at = now()
             WHERE workspace_id = @ws AND production_id = @id AND state = 2;
            """))
        {
            update.Parameters.AddWithValue("ws", workspaceId);
            update.Parameters.AddWithValue("id", productionId);
            update.Parameters.AddWithValue("by", voidedBy);
            update.Parameters.AddWithValue("reason", reason.Trim());
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AuditSql.InsertAsync(tx, Resource(audit, workspaceId, productionId, AuditTaxonomy.Production.Voided), cancellationToken).ConfigureAwait(false);
        var record = (await ReadOneAsync(tx, productionId, forUpdate: false, cancellationToken).ConfigureAwait(false))!;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ProductionWriteResult(ProductionWriteStatus.Applied, record);
    }

    public async Task<ProductionWriteResult> DiscardAsync(
        Guid workspaceId, Guid productionId, long expectedRowVersion, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        if (await EditableDraftAsync(tx, productionId, expectedRowVersion, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        await ReleaseRangeAsync(tx, productionId, cancellationToken).ConfigureAwait(false);
        await using (var update = tx.Command(
            """
            UPDATE opportunity.production
               SET status = 4, discarded_at = now(), claimed_by = NULL, claimed_until = NULL, row_version = row_version + 1, modified_at = now()
             WHERE workspace_id = @ws AND production_id = @id
            """))
        {
            update.Parameters.AddWithValue("ws", workspaceId);
            update.Parameters.AddWithValue("id", productionId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AuditSql.InsertAsync(tx, Resource(audit, workspaceId, productionId, AuditTaxonomy.Production.Discarded), cancellationToken).ConfigureAwait(false);
        var record = (await ReadOneAsync(tx, productionId, forUpdate: false, cancellationToken).ConfigureAwait(false))!;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ProductionWriteResult(ProductionWriteStatus.Applied, record);
    }

    public async Task<IReadOnlyList<BatesFormatInUse>> GetFormatsInUseAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            SELECT DISTINCT p.bates_prefix, p.bates_suffix, p.bates_padding FROM opportunity.production p
             WHERE p.workspace_id = @ws AND p.bates_first IS NOT NULL AND {LiveProduction}
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        var formats = new List<BatesFormatInUse>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                formats.Add(new BatesFormatInUse(reader.GetString(0), reader.GetString(1), reader.GetInt16(2)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return formats;
    }

    public async Task<IReadOnlyList<BatesLookupRow>> LookupNumberAsync(
        Guid workspaceId, string prefixKey, long number, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            SELECT p.production_id, p.name, p.version, p.status, pd.document_id, d.control_number, pd.prod_beg_bates, pd.prod_end_bates,
                   pd.prod_beg_attach, pd.prod_end_attach
              FROM opportunity.production p
             CROSS JOIN LATERAL (SELECT x.* FROM opportunity.production_document x
                                  WHERE x.workspace_id = p.workspace_id AND x.production_id = p.production_id
                                    AND x.beg_number IS NOT NULL AND x.beg_number <= @n
                                  ORDER BY x.beg_number DESC LIMIT 1) pd
              LEFT JOIN opportunity.document d ON d.workspace_id = pd.workspace_id AND d.document_id = pd.document_id
             WHERE p.workspace_id = @ws AND p.bates_prefix_key = @key AND p.bates_first <= @n AND p.bates_last >= @n
               AND {LiveProduction} AND pd.end_number >= @n
             ORDER BY p.created_at, p.production_id
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("key", prefixKey);
        command.Parameters.AddWithValue("n", number);
        var rows = await ReadLookupAsync(command, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }

    public async Task<IReadOnlyList<BatesLookupRow>> LookupDocumentAsync(Guid workspaceId, Guid documentId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            SELECT p.production_id, p.name, p.version, p.status, pd.document_id, d.control_number, pd.prod_beg_bates, pd.prod_end_bates,
                   pd.prod_beg_attach, pd.prod_end_attach
              FROM opportunity.production_document pd
              JOIN opportunity.production p ON p.workspace_id = pd.workspace_id AND p.production_id = pd.production_id
              LEFT JOIN opportunity.document d ON d.workspace_id = pd.workspace_id AND d.document_id = pd.document_id
             WHERE pd.workspace_id = @ws AND pd.document_id = @doc AND pd.beg_number IS NOT NULL AND {LiveProduction}
             ORDER BY p.created_at, p.production_id
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("doc", documentId);
        var rows = await ReadLookupAsync(command, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }

    public async Task AuditAsync(AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, audit.WorkspaceId!.Value, cancellationToken).ConfigureAwait(false);
        await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProductionInitiator?> ReadInitiatorAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command("SELECT coalesce(display_name, email, subject), groups FROM opportunity.app_user WHERE user_id = @id");
        command.Parameters.AddWithValue("id", userId);
        ProductionInitiator? initiator = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                initiator = new ProductionInitiator(reader.GetString(0), reader.GetFieldValue<string[]>(1));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return initiator;
    }

    /// <summary>Null when the draft may be edited; otherwise the refusal.</summary>
    private static async Task<ProductionWriteResult?> EditableDraftAsync(
        WorkspaceTransaction tx, Guid productionId, long expectedRowVersion, CancellationToken cancellationToken)
    {
        var current = await ReadOneAsync(tx, productionId, forUpdate: true, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return new ProductionWriteResult(ProductionWriteStatus.NotFound);
        }

        if (current.RowVersion != expectedRowVersion)
        {
            return new ProductionWriteResult(ProductionWriteStatus.VersionConflict, current);
        }

        if (current.Status != ProductionStatus.Draft)
        {
            return new ProductionWriteResult(ProductionWriteStatus.InvalidState, current,
                Reason: "A finalized production never changes; create a new version of it instead.");
        }

        return current.BatesState == BatesAllocationState.Allocating
            ? new ProductionWriteResult(ProductionWriteStatus.InvalidState, current, Reason: "Bates numbers are being allocated; wait for the job to finish.")
            : null;
    }

    /// <summary>Releases a draft's allocation: its reserved range (Q-54) and its members; the state returns to None.</summary>
    private static async Task ReleaseAllocationAsync(WorkspaceTransaction tx, Guid productionId, CancellationToken cancellationToken)
    {
        await ReleaseRangeAsync(tx, productionId, cancellationToken).ConfigureAwait(false);
        await DeleteMembersAsync(tx, productionId, cancellationToken).ConfigureAwait(false);
        await using var update = tx.Command(
            """
            UPDATE opportunity.production
               SET bates_state = 0, bates_job_id = NULL, bates_reason = NULL, bates_first = NULL, bates_last = NULL, bates_documents = NULL,
                   bates_units = NULL, assignments_sha256 = NULL, integrity = NULL
             WHERE workspace_id = @ws AND production_id = @id AND status = 1
            """);
        update.Parameters.AddWithValue("ws", tx.WorkspaceId);
        update.Parameters.AddWithValue("id", productionId);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReleaseRangeAsync(WorkspaceTransaction tx, Guid productionId, CancellationToken cancellationToken)
    {
        await using var release = tx.Command(
            "UPDATE opportunity.bates_range SET state = 4, state_changed_at = now() WHERE workspace_id = @ws AND production_id = @id AND state = 1");
        release.Parameters.AddWithValue("ws", tx.WorkspaceId);
        release.Parameters.AddWithValue("id", productionId);
        await release.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteMembersAsync(WorkspaceTransaction tx, Guid productionId, CancellationToken cancellationToken)
    {
        await using var delete = tx.Command("DELETE FROM opportunity.production_document WHERE workspace_id = @ws AND production_id = @id");
        delete.Parameters.AddWithValue("ws", tx.WorkspaceId);
        delete.Parameters.AddWithValue("id", productionId);
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task LockPrefixAsync(WorkspaceTransaction tx, string prefixKey, CancellationToken cancellationToken)
    {
        await using var command = tx.Command("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))");
        command.Parameters.AddWithValue("key", "opportunity.bates:" + tx.WorkspaceId.ToString("N") + ":" + prefixKey);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<BatesRangeConflict>> LockAndFindConflictsAsync(
        WorkspaceTransaction tx, Guid productionId, string prefixKey, long first, long last, CancellationToken cancellationToken)
    {
        await LockPrefixAsync(tx, prefixKey, cancellationToken).ConfigureAwait(false);
        return await FindConflictsAsync(tx, productionId, prefixKey, first, last, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<BatesRangeConflict>> FindConflictsAsync(
        WorkspaceTransaction tx, Guid productionId, string prefixKey, long first, long last, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            """
            SELECT r.production_id, p.name, r.first_number, r.last_number, p.status
              FROM opportunity.bates_range r
              JOIN opportunity.production p ON p.workspace_id = r.workspace_id AND p.production_id = r.production_id
             WHERE r.workspace_id = @ws AND r.bates_prefix_key = @key AND r.state <> 4 AND r.production_id <> @id
               AND r.first_number <= @last AND r.last_number >= @first
             ORDER BY r.first_number
             LIMIT 5
            """);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("key", prefixKey);
        command.Parameters.AddWithValue("id", productionId);
        command.Parameters.AddWithValue("first", first);
        command.Parameters.AddWithValue("last", last);
        var conflicts = new List<BatesRangeConflict>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            conflicts.Add(new BatesRangeConflict(reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), (ProductionStatus)reader.GetInt16(4)));
        }

        return conflicts;
    }

    private static async Task<IReadOnlyList<BatesChunkRange>> ChunkRangesAsync(WorkspaceTransaction tx, Guid productionId, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            """
            SELECT chunk_sequence, min(sequence), max(sequence)
              FROM opportunity.production_document
             WHERE workspace_id = @ws AND production_id = @id
             GROUP BY chunk_sequence
             ORDER BY chunk_sequence
            """);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("id", productionId);
        var chunks = new List<BatesChunkRange>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            chunks.Add(new BatesChunkRange(reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2)));
        }

        return chunks;
    }

    private static async Task<List<BatesLookupRow>> ReadLookupAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var rows = new List<BatesLookupRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new BatesLookupRow(
                reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), (ProductionStatus)reader.GetInt16(3), reader.GetGuid(4),
                NullableString(reader, 5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9)));
        }

        return rows;
    }

    private static void AddSpecification(NpgsqlCommand command, ProductionSpecificationRow spec)
    {
        command.Parameters.AddWithValue("spec", spec.Json);
        command.Parameters.AddWithValue("sha", spec.Sha256);
        command.Parameters.AddWithValue("prefix", spec.BatesPrefix);
        command.Parameters.AddWithValue("key", BatesFormat.KeyOf(spec.BatesPrefix));
        command.Parameters.AddWithValue("suffix", spec.BatesSuffix);
        command.Parameters.AddWithValue("padding", (short)spec.BatesPadding);
        command.Parameters.AddWithValue("start", spec.BatesStart);
    }

    private static AuditEvent Resource(AuditEvent template, Guid workspaceId, Guid productionId, string action) => template with
    {
        EventId = Guid.CreateVersion7(),
        WorkspaceId = workspaceId,
        Category = AuditTaxonomy.Production.Category,
        Action = action,
        ResourceType = AuditTaxonomy.Production.ResourceType,
        ResourceId = productionId.ToString(),
    };

    private static async Task<ProductionRecord?> ReadOneAsync(WorkspaceTransaction tx, Guid productionId, bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            $"SELECT {Columns} FROM opportunity.production p WHERE p.workspace_id = @ws AND p.production_id = @id{(forUpdate ? " FOR UPDATE" : string.Empty)}");
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("id", productionId);
        var records = await ReadManyAsync(command, cancellationToken).ConfigureAwait(false);
        return records.Count == 1 ? records[0] : null;
    }

    private static async Task<List<ProductionRecord>> ReadManyAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var records = new List<ProductionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(Read(reader));
        }

        return records;
    }

    private static ProductionRecord Read(NpgsqlDataReader r) => new()
    {
        WorkspaceId = r.GetGuid(0),
        ProductionId = r.GetGuid(1),
        LineageId = r.GetGuid(2),
        Version = r.GetInt32(3),
        Name = r.GetString(4),
        SnapshotId = r.GetGuid(5),
        SpecificationJson = r.GetString(6),
        SpecificationSha256 = r.GetFieldValue<byte[]>(7),
        BatesPrefix = r.GetString(8),
        BatesSuffix = r.GetString(9),
        BatesPadding = r.GetInt16(10),
        BatesStart = r.GetInt64(11),
        Status = (ProductionStatus)r.GetInt16(12),
        RowVersion = r.GetInt64(13),
        BatesState = (BatesAllocationState)r.GetInt16(14),
        BatesJobId = r.IsDBNull(15) ? null : r.GetGuid(15),
        BatesReason = NullableString(r, 16),
        BatesFirst = NullableInt64(r, 17),
        BatesLast = NullableInt64(r, 18),
        BatesDocuments = NullableInt64(r, 19),
        BatesUnits = NullableInt64(r, 20),
        AssignmentsSha256 = r.IsDBNull(21) ? null : r.GetFieldValue<byte[]>(21),
        IntegrityJson = NullableString(r, 22),
        Manifest = NullableString(r, 23),
        ManifestSha256 = r.IsDBNull(24) ? null : r.GetFieldValue<byte[]>(24),
        CreatedBy = r.GetGuid(25),
        CreatedByDisplay = r.GetString(26),
        CreatedByGroups = r.GetFieldValue<string[]>(27),
        CreatedAt = r.GetFieldValue<DateTimeOffset>(28),
        ModifiedAt = r.GetFieldValue<DateTimeOffset>(29),
        FinalizedAt = r.IsDBNull(30) ? null : r.GetFieldValue<DateTimeOffset>(30),
        FinalizedBy = r.IsDBNull(31) ? null : r.GetGuid(31),
        VoidedAt = r.IsDBNull(32) ? null : r.GetFieldValue<DateTimeOffset>(32),
        VoidReason = NullableString(r, 33),
        DiscardedAt = r.IsDBNull(34) ? null : r.GetFieldValue<DateTimeOffset>(34),
    };

    private static string? NullableString(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static long? NullableInt64(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    private static NpgsqlParameter Nullable(string name, NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}

public static class ProductionStoreRegistration
{
    public static IServiceCollection AddPostgresProductionStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IProductionStore, ProductionRepository>();
        return services;
    }
}
