using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Content;
using Opportunity.Core.Pages;
using Opportunity.Core.Storage;
using Opportunity.Data.Coding;
using Opportunity.Data.Fields;
using Opportunity.Data.Search;

namespace Opportunity.Data.Documents;

/// <summary>
/// PostgreSQL reads of the document viewer (E11-T01): the document with its field catalogue, current coding, raw
/// imported strings and artifact facts in one <c>REPEATABLE READ</c> snapshot, and keyset pages of its active page set
/// (ADR-012) with the images the protected-content gateway can serve. Runs inside the workspace's RLS context; a
/// deleted document reads as absent. Object keys are never selected.
/// </summary>
public sealed class DocumentViewerCatalog(NpgsqlDataSource dataSource) : IDocumentViewerCatalog
{
    private const string ExtrasSql =
        """
        SELECT w.display_time_zone, d.metadata_raw::text,
               n.size_bytes, n.state, t.size_bytes, t.state,
               ps.page_set_id, ps.source, ps.status,
               (SELECT count(*)::int FROM opportunity.page pg
                WHERE pg.workspace_id = ps.workspace_id AND pg.page_set_id = ps.page_set_id)
        FROM opportunity.document d
        JOIN opportunity.workspace w ON w.workspace_id = d.workspace_id
        LEFT JOIN opportunity.stored_object n
               ON n.workspace_id = d.workspace_id AND n.object_id = d.native_object_id AND n.area = @nativeArea
        LEFT JOIN opportunity.stored_object t
               ON t.workspace_id = d.workspace_id AND t.object_id = d.text_object_id AND t.area = @textArea
        LEFT JOIN opportunity.page_set ps
               ON ps.workspace_id = d.workspace_id AND ps.document_id = d.document_id AND ps.page_set_id = d.active_page_set_id
        WHERE d.workspace_id = @ws AND d.document_id = @doc
        """;

    private const string PageSetSql =
        """
        SELECT ps.page_set_id, ps.source, ps.status,
               (SELECT count(*)::int FROM opportunity.page pg
                WHERE pg.workspace_id = ps.workspace_id AND pg.page_set_id = ps.page_set_id)
        FROM opportunity.document d
        JOIN opportunity.document_projection_state s
          ON s.workspace_id = d.workspace_id AND s.document_id = d.document_id AND NOT s.is_deleted
        LEFT JOIN opportunity.page_set ps
               ON ps.workspace_id = d.workspace_id AND ps.document_id = d.document_id AND ps.page_set_id = d.active_page_set_id
        WHERE d.workspace_id = @ws AND d.document_id = @doc
        """;

    // The same image choice as DocumentContentCatalog: review before original, inline formats only, registered object.
    private const string PagesSql =
        """
        SELECT p.ordinal, p.width_pt, p.height_pt, p.rotation, p.color_mode, p.image_missing,
               img.format, img.width_px, img.height_px, thumb.present
        FROM opportunity.page p
        LEFT JOIN LATERAL (
            SELECT pi.format, pi.width_px, pi.height_px
            FROM opportunity.page_image pi
            JOIN opportunity.stored_object o
              ON o.workspace_id = pi.workspace_id AND o.object_id = pi.object_id AND o.area = ANY(@areas) AND o.state = @committed
            WHERE pi.workspace_id = p.workspace_id AND pi.page_set_id = p.page_set_id AND pi.ordinal = p.ordinal
              AND pi.purpose = ANY(@purposes) AND pi.format = ANY(@formats)
            ORDER BY array_position(@purposes, pi.purpose)
            LIMIT 1) img ON true
        LEFT JOIN LATERAL (
            SELECT true AS present
            FROM opportunity.page_image pi
            JOIN opportunity.stored_object o
              ON o.workspace_id = pi.workspace_id AND o.object_id = pi.object_id AND o.area = ANY(@areas) AND o.state = @committed
            WHERE pi.workspace_id = p.workspace_id AND pi.page_set_id = p.page_set_id AND pi.ordinal = p.ordinal
              AND pi.purpose = @thumbnail AND pi.format = ANY(@formats)
            LIMIT 1) thumb ON true
        WHERE p.workspace_id = @ws AND p.page_set_id = @ps AND p.document_id = @doc AND p.ordinal > @after
        ORDER BY p.ordinal
        LIMIT @limit
        """;

    private static readonly short[] InlineFormats = [(short)PageImageFormat.Jpeg, (short)PageImageFormat.Png, (short)PageImageFormat.WebP];
    private static readonly short[] ImageAreas = [(short)ObjectArea.Image, (short)ObjectArea.Rendition];
    private static readonly short[] ReviewPurposes = [(short)PageImagePurpose.Review, (short)PageImagePurpose.Original];

