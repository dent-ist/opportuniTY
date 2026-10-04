using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Import;
using Opportunity.Core.Pages;
using Opportunity.Core.Storage;

namespace Opportunity.Data.Import;

/// <summary>
/// OPT image cross-references (E08-T05, V0021): the staged OPT rows of the preparation pass, their assignment to rows,
/// orphan reporting, and the chunk's write of Imported page sets (ADR-012 §1.6) with their objects (ADR-011 §2.4: the
/// registry row commits in the transaction of the domain rows that reference it).
/// </summary>
public sealed partial class ImportBatchRepository
{
    public const string OrphanDocumentCode = "opt-orphan-document";
    public const string DuplicateDocumentCode = "opt-duplicate-document";
    public const string MissingDocumentBreakCode = "opt-missing-document-break";
    public const string InvalidImageKeyCode = "opt-image-key-invalid";
    public const string DocumentNotLoadedCode = "opt-document-not-loaded";
    public const string ImagesNotLinkedCode = "opt-images-not-linked";

    public async Task AddBatesKeysAsync(Guid workspaceId, Guid importBatchId, IReadOnlyList<ImportKey> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            return;
        }

        var first = keys.GroupBy(k => k.ControlNumberNorm, StringComparer.Ordinal).Select(g => g.MinBy(k => k.RowNo)!).ToList();
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var command = tx.Command(
            """
            INSERT INTO opportunity.import_batch_bates (workspace_id, import_batch_id, beg_bates_norm, row_no)
            SELECT @ws, @id, u.norm, u.row_no FROM unnest(@norms, @rows) AS u(norm, row_no)
            ON CONFLICT (workspace_id, import_batch_id, beg_bates_norm) DO NOTHING
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("id", importBatchId);
            command.Parameters.AddWithValue("norms", first.Select(k => k.ControlNumberNorm).ToArray());
            command.Parameters.AddWithValue("rows", first.Select(k => k.RowNo).ToArray());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddImageRowsAsync(Guid workspaceId, Guid importBatchId, IReadOnlyList<ImportImageRow> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return;
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var command = tx.Command(
            """
            INSERT INTO opportunity.import_batch_image
                (workspace_id, import_batch_id, opt_row, line_no, doc_no, is_break, image_key, volume, path, page_count, problem, match_key)
            SELECT @ws, @id, u.opt_row, u.line_no, u.doc_no, u.is_break, u.image_key, u.volume, u.path, u.page_count, u.problem, u.match_key
            FROM unnest(@opt_rows, @lines, @docs, @breaks, @keys, @volumes, @paths, @counts, @problems, @match_keys)
                AS u(opt_row, line_no, doc_no, is_break, image_key, volume, path, page_count, problem, match_key)
            ON CONFLICT (workspace_id, import_batch_id, opt_row) DO NOTHING
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("id", importBatchId);
            command.Parameters.AddWithValue("opt_rows", rows.Select(r => r.OptRow).ToArray());
            command.Parameters.AddWithValue("lines", rows.Select(r => r.LineNo).ToArray());
            command.Parameters.AddWithValue("docs", rows.Select(r => r.DocNo).ToArray());
            command.Parameters.AddWithValue("breaks", rows.Select(r => r.IsBreak).ToArray());
            command.Parameters.AddWithValue("keys", rows.Select(r => Truncate(r.ImageKey, 1000)!).ToArray());
            command.Parameters.AddWithValue("volumes", rows.Select(r => Truncate(r.Volume, 1000)!).ToArray());
            command.Parameters.AddWithValue("paths", rows.Select(r => Truncate(r.Path, 4000)!).ToArray());
            command.Parameters.Add(new NpgsqlParameter("counts", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = rows.Select(r => r.PageCount).ToArray() });
            command.Parameters.Add(new NpgsqlParameter("problems", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = rows.Select(r => Truncate(r.Problem, 1000)).ToArray() });
            command.Parameters.Add(new NpgsqlParameter("match_keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = rows.Select(r => Truncate(r.MatchKey, 1000)).ToArray() });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImportImageMatch> MatchImageRowsAsync(
        Guid workspaceId, Guid importBatchId, ImportImageMatchMode mode, CancellationToken cancellationToken = default)
    {
        // Each document break finds its row; the first OPT document wins a row (or, OPT-only, a key).
        var (join, rowExpr, partition, keyLabel) = mode switch
        {
            ImportImageMatchMode.ControlNumber => (
                """
                JOIN opportunity.import_batch_key k ON k.workspace_id = i.workspace_id AND k.import_batch_id = i.import_batch_id
                                                   AND k.control_number_norm = i.match_key
                """, "k.row_no", "k.row_no", "control number"),
            ImportImageMatchMode.BegBates => (
                """
                JOIN opportunity.import_batch_bates k ON k.workspace_id = i.workspace_id AND k.import_batch_id = i.import_batch_id
                                                     AND k.beg_bates_norm = i.match_key
                """, "k.row_no", "k.row_no", "Beg Bates"),
            ImportImageMatchMode.OptDocuments => (string.Empty, "i.doc_no", "i.match_key", "image key"),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var match = tx.Command(
            $"""
            WITH docs AS (
                SELECT i.doc_no, {rowExpr} AS row_no, row_number() OVER (PARTITION BY {partition} ORDER BY i.doc_no) AS rn
                FROM opportunity.import_batch_image i
                {join}
                WHERE i.workspace_id = @ws AND i.import_batch_id = @id AND i.is_break AND i.doc_no > 0 AND i.match_key IS NOT NULL)
            UPDATE opportunity.import_batch_image i SET row_no = d.row_no
            FROM docs d
            WHERE i.workspace_id = @ws AND i.import_batch_id = @id AND i.doc_no = d.doc_no AND d.rn = 1
              AND i.row_no IS DISTINCT FROM d.row_no
            """))
        {
            match.Parameters.AddWithValue("ws", workspaceId);
            match.Parameters.AddWithValue("id", importBatchId);
            await match.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // One warning on the first OPT row of every document that belongs to no row (OPT rows of the load file).
        await using (var orphans = tx.Command(
            """
            INSERT INTO opportunity.import_row_issue
                (workspace_id, import_batch_id, source, row_no, issue_no, severity, line_no, control_number, column_name, code, message)
            SELECT @ws, @id, 2, i.opt_row, 1, 2, i.line_no, left(i.image_key, 1000), 'ImageKey',
                   CASE WHEN i.doc_no = 0 THEN @code_break
                        WHEN i.match_key IS NULL THEN @code_invalid
                        WHEN i.won THEN @code_duplicate
                        ELSE @code_orphan END,
                   left(CASE WHEN i.doc_no = 0 THEN format('%s OPT row(s) come before the first document break (''Y'' in column 4) and belong to no document; their images were not loaded.', i.rows)
                        WHEN i.match_key IS NULL THEN format('The image key ''%s'' of this document break is not a valid key; its %s OPT row(s) were not loaded.', i.image_key, i.rows)
                        WHEN i.won THEN format('Image key ''%s'' already starts an earlier document of the OPT; this document''s %s OPT row(s) were not loaded.', i.image_key, i.rows)
                        ELSE format('Orphan OPT document: no row of the load has %s ''%s''; its %s OPT row(s) were not loaded.', @key_label, i.image_key, i.rows) END, 2000)
            FROM (
                SELECT DISTINCT ON (b.doc_no) b.opt_row, b.line_no, b.doc_no, b.image_key, b.match_key,
                       count(*) OVER (PARTITION BY b.doc_no) AS rows,
                       EXISTS (SELECT FROM opportunity.import_batch_image o
                               WHERE o.workspace_id = b.workspace_id AND o.import_batch_id = b.import_batch_id AND o.is_break
                                 AND o.match_key = b.match_key AND o.row_no IS NOT NULL AND o.doc_no < b.doc_no) AS won
                FROM opportunity.import_batch_image b
                WHERE b.workspace_id = @ws AND b.import_batch_id = @id AND b.row_no IS NULL
                ORDER BY b.doc_no, b.opt_row) i
            ON CONFLICT (workspace_id, import_batch_id, source, row_no, issue_no) DO NOTHING
            """))
        {
            orphans.Parameters.AddWithValue("ws", workspaceId);
            orphans.Parameters.AddWithValue("id", importBatchId);
            orphans.Parameters.AddWithValue("code_break", MissingDocumentBreakCode);
            orphans.Parameters.AddWithValue("code_invalid", InvalidImageKeyCode);
            orphans.Parameters.AddWithValue("code_duplicate", DuplicateDocumentCode);
            orphans.Parameters.AddWithValue("code_orphan", OrphanDocumentCode);
            orphans.Parameters.AddWithValue("key_label", keyLabel);
            await orphans.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        ImportImageMatch result;
        await using (var counts = tx.Command(
            """
            SELECT count(DISTINCT doc_no) FILTER (WHERE doc_no > 0),
                   count(DISTINCT doc_no) FILTER (WHERE row_no IS NOT NULL),
                   count(DISTINCT doc_no) FILTER (WHERE row_no IS NULL)
            FROM opportunity.import_batch_image WHERE workspace_id = @ws AND import_batch_id = @id
            """))
        {
            counts.Parameters.AddWithValue("ws", workspaceId);
            counts.Parameters.AddWithValue("id", importBatchId);
            await using var reader = await counts.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            result = new ImportImageMatch(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<ImportImageRow>> GetImageRowsAsync(
        Guid workspaceId, Guid importBatchId, long rowFrom, long rowTo, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT opt_row, line_no, doc_no, is_break, image_key, volume, path, page_count, problem, match_key, row_no
            FROM opportunity.import_batch_image
            WHERE workspace_id = @ws AND import_batch_id = @id AND row_no BETWEEN @from AND @to
            ORDER BY row_no, opt_row
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", importBatchId);
        command.Parameters.AddWithValue("from", rowFrom);
        command.Parameters.AddWithValue("to", rowTo);
        var rows = new List<ImportImageRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new ImportImageRow(
                    reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetBoolean(3), reader.GetString(4), reader.GetString(5),
                    reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetInt32(7), reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetInt64(10)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }

    public async Task<IReadOnlyDictionary<string, ImportExistingDocument>> FindDocumentsAsync(
        Guid workspaceId, IReadOnlyCollection<string> keys, ImportImageMatchMode by, bool caseSensitive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var result = new Dictionary<string, ImportExistingDocument>(StringComparer.Ordinal);
        if (keys.Count == 0)
        {
            return result;
        }

        var condition = by switch
        {
            ImportImageMatchMode.BegBates => "(CASE WHEN @cs THEN btrim(d.beg_bates) ELSE upper(btrim(d.beg_bates)) END) = u.k",
            _ => "d.control_number_norm = normalize(u.k, NFC)",
        };
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            SELECT u.k, d.document_id, d.control_number, d.control_number_norm
            FROM unnest(@keys) AS u(k)
            JOIN opportunity.document d ON d.workspace_id = @ws AND {condition}
            JOIN opportunity.document_projection_state s ON s.workspace_id = d.workspace_id AND s.document_id = d.document_id
            WHERE NOT s.is_deleted
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("keys", keys.Distinct(StringComparer.Ordinal).ToArray());
        command.Parameters.AddWithValue("cs", caseSensitive);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var key = reader.GetString(0);
                if (!result.TryAdd(key, new ImportExistingDocument(reader.GetGuid(1), reader.GetString(2), reader.GetString(3))))
                {
                    ambiguous.Add(key);
                }
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var key in ambiguous)
        {
            result.Remove(key);
        }

        return result;
    }

    // ---- the chunk transaction: Imported page sets ------------------------------------------------------------

    /// <summary>
    /// Writes the Imported page set of every created or overlaid document whose row carries images: registers the image
    /// objects, inserts the page set, its pages and original rasters, points the document at it and sets
    /// <c>images_incomplete</c>. An overlaid document whose active Imported set already has exactly these pages keeps
    /// it (an identical re-load changes nothing). Overlaid documents with a new set get a new DocumentVersion unless
    /// <paramref name="alreadyBumped"/>; their ids are returned.
    /// </summary>
    private static async Task<HashSet<Guid>> WritePageSetsAsync(
        WorkspaceTransaction tx, ImportBatchRecord batch, ChunkPlan plan, HashSet<Guid> alreadyBumped, CancellationToken cancellationToken)
    {
        var ws = tx.WorkspaceId;
        var changedOverlays = new HashSet<Guid>();
        foreach (var row in plan.ErroredRows.Where(r => r.Images is not null).DistinctBy(r => r.RowNo))
        {
            plan.OptIssues.Add(NotLoaded(row.Images!, DocumentNotLoadedCode,
                $"The document {row.ControlNumber} was not loaded (see its row errors), so its images were not linked."));
        }

        var targets = plan.Members.Where(m => m.Action == ActionImported).Select(m => (m.Row, m.DocumentId, Overlay: false))
            .Concat(plan.Overlays.Select(o => (o.Row, o.DocumentId, Overlay: true)))
            .Where(t => t.Row.Images is not null)
            .ToList();
        var sets = new List<(Guid DocumentId, bool Overlay, ImportImages Images)>();
        foreach (var (row, documentId, overlay) in targets)
        {
            if (row.Images!.DocumentId != documentId)
            {
                // The images were stored for another document id than the one the row ended up with (a concurrent load).
                plan.OptIssues.Add(NotLoaded(row.Images, ImagesNotLinkedCode,
                    $"The images of {row.ControlNumber} were stored before its document changed; they were not linked. Load the OPT again."));
                continue;
            }

            sets.Add((documentId, overlay, row.Images));
        }

        if (sets.Count == 0)
        {
            return changedOverlays;
        }

        var unchanged = await UnchangedPageSetsAsync(tx, [.. sets.Where(s => s.Overlay).Select(s => (s.DocumentId, s.Images))], cancellationToken)
            .ConfigureAwait(false);
        sets.RemoveAll(s => unchanged.Contains(s.DocumentId));
        if (sets.Count == 0)
        {
            return changedOverlays;
        }

        // Objects: content-addressed per document, so a key registered by an earlier load of the same bytes is reused.
        var objects = sets.SelectMany(s => s.Images.Objects.Select(o => (s.DocumentId, Object: o))).DistinctBy(o => o.Object.LogicalKey).ToList();
        var objectIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (objects.Count > 0)
        {
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
                insert.Parameters.AddWithValue("ws", ws);
                insert.Parameters.AddWithValue("area", (short)ObjectArea.Image);
                insert.Parameters.AddWithValue("state", (short)StoredObjectState.Committed);
                insert.Parameters.AddWithValue("job", batch.JobId);
                insert.Parameters.AddWithValue("ids", objects.Select(_ => Guid.CreateVersion7()).ToArray());
                insert.Parameters.AddWithValue("keys", objects.Select(o => o.Object.LogicalKey).ToArray());
                insert.Parameters.AddWithValue("documents", objects.Select(o => o.DocumentId).ToArray());
                insert.Parameters.AddWithValue("shas", objects.Select(o => o.Object.Sha256).ToArray());
                insert.Parameters.AddWithValue("sizes", objects.Select(o => o.Object.SizeBytes).ToArray());
                insert.Parameters.AddWithValue("types", objects.Select(o => o.Object.ContentType).ToArray());
                insert.Parameters.AddWithValue("key_ids", objects.Select(o => o.Object.KeyId).ToArray());
                insert.Parameters.AddWithValue("schemes", objects.Select(o => (short)o.Object.EncryptionScheme).ToArray());
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var select = tx.Command(
                "SELECT logical_key, object_id FROM opportunity.stored_object WHERE workspace_id = @ws AND logical_key = ANY(@keys)");
            select.Parameters.AddWithValue("ws", ws);
            select.Parameters.AddWithValue("keys", objects.Select(o => o.Object.LogicalKey).ToArray());
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                objectIds[reader.GetString(0)] = reader.GetGuid(1);
            }
        }

        var setIds = sets.Select(_ => Guid.CreateVersion7()).ToArray();
        await using (var insertSets = tx.Command(
            """
            INSERT INTO opportunity.page_set (workspace_id, page_set_id, document_id, source, import_job_id, page_count, status)
            SELECT @ws, u.page_set_id, u.document_id, @source, @job, u.page_count, u.status
            FROM unnest(@ids, @documents, @counts, @statuses) AS u(page_set_id, document_id, page_count, status)
            """))
        {
            insertSets.Parameters.AddWithValue("ws", ws);
            insertSets.Parameters.AddWithValue("source", (short)PageSetSource.Imported);
            insertSets.Parameters.AddWithValue("job", batch.JobId);
            insertSets.Parameters.AddWithValue("ids", setIds);
            insertSets.Parameters.AddWithValue("documents", sets.Select(s => s.DocumentId).ToArray());
            insertSets.Parameters.AddWithValue("counts", sets.Select(s => s.Images.Pages.Count).ToArray());
            insertSets.Parameters.AddWithValue("statuses", sets.Select(s => (short)(s.Images.Incomplete ? PageSetStatus.Incomplete : PageSetStatus.Ready)).ToArray());
            await insertSets.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var pages = sets.SelectMany((s, i) => s.Images.Pages.Select(p => (SetId: setIds[i], s.DocumentId, Page: p))).ToList();
        if (pages.Count > 0)
        {
            await using (var insertPages = tx.Command(
                """
                INSERT INTO opportunity.page
                    (workspace_id, page_set_id, ordinal, document_id, image_key, width_pt, height_pt, rotation, color_mode, source_frame, image_missing)
                SELECT @ws, u.page_set_id, u.ordinal, u.document_id, u.image_key, u.width_pt, u.height_pt, 0, u.color_mode, u.source_frame, u.missing
                FROM unnest(@sets, @ordinals, @documents, @keys, @widths, @heights, @colors, @frames, @missing)
                    AS u(page_set_id, ordinal, document_id, image_key, width_pt, height_pt, color_mode, source_frame, missing)
                """))
            {
                insertPages.Parameters.AddWithValue("ws", ws);
                insertPages.Parameters.AddWithValue("sets", pages.Select(p => p.SetId).ToArray());
                insertPages.Parameters.AddWithValue("ordinals", pages.Select(p => p.Page.Ordinal).ToArray());
                insertPages.Parameters.AddWithValue("documents", pages.Select(p => p.DocumentId).ToArray());
                insertPages.Parameters.Add(new NpgsqlParameter("keys", NpgsqlDbType.Array | NpgsqlDbType.Text)
                {
                    Value = pages.Select(p => Truncate(p.Page.ImageKey, 1000)).ToArray(),
                });
                insertPages.Parameters.AddWithValue("widths", pages.Select(p => Geometry(p.Page).Width).ToArray());
                insertPages.Parameters.AddWithValue("heights", pages.Select(p => Geometry(p.Page).Height).ToArray());
                insertPages.Parameters.AddWithValue("colors", pages.Select(p => (short)(p.Page.Raster?.ColorMode ?? PageColorMode.Bitonal)).ToArray());
                insertPages.Parameters.AddWithValue("frames", pages.Select(p => p.Page.SourceFrame).ToArray());
                insertPages.Parameters.AddWithValue("missing", pages.Select(p => p.Page.ImageMissing).ToArray());
                await insertPages.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var rasters = pages.Where(p => p.Page.Raster is not null).ToList();
            if (rasters.Count > 0)
            {
                await using var insertImages = tx.Command(
                    """
                    INSERT INTO opportunity.page_image (workspace_id, page_set_id, ordinal, purpose, object_id, width_px, height_px, dpi_x, dpi_y, format)
                    SELECT @ws, u.page_set_id, u.ordinal, @purpose, u.object_id, u.width_px, u.height_px, u.dpi_x, u.dpi_y, u.format
                    FROM unnest(@sets, @ordinals, @objects, @widths, @heights, @dpi_x, @dpi_y, @formats)
                        AS u(page_set_id, ordinal, object_id, width_px, height_px, dpi_x, dpi_y, format)
                    """);
                insertImages.Parameters.AddWithValue("ws", ws);
                insertImages.Parameters.AddWithValue("purpose", (short)PageImagePurpose.Original);
                insertImages.Parameters.AddWithValue("sets", rasters.Select(p => p.SetId).ToArray());
                insertImages.Parameters.AddWithValue("ordinals", rasters.Select(p => p.Page.Ordinal).ToArray());
                insertImages.Parameters.AddWithValue("objects", rasters.Select(p => objectIds[p.Page.Raster!.LogicalKey]).ToArray());
                insertImages.Parameters.AddWithValue("widths", rasters.Select(p => p.Page.Raster!.WidthPx).ToArray());
                insertImages.Parameters.AddWithValue("heights", rasters.Select(p => p.Page.Raster!.HeightPx).ToArray());
                insertImages.Parameters.AddWithValue("dpi_x", rasters.Select(p => p.Page.Raster!.DpiX).ToArray());
                insertImages.Parameters.AddWithValue("dpi_y", rasters.Select(p => p.Page.Raster!.DpiY).ToArray());
                insertImages.Parameters.AddWithValue("formats", rasters.Select(p => (short)p.Page.Raster!.Format).ToArray());
                await insertImages.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // ADR-012 §1.2: the Imported set becomes active, except that an Incomplete one does not replace a Ready Rendered set.
        await using (var activate = tx.Command(
            """
            UPDATE opportunity.document d
            SET active_page_set_id = CASE
                    WHEN u.ready OR cur.page_set_id IS NULL OR cur.source = @imported OR cur.status <> @ready_status THEN u.page_set_id
                    ELSE d.active_page_set_id END,
                images_incomplete = NOT u.ready,
                updated_at = now()
            FROM unnest(@documents, @sets, @ready) AS u(document_id, page_set_id, ready)
            LEFT JOIN opportunity.document cd ON cd.workspace_id = @ws AND cd.document_id = u.document_id
            LEFT JOIN opportunity.page_set cur ON cur.workspace_id = cd.workspace_id AND cur.page_set_id = cd.active_page_set_id
            WHERE d.workspace_id = @ws AND d.document_id = u.document_id
            """))
        {
            activate.Parameters.AddWithValue("ws", ws);
            activate.Parameters.AddWithValue("imported", (short)PageSetSource.Imported);
            activate.Parameters.AddWithValue("ready_status", (short)PageSetStatus.Ready);
            activate.Parameters.AddWithValue("documents", sets.Select(s => s.DocumentId).ToArray());
            activate.Parameters.AddWithValue("sets", setIds);
            activate.Parameters.AddWithValue("ready", sets.Select(s => !s.Images.Incomplete).ToArray());
            await activate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        changedOverlays.UnionWith(sets.Where(s => s.Overlay).Select(s => s.DocumentId));
        var bump = changedOverlays.Where(id => !alreadyBumped.Contains(id)).ToArray();
        if (bump.Length > 0)
        {
            await using var command = tx.Command(
                """
                UPDATE opportunity.document_projection_state SET document_version = document_version + 1
                WHERE workspace_id = @ws AND document_id = ANY(@ids)
                """);
            command.Parameters.AddWithValue("ws", ws);
            command.Parameters.AddWithValue("ids", bump);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return changedOverlays;
    }

    /// <summary>Overlaid documents whose active Imported page set already holds exactly the given pages and rasters.</summary>
    private static async Task<HashSet<Guid>> UnchangedPageSetsAsync(
        WorkspaceTransaction tx, IReadOnlyList<(Guid DocumentId, ImportImages Images)> candidates, CancellationToken cancellationToken)
    {
        var unchanged = new HashSet<Guid>();
        if (candidates.Count == 0)
        {
            return unchanged;
        }

        var current = new Dictionary<Guid, List<(int Ordinal, string? Key, int Frame, string? LogicalKey)>>();
        await using (var command = tx.Command(
            """
            SELECT d.document_id, p.ordinal, p.image_key, p.source_frame, o.logical_key
            FROM opportunity.document d
            JOIN opportunity.page_set ps ON ps.workspace_id = d.workspace_id AND ps.page_set_id = d.active_page_set_id AND ps.source = @imported
            JOIN opportunity.page p ON p.workspace_id = ps.workspace_id AND p.page_set_id = ps.page_set_id
            LEFT JOIN opportunity.page_image pi ON pi.workspace_id = p.workspace_id AND pi.page_set_id = p.page_set_id
                                               AND pi.ordinal = p.ordinal AND pi.purpose = @original
            LEFT JOIN opportunity.stored_object o ON o.workspace_id = pi.workspace_id AND o.object_id = pi.object_id
            WHERE d.workspace_id = @ws AND d.document_id = ANY(@ids)
            ORDER BY d.document_id, p.ordinal
            """))
        {
            command.Parameters.AddWithValue("ws", tx.WorkspaceId);
            command.Parameters.AddWithValue("imported", (short)PageSetSource.Imported);
            command.Parameters.AddWithValue("original", (short)PageImagePurpose.Original);
            command.Parameters.AddWithValue("ids", candidates.Select(c => c.DocumentId).ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetGuid(0);
                if (!current.TryGetValue(id, out var list))
                {
                    list = [];
                    current[id] = list;
                }

                list.Add((reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        foreach (var (documentId, images) in candidates)
        {
            if (current.TryGetValue(documentId, out var pages) && pages.Count == images.Pages.Count
                && pages.Zip(images.Pages).All(x => x.First.Ordinal == x.Second.Ordinal && x.First.Key == Truncate(x.Second.ImageKey, 1000)
                    && x.First.Frame == x.Second.SourceFrame && x.First.LogicalKey == x.Second.Raster?.LogicalKey))
            {
                unchanged.Add(documentId);
            }
        }

        return unchanged;
    }

    /// <summary>Page geometry in points; a page without an image gets US Letter so the viewer can still lay it out.</summary>
    private static (decimal Width, decimal Height) Geometry(ImportPage page) => page.Raster is { } r
        ? (Math.Round(r.WidthPx * 72m / r.DpiX, 2), Math.Round(r.HeightPx * 72m / r.DpiY, 2))
        : (612m, 792m);

    private static ImportOptIssue NotLoaded(ImportImages images, string code, string message) =>
        new(images.BreakOptRow, images.BreakLineNo, images.BreakImageKey, new ImportRowIssue(ImportIssueSeverity.Warning, code, message, "ImageKey"));
}
