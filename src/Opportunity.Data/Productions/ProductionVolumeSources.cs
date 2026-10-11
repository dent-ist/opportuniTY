using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Exports;
using Opportunity.Application.Productions;
using Opportunity.Core.Pages;
using Opportunity.Core.Redactions;
using Opportunity.Core.Storage;

namespace Opportunity.Data.Productions;

/// <summary>
/// What a production volume run reads (E12-T05, V0052): the page sets and redaction versions frozen at finalization,
/// the stored image of every frozen page, and each member's redactions as of its frozen version (ADR-012 §3.5).
/// </summary>
public sealed partial class ProductionRepository
{
    private static readonly short[] SourcePurposes = [(short)PageImagePurpose.Original, (short)PageImagePurpose.Review];

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<ProducedSourcePage>>> ReadSourcePagesAsync(
        Guid workspaceId, IReadOnlyCollection<(Guid DocumentId, Guid PageSetId)> pageSets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pageSets);
        var pages = new Dictionary<Guid, List<ProducedSourcePage>>();
        if (pageSets.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<ProducedSourcePage>>();
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT p.document_id, p.page_set_id, p.ordinal, p.source_frame, o.logical_key, o.sha256, o.size_bytes, o.content_type, img.format
              FROM unnest(@documents, @sets) AS s(document_id, page_set_id)
              JOIN opportunity.page p ON p.workspace_id = @ws AND p.page_set_id = s.page_set_id AND p.document_id = s.document_id
              LEFT JOIN LATERAL (
                  SELECT pi.object_id, pi.format
                    FROM opportunity.page_image pi
                    JOIN opportunity.stored_object so
                      ON so.workspace_id = pi.workspace_id AND so.object_id = pi.object_id AND so.state = @committed
                   WHERE pi.workspace_id = p.workspace_id AND pi.page_set_id = p.page_set_id AND pi.ordinal = p.ordinal
                     AND pi.purpose = ANY(@purposes)
                   ORDER BY array_position(@purposes, pi.purpose)
                   LIMIT 1) img ON NOT p.image_missing
              LEFT JOIN opportunity.stored_object o ON o.workspace_id = p.workspace_id AND o.object_id = img.object_id
             ORDER BY p.document_id, p.ordinal
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("documents", pageSets.Select(p => p.DocumentId).ToArray());
        command.Parameters.AddWithValue("sets", pageSets.Select(p => p.PageSetId).ToArray());
        command.Parameters.AddWithValue("committed", (short)StoredObjectState.Committed);
        command.Parameters.Add(new NpgsqlParameter<short[]>("purposes", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { TypedValue = SourcePurposes });
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetGuid(0);
                if (!pages.TryGetValue(id, out var list))
                {
                    pages[id] = list = [];
                }

                var image = reader.IsDBNull(4)
                    ? null
                    : new ExportSourceObject(reader.GetString(4), reader.GetFieldValue<byte[]>(5), reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetString(7));
                list.Add(new ProducedSourcePage(reader.GetInt32(2), reader.GetGuid(1), image,
                    image is null || reader.IsDBNull(8) ? null : (PageImageFormat)reader.GetInt16(8), reader.GetInt32(3)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return pages.ToDictionary(p => p.Key, p => (IReadOnlyList<ProducedSourcePage>)p.Value);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<FrozenRedaction>>> ReadFrozenRedactionsAsync(
        Guid workspaceId, Guid redactionSetId, IReadOnlyCollection<(Guid DocumentId, long Version)> documents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var result = new Dictionary<Guid, List<FrozenRedaction>>();
        var wanted = documents.Where(d => d.Version > 0).ToList();
        if (wanted.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<FrozenRedaction>>();
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            WITH wanted AS (
                SELECT * FROM unnest(@documents, @versions) AS w(document_id, version)
            ), latest AS (
                SELECT DISTINCT ON (r.document_id, r.redaction_id) r.*
                  FROM opportunity.redaction_revision r
                  JOIN wanted w ON w.document_id = r.document_id
                 WHERE r.workspace_id = @ws AND r.redaction_set_id = @set AND r.redaction_version <= w.version
                 ORDER BY r.document_id, r.redaction_id, r.redaction_version DESC
            )
            SELECT l.document_id, l.redaction_id, l.page_set_id, l.ordinal, l.x, l.y, l.w, l.h, l.redaction_type, coalesce(rr.box_label, 'Redacted')
              FROM latest l
              LEFT JOIN opportunity.redaction_reason rr ON rr.workspace_id = l.workspace_id AND rr.code = l.reason_code
             WHERE l.operation <> 3
             ORDER BY l.document_id, l.ordinal, l.y, l.x, l.redaction_id
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("set", redactionSetId);
        command.Parameters.AddWithValue("documents", wanted.Select(d => d.DocumentId).ToArray());
        command.Parameters.AddWithValue("versions", wanted.Select(d => d.Version).ToArray());
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetGuid(0);
                if (!result.TryGetValue(id, out var list))
                {
                    result[id] = list = [];
                }

                list.Add(new FrozenRedaction(reader.GetGuid(1), reader.GetGuid(2), reader.GetInt32(3),
                    new NormalizedRect(reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7)),
                    (RedactionType)reader.GetInt16(8), reader.GetString(9)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result.ToDictionary(p => p.Key, p => (IReadOnlyList<FrozenRedaction>)p.Value);
    }

    /// <summary>
    /// Freezes every member's active page set and its redaction version and count in the production's Redaction Set
    /// (<paramref name="set"/>, resolved by <see cref="ResolveRedactionSetAsync"/>; null when the workspace has none).
    /// </summary>
    private static async Task FreezeRedactionsAsync(WorkspaceTransaction tx, Guid productionId, Guid? set, CancellationToken cancellationToken)
    {
        await using var freeze = tx.Command(
            """
            UPDATE opportunity.production_document pd
               SET page_set_id = d.active_page_set_id,
                   redaction_set_id = @set,
                   redaction_version = CASE WHEN @set::uuid IS NULL THEN NULL ELSE coalesce(st.current_version, 0) END,
                   redaction_count = CASE WHEN @set::uuid IS NULL THEN NULL ELSE coalesce(st.active_count, 0) END
              FROM opportunity.document d
              LEFT JOIN opportunity.document_redaction_state st
                     ON st.workspace_id = d.workspace_id AND st.document_id = d.document_id AND st.redaction_set_id = @set
             WHERE pd.workspace_id = @ws AND pd.production_id = @id AND d.workspace_id = pd.workspace_id AND d.document_id = pd.document_id
            """);
        freeze.Parameters.AddWithValue("ws", tx.WorkspaceId);
        freeze.Parameters.AddWithValue("id", productionId);
        freeze.Parameters.Add(Nullable("set", NpgsqlDbType.Uuid, set));
        await freeze.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