    public async Task<DocumentViewerRecord?> GetDocumentAsync(Guid workspaceId, Guid documentId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);

        Core.Documents.Document? document = null;
        long version = 0;
        await using (var command = tx.Command(ProjectionSourceReader.DocumentSql))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("ids", new[] { documentId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && !reader.GetBoolean(2))
            {
                version = reader.GetInt64(1);
                document = ProjectionSourceReader.ReadDocument(reader, workspaceId);
            }
        }

        if (document is null)
        {
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        string timeZone;
        string? raw;
        StoredArtifact? native, text;
        PageSetSummary? pageSet;
        await using (var command = tx.Command(ExtrasSql))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("doc", documentId);
            command.Parameters.AddWithValue("nativeArea", (short)ObjectArea.Native);
            command.Parameters.AddWithValue("textArea", (short)ObjectArea.Text);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            timeZone = reader.GetString(0);
            raw = reader.IsDBNull(1) ? null : reader.GetString(1);
            native = Artifact(reader, 2);
            text = Artifact(reader, 4);
            pageSet = PageSet(reader, 6);
        }

        var catalog = await FieldCatalogRepository.LoadCatalogAsync(tx, workspaceId, includeDeleted: false, cancellationToken).ConfigureAwait(false);
        var coding = await CodingRepository.ReadCurrentValuesAsync(tx, workspaceId, [documentId], cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new DocumentViewerRecord(
            document,
            version,
            catalog,
            coding.GetValueOrDefault(documentId) ?? [],
            RawStrings(raw),
            timeZone,
            native,
            text,
            pageSet);
    }

    public async Task<DocumentPageList?> GetPagesAsync(
        Guid workspaceId, Guid documentId, int afterPage, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);

        PageSetSummary? pageSet;
        await using (var command = tx.Command(PageSetSql))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("doc", documentId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            pageSet = PageSet(reader, 0);
        }

        var pages = new List<DocumentPageInfo>();
        if (pageSet is not null)
        {
            await using var command = tx.Command(PagesSql);
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("doc", documentId);
            command.Parameters.AddWithValue("ps", pageSet.PageSetId);
            command.Parameters.AddWithValue("after", Math.Max(0, afterPage));
            command.Parameters.AddWithValue("limit", limit);
            command.Parameters.AddWithValue("committed", (short)StoredObjectState.Committed);
            command.Parameters.AddWithValue("thumbnail", (short)PageImagePurpose.Thumbnail);
            command.Parameters.Add(new NpgsqlParameter<short[]>("purposes", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { TypedValue = ReviewPurposes });
            command.Parameters.Add(new NpgsqlParameter<short[]>("formats", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { TypedValue = InlineFormats });
            command.Parameters.Add(new NpgsqlParameter<short[]>("areas", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { TypedValue = ImageAreas });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var hasImage = !reader.IsDBNull(6);
                pages.Add(new DocumentPageInfo(
                    reader.GetInt32(0),
                    reader.GetDecimal(1),
                    reader.GetDecimal(2),
                    reader.GetInt16(3),
                    (PageColorMode)reader.GetInt16(4),
                    reader.GetBoolean(5),
                    hasImage ? (PageImageFormat)reader.GetInt16(6) : null,
                    hasImage ? reader.GetInt32(7) : null,
                    hasImage ? reader.GetInt32(8) : null,
                    !reader.IsDBNull(9)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new DocumentPageList(pageSet, pages);
    }

    private static StoredArtifact? Artifact(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : new StoredArtifact(reader.GetInt64(ordinal), (StoredObjectState)reader.GetInt16(ordinal + 1) == StoredObjectState.Quarantined);

    private static PageSetSummary? PageSet(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : new PageSetSummary(
                reader.GetGuid(ordinal),
                (PageSetSource)reader.GetInt16(ordinal + 1),
                (PageSetStatus)reader.GetInt16(ordinal + 2),
                reader.GetInt32(ordinal + 3));

    /// <summary>The <c>raw</c> strings of <c>MetadataRaw</c> entries keyed <c>f{FieldId}</c> (ADR-003 R9).</summary>
    private static Dictionary<int, string> RawStrings(string? json)
    {
        var values = new Dictionary<int, string>();
        if (json is null)
        {
            return values;
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return values;
        }

        foreach (var (key, entry) in root ?? [])
        {
            if (Core.Fields.FieldKey.TryParse(key, out var fieldId)
                && entry is JsonObject { } obj && obj["raw"] is JsonValue rawValue && rawValue.TryGetValue<string>(out var text))
            {
                values[fieldId] = text;
            }
        }

        return values;
    }
}
