using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Jobs;
using Opportunity.Application.Rendering;
using Opportunity.Core.Jobs;
using Opportunity.Core.Pages;
using Opportunity.Core.Storage;
using Opportunity.Data.Audit;
using Opportunity.Data.Jobs;

namespace Opportunity.Data.Rendering;

/// <summary>PostgreSQL implementation of <see cref="IRenderStore"/> (E11-T02); every statement runs in the workspace's RLS context.</summary>
public sealed class RenderRepository(NpgsqlDataSource dataSource) : IRenderStore
{
    /// <summary>Stored content types a native is rendered from (ContentSniffer values).</summary>
    public static readonly string[] RenderableNativeTypes = ["application/pdf", "image/tiff", "image/jpeg", "image/png"];

    private static readonly string[] FinishedStatuses =
        [nameof(JobStatus.Completed), nameof(JobStatus.CompletedWithErrors), nameof(JobStatus.Failed), nameof(JobStatus.Cancelled)];

    public async Task<IReadOnlyList<ImportToRender>> GetImportsToRenderAsync(Guid workspaceId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT j.job_id, j.import_batch_id, j.initiated_by, j.correlation_id
            FROM opportunity.job j
            WHERE j.workspace_id = @ws AND j.job_type = @import AND j.status = ANY(@finished)
              AND NOT EXISTS (SELECT 1 FROM opportunity.render_request r
                              WHERE r.workspace_id = j.workspace_id AND r.source_job_id = j.job_id)
            ORDER BY j.created_at, j.job_id
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("import", nameof(JobType.Import));
        command.Parameters.AddWithValue("finished", FinishedStatuses);
        command.Parameters.AddWithValue("limit", limit);
        var result = new List<ImportToRender>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(new ImportToRender(
                    reader.GetGuid(0),
                    reader.IsDBNull(1) ? null : reader.GetGuid(1),
                    reader.GetGuid(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task RecordRenderRequestAsync(Guid workspaceId, Guid sourceJobId, Guid? renderJobId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            INSERT INTO opportunity.render_request (workspace_id, source_job_id, render_job_id)
            VALUES (@ws, @source, @render)
            ON CONFLICT (workspace_id, source_job_id) DO NOTHING
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("source", sourceJobId);
        command.Parameters.Add(new NpgsqlParameter("render", NpgsqlDbType.Uuid) { Value = (object?)renderJobId ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RenderJobToPlan>> GetJobsToPlanAsync(Guid workspaceId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT j.job_id, j.status, j.parameters::text
            FROM opportunity.job j
            WHERE j.workspace_id = @ws AND j.job_type = @render AND j.status = ANY(@open)
            ORDER BY j.created_at, j.job_id
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("render", nameof(JobType.Render));
        command.Parameters.AddWithValue("open", new[] { nameof(JobStatus.Created), nameof(JobStatus.Preparing) });
        command.Parameters.AddWithValue("limit", limit);
        var result = new List<RenderJobToPlan>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(new RenderJobToPlan(
                    reader.GetGuid(0),
                    Enum.Parse<JobStatus>(reader.GetString(1)),
                    JsonNode.Parse(reader.GetString(2)) as JsonObject ?? []));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<RenderCandidate>> GetCandidatesAsync(
        Guid workspaceId, RenderScope scope, RenderRendererKey renderer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(renderer);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            WITH scope AS (
                SELECT m.document_id FROM opportunity.import_batch_member m
                WHERE m.workspace_id = @ws AND m.import_batch_id = @batch AND m.action IN (1, 2)
                UNION
                SELECT p.document_id FROM opportunity.page_set p
                WHERE p.workspace_id = @ws AND p.import_job_id = @job AND p.source = @imported)
            SELECT d.document_id, coalesce(ps.page_count, 0), coalesce(n.size_bytes, 0)
            FROM scope
            JOIN opportunity.document d ON d.workspace_id = @ws AND d.document_id = scope.document_id
            JOIN opportunity.document_projection_state s
              ON s.workspace_id = d.workspace_id AND s.document_id = d.document_id AND NOT s.is_deleted
            LEFT JOIN opportunity.page_set ps ON ps.workspace_id = d.workspace_id AND ps.page_set_id = d.active_page_set_id
            LEFT JOIN opportunity.stored_object n
                   ON n.workspace_id = d.workspace_id AND n.object_id = d.native_object_id AND n.area = @native_area AND n.state = @committed
            WHERE (ps.source = @imported AND EXISTS (
                       SELECT 1
                       FROM opportunity.page_image o
                       WHERE o.workspace_id = ps.workspace_id AND o.page_set_id = ps.page_set_id AND o.purpose = @original
                         AND NOT EXISTS (SELECT 1 FROM opportunity.page_image t
                                         WHERE t.workspace_id = o.workspace_id AND t.page_set_id = o.page_set_id
                                           AND t.ordinal = o.ordinal AND t.purpose = @thumbnail)))
               OR (n.content_type = ANY(@renderable)
                   AND (ps.page_set_id IS NULL OR ps.status <> @ready)
                   AND NOT EXISTS (SELECT 1 FROM opportunity.page_set r
                                   WHERE r.workspace_id = d.workspace_id AND r.document_id = d.document_id AND r.source = @rendered
                                     AND r.renderer_name = @name AND r.renderer_version = @version AND r.render_settings_hash = @hash))
            ORDER BY d.document_id
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.Add(new NpgsqlParameter("batch", NpgsqlDbType.Uuid) { Value = (object?)scope.ImportBatchId ?? DBNull.Value });
        command.Parameters.AddWithValue("job", scope.ImportJobId);
        command.Parameters.AddWithValue("imported", (short)PageSetSource.Imported);
        command.Parameters.AddWithValue("rendered", (short)PageSetSource.Rendered);
        command.Parameters.AddWithValue("ready", (short)PageSetStatus.Ready);
        command.Parameters.AddWithValue("original", (short)PageImagePurpose.Original);
        command.Parameters.AddWithValue("thumbnail", (short)PageImagePurpose.Thumbnail);
        command.Parameters.AddWithValue("native_area", (short)ObjectArea.Native);
        command.Parameters.AddWithValue("committed", (short)StoredObjectState.Committed);
        command.Parameters.AddWithValue("renderable", RenderableNativeTypes);
        command.Parameters.AddWithValue("name", renderer.Name);
        command.Parameters.AddWithValue("version", renderer.Version);
        command.Parameters.AddWithValue("hash", renderer.SettingsHash);
        var result = new List<RenderCandidate>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(new RenderCandidate(reader.GetGuid(0), reader.GetInt32(1), reader.GetInt64(2)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<RenderDocumentState>> ReadDocumentsAsync(
        Guid workspaceId, IReadOnlyList<Guid> documentIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        var ids = documentIds.ToArray();
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var documents = new Dictionary<Guid, RenderDocumentState>();
        await using (var command = tx.Command(
            """
            SELECT d.document_id, d.active_page_set_id, ps.source, ps.status,
                   n.logical_key, n.sha256, n.size_bytes, n.content_type
            FROM opportunity.document d
            JOIN opportunity.document_projection_state s
              ON s.workspace_id = d.workspace_id AND s.document_id = d.document_id AND NOT s.is_deleted
            LEFT JOIN opportunity.page_set ps ON ps.workspace_id = d.workspace_id AND ps.page_set_id = d.active_page_set_id
            LEFT JOIN opportunity.stored_object n
                   ON n.workspace_id = d.workspace_id AND n.object_id = d.native_object_id AND n.area = @native_area AND n.state = @committed
            WHERE d.workspace_id = @ws AND d.document_id = ANY(@ids)
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("ids", ids);
            command.Parameters.AddWithValue("native_area", (short)ObjectArea.Native);
            command.Parameters.AddWithValue("committed", (short)StoredObjectState.Committed);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetGuid(0);
                documents[id] = new RenderDocumentState
                {
                    DocumentId = id,
                    ActivePageSetId = reader.IsDBNull(1) ? null : reader.GetGuid(1),
                    ActiveSource = reader.IsDBNull(2) ? null : (PageSetSource)reader.GetInt16(2),
                    ActiveStatus = reader.IsDBNull(3) ? null : (PageSetStatus)reader.GetInt16(3),
                    Native = reader.IsDBNull(4)
                        ? null
                        : new RenderSourceObject(reader.GetString(4), reader.GetFieldValue<byte[]>(5), reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetString(7)),
                };
            }
        }

        var pages = new Dictionary<Guid, List<ImportedPageState>>();
        await using (var command = tx.Command(
            """
            SELECT d.document_id, p.ordinal, p.source_frame, p.width_pt, p.height_pt,
                   o.logical_key, o.sha256, o.size_bytes, o.content_type, orig.format,
                   EXISTS (SELECT 1 FROM opportunity.page_image r WHERE r.workspace_id = p.workspace_id AND r.page_set_id = p.page_set_id
                                                                    AND r.ordinal = p.ordinal AND r.purpose = @review),
                   EXISTS (SELECT 1 FROM opportunity.page_image t WHERE t.workspace_id = p.workspace_id AND t.page_set_id = p.page_set_id
                                                                    AND t.ordinal = p.ordinal AND t.purpose = @thumbnail)
            FROM opportunity.document d
            JOIN opportunity.page_set ps ON ps.workspace_id = d.workspace_id AND ps.page_set_id = d.active_page_set_id AND ps.source = @imported
            JOIN opportunity.page p ON p.workspace_id = ps.workspace_id AND p.page_set_id = ps.page_set_id
            LEFT JOIN opportunity.page_image orig ON orig.workspace_id = p.workspace_id AND orig.page_set_id = p.page_set_id
                                                 AND orig.ordinal = p.ordinal AND orig.purpose = @original
            LEFT JOIN opportunity.stored_object o ON o.workspace_id = orig.workspace_id AND o.object_id = orig.object_id AND o.state = @committed
            WHERE d.workspace_id = @ws AND d.document_id = ANY(@ids)
            ORDER BY d.document_id, p.ordinal
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("ids", ids);
            command.Parameters.AddWithValue("imported", (short)PageSetSource.Imported);
            command.Parameters.AddWithValue("original", (short)PageImagePurpose.Original);
            command.Parameters.AddWithValue("review", (short)PageImagePurpose.Review);
            command.Parameters.AddWithValue("thumbnail", (short)PageImagePurpose.Thumbnail);
            command.Parameters.AddWithValue("committed", (short)StoredObjectState.Committed);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetGuid(0);
                if (!pages.TryGetValue(id, out var list))
                {
                    pages[id] = list = [];
                }

                list.Add(new ImportedPageState(
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetDecimal(3),
                    reader.GetDecimal(4),
                    reader.IsDBNull(5)
                        ? null
                        : new RenderSourceObject(reader.GetString(5), reader.GetFieldValue<byte[]>(6), reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetString(8)),
                    reader.IsDBNull(9) ? null : (PageImageFormat)reader.GetInt16(9),
                    reader.GetBoolean(10),
                    reader.GetBoolean(11)));
            }
        }

        var rendered = new Dictionary<Guid, List<Guid>>();
        await using (var command = tx.Command(
            "SELECT document_id, page_set_id FROM opportunity.page_set WHERE workspace_id = @ws AND document_id = ANY(@ids) AND source = @rendered"))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("ids", ids);
            command.Parameters.AddWithValue("rendered", (short)PageSetSource.Rendered);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetGuid(0);
                if (!rendered.TryGetValue(id, out var list))
                {
                    rendered[id] = list = [];
                }

                list.Add(reader.GetGuid(1));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return [.. ids.Where(documents.ContainsKey).Select(id => documents[id] with
        {
            ImportedPages = pages.TryGetValue(id, out var p) ? p : [],
            RenderedPageSetIds = rendered.TryGetValue(id, out var r) ? r : [],
        })];
    }

    public async Task<ChunkCommitResult> ApplyChunkAsync(
        ClaimedChunk chunk, RenderChunkWrite write, ChunkCompletion completion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(completion);
        var lease = chunk.Lease;
        if (chunk.JobType != JobType.Render || chunk.Membership.Kind != ChunkMembershipKind.ExplicitIds)
        {
            throw new ArgumentException("A render chunk write belongs to a leased ExplicitIds render chunk.", nameof(chunk));
        }

        var members = chunk.Membership.DocumentIds!.ToHashSet();
        if (write.Objects.Any(o => !members.Contains(o.DocumentId)) || write.PageSets.Any(s => !members.Contains(s.DocumentId)))
        {
            throw new ArgumentException("A render chunk writes only for its own documents.", nameof(write));
        }

        ChunkCommitResult commit;
        await using (var tx = await WorkspaceTransaction.BeginAsync(dataSource, lease.WorkspaceId, cancellationToken).ConfigureAwait(false))
        {
            var objectIds = await RegisterObjectsAsync(tx, lease.JobId, write.Objects, cancellationToken).ConfigureAwait(false);
            await InsertPageSetsAsync(tx, write.PageSets, cancellationToken).ConfigureAwait(false);
            await InsertRastersAsync(tx, write.Rasters, objectIds, cancellationToken).ConfigureAwait(false);
            await ActivateAsync(tx, write.PageSets, cancellationToken).ConfigureAwait(false);
            foreach (var auditEvent in write.Audit)
            {
                await AuditSql.InsertAsync(tx, auditEvent, cancellationToken).ConfigureAwait(false);
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

    private static async Task<Dictionary<string, Guid>> RegisterObjectsAsync(
        WorkspaceTransaction tx, Guid jobId, IReadOnlyList<RenditionObject> objects, CancellationToken cancellationToken)
    {
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (objects.Count == 0)
        {
            return ids;
        }

        var distinct = objects.DistinctBy(o => o.LogicalKey, StringComparer.Ordinal).ToList();
        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.stored_object
                (workspace_id, object_id, logical_key, area, document_id, sha256, size_bytes, content_type, key_id, encryption_scheme, state,
                 created_by_job_id)
            SELECT @ws, u.object_id, u.logical_key, @area, u.document_id, u.sha256, u.size_bytes, u.content_type, u.key_id, u.scheme, @state, @job
            FROM unnest(@ids, @keys, @documents, @shas, @sizes, @types, @key_ids, @schemes)
                AS u(object_id, logical_key, document_id, sha256, size_bytes, content_type, key_id, scheme)
            ON CONFLICT (workspace_id, logical_key) DO NOTHING
            """))
        {
            insert.Parameters.AddWithValue("ws", tx.WorkspaceId);
            insert.Parameters.AddWithValue("area", (short)ObjectArea.Rendition);
            insert.Parameters.AddWithValue("state", (short)StoredObjectState.Committed);
            insert.Parameters.AddWithValue("job", jobId);
            insert.Parameters.AddWithValue("ids", distinct.Select(_ => Guid.CreateVersion7()).ToArray());
            insert.Parameters.AddWithValue("keys", distinct.Select(o => o.LogicalKey).ToArray());
            insert.Parameters.AddWithValue("documents", distinct.Select(o => o.DocumentId).ToArray());
            insert.Parameters.AddWithValue("shas", distinct.Select(o => o.Sha256).ToArray());
            insert.Parameters.AddWithValue("sizes", distinct.Select(o => o.SizeBytes).ToArray());
            insert.Parameters.AddWithValue("types", distinct.Select(o => o.ContentType).ToArray());
            insert.Parameters.AddWithValue("key_ids", distinct.Select(o => o.KeyId).ToArray());
            insert.Parameters.AddWithValue("schemes", distinct.Select(o => (short)o.EncryptionScheme).ToArray());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var select = tx.Command(
            "SELECT logical_key, object_id FROM opportunity.stored_object WHERE workspace_id = @ws AND logical_key = ANY(@keys)");
        select.Parameters.AddWithValue("ws", tx.WorkspaceId);
        select.Parameters.AddWithValue("keys", distinct.Select(o => o.LogicalKey).ToArray());
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids[reader.GetString(0)] = reader.GetGuid(1);
        }

        return ids;
    }

    private static async Task InsertPageSetsAsync(WorkspaceTransaction tx, IReadOnlyList<NewRenderedPageSet> sets, CancellationToken cancellationToken)
    {
        if (sets.Count == 0)
        {
            return;
        }

        await using (var command = tx.Command(
            """
            INSERT INTO opportunity.page_set
                (workspace_id, page_set_id, document_id, source, renderer_name, renderer_version, render_settings_hash, page_count, status)
            SELECT @ws, u.page_set_id, u.document_id, @rendered, u.name, u.version, u.hash, u.page_count, u.status
            FROM unnest(@ids, @documents, @names, @versions, @hashes, @counts, @statuses)
                AS u(page_set_id, document_id, name, version, hash, page_count, status)
            ON CONFLICT (workspace_id, page_set_id) DO NOTHING
            """))
        {
            command.Parameters.AddWithValue("ws", tx.WorkspaceId);
            command.Parameters.AddWithValue("rendered", (short)PageSetSource.Rendered);
            command.Parameters.AddWithValue("ids", sets.Select(s => s.PageSetId).ToArray());
            command.Parameters.AddWithValue("documents", sets.Select(s => s.DocumentId).ToArray());
            command.Parameters.AddWithValue("names", sets.Select(s => s.Renderer.Name).ToArray());
            command.Parameters.AddWithValue("versions", sets.Select(s => s.Renderer.Version).ToArray());
            command.Parameters.AddWithValue("hashes", sets.Select(s => s.Renderer.SettingsHash).ToArray());
            command.Parameters.AddWithValue("counts", sets.Select(s => s.Pages.Count).ToArray());
            command.Parameters.AddWithValue("statuses", sets.Select(s => (short)s.Status).ToArray());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var pages = sets.SelectMany(s => s.Pages.Select(p => (s.PageSetId, s.DocumentId, Page: p))).ToList();
        if (pages.Count == 0)
        {
            return;
        }

        await using var insertPages = tx.Command(
            """
            INSERT INTO opportunity.page
                (workspace_id, page_set_id, ordinal, document_id, image_key, width_pt, height_pt, rotation, color_mode, source_frame, image_missing)
            SELECT @ws, u.page_set_id, u.ordinal, u.document_id, NULL, u.width_pt, u.height_pt, 0, u.color_mode, u.source_frame, u.missing
            FROM unnest(@sets, @ordinals, @documents, @widths, @heights, @colors, @frames, @missing)
                AS u(page_set_id, ordinal, document_id, width_pt, height_pt, color_mode, source_frame, missing)
            ON CONFLICT (workspace_id, page_set_id, ordinal) DO NOTHING
            """);
        insertPages.Parameters.AddWithValue("ws", tx.WorkspaceId);
        insertPages.Parameters.AddWithValue("sets", pages.Select(p => p.PageSetId).ToArray());
        insertPages.Parameters.AddWithValue("ordinals", pages.Select(p => p.Page.Ordinal).ToArray());
        insertPages.Parameters.AddWithValue("documents", pages.Select(p => p.DocumentId).ToArray());
        insertPages.Parameters.AddWithValue("widths", pages.Select(p => p.Page.WidthPt).ToArray());
        insertPages.Parameters.AddWithValue("heights", pages.Select(p => p.Page.HeightPt).ToArray());
        insertPages.Parameters.AddWithValue("colors", pages.Select(p => (short)p.Page.ColorMode).ToArray());
        insertPages.Parameters.AddWithValue("frames", pages.Select(p => p.Page.Ordinal - 1).ToArray());
        insertPages.Parameters.AddWithValue("missing", pages.Select(p => p.Page.ImageMissing).ToArray());
        await insertPages.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertRastersAsync(
        WorkspaceTransaction tx, IReadOnlyList<NewPageRaster> rasters, Dictionary<string, Guid> objectIds, CancellationToken cancellationToken)
    {
        if (rasters.Count == 0)
        {
            return;
        }

        await using var command = tx.Command(
            """
            INSERT INTO opportunity.page_image (workspace_id, page_set_id, ordinal, purpose, object_id, width_px, height_px, dpi_x, dpi_y, format)
            SELECT @ws, u.page_set_id, u.ordinal, u.purpose, u.object_id, u.width_px, u.height_px, u.dpi, u.dpi, u.format
            FROM unnest(@sets, @ordinals, @purposes, @objects, @widths, @heights, @dpis, @formats)
                AS u(page_set_id, ordinal, purpose, object_id, width_px, height_px, dpi, format)
            ON CONFLICT (workspace_id, page_set_id, ordinal, purpose) DO NOTHING
            """);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("sets", rasters.Select(r => r.PageSetId).ToArray());
        command.Parameters.AddWithValue("ordinals", rasters.Select(r => r.Ordinal).ToArray());
        command.Parameters.AddWithValue("purposes", rasters.Select(r => (short)r.Purpose).ToArray());
        command.Parameters.AddWithValue("objects", rasters.Select(r => objectIds[r.LogicalKey]).ToArray());
        command.Parameters.AddWithValue("widths", rasters.Select(r => r.WidthPx).ToArray());
        command.Parameters.AddWithValue("heights", rasters.Select(r => r.HeightPx).ToArray());
        command.Parameters.AddWithValue("dpis", rasters.Select(r => r.Dpi).ToArray());
        command.Parameters.AddWithValue("formats", rasters.Select(r => (short)r.Format).ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// ADR-012 §1.2 and Q-68: a Ready Rendered set becomes active unless the active set is Ready and Imported (an
    /// Incomplete, Pending or Failed set, or an older Rendered set, gives way); any rendered set fills an empty slot.
    /// </summary>
    private static async Task ActivateAsync(WorkspaceTransaction tx, IReadOnlyList<NewRenderedPageSet> sets, CancellationToken cancellationToken)
    {
        var candidates = sets.Where(s => s.Status is PageSetStatus.Ready or PageSetStatus.Incomplete).ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        await using var command = tx.Command(
            """
            UPDATE opportunity.document d
            SET active_page_set_id = u.page_set_id, updated_at = now()
            FROM unnest(@documents, @sets, @ready) AS u(document_id, page_set_id, ready)
            WHERE d.workspace_id = @ws AND d.document_id = u.document_id
              AND d.active_page_set_id IS DISTINCT FROM u.page_set_id
              AND (d.active_page_set_id IS NULL
                   OR (u.ready AND EXISTS (
                         SELECT 1 FROM opportunity.page_set cur
                         WHERE cur.workspace_id = d.workspace_id AND cur.page_set_id = d.active_page_set_id
                           AND (cur.status <> @ready_status OR cur.source = @rendered))))
            """);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("documents", candidates.Select(s => s.DocumentId).ToArray());
        command.Parameters.AddWithValue("sets", candidates.Select(s => s.PageSetId).ToArray());
        command.Parameters.AddWithValue("ready", candidates.Select(s => s.Status == PageSetStatus.Ready).ToArray());
        command.Parameters.AddWithValue("ready_status", (short)PageSetStatus.Ready);
        command.Parameters.AddWithValue("rendered", (short)PageSetSource.Rendered);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public static class RenderStoreRegistration
{
    public static IServiceCollection AddPostgresRenderStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IRenderStore, RenderRepository>();
        return services;
    }
}
