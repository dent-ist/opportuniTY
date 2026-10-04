using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Content;
using Opportunity.Core.Pages;
using Opportunity.Core.Storage;

namespace Opportunity.Data.Documents;

/// <summary>
/// Content locations for the protected-content gateway (E05-T04): the document's native or text object, or one page
/// image of its active page set, joined to the object registry (ADR-011 §2.3, ADR-012). Reads run inside the
/// workspace's RLS context; a deleted document is reported as absent. Only images in an inline-safe format (PNG, JPEG,
/// WebP) are offered, and an object whose registry area does not match the rendition is treated as missing.
/// </summary>
public sealed class DocumentContentCatalog(NpgsqlDataSource dataSource) : IDocumentContentCatalog
{
    public const string TextContentType = "text/plain; charset=utf-8";
    public const string NativeContentType = "application/octet-stream";

    private const string NativeSql = $"""
        {DocumentObjectSelect} o.object_id = d.native_object_id {DocumentObjectWhere}
        """;

    private const string TextSql = $"""
        {DocumentObjectSelect} o.object_id = d.text_object_id {DocumentObjectWhere}
        """;

    private const string DocumentObjectSelect = """
        SELECT d.control_number, d.file_extension, o.object_id, o.logical_key, o.sha256, o.size_bytes, o.state, NULL::smallint,
               d.text_truncated, d.text_missing, d.text_encoding_warning
        FROM opportunity.document d
        LEFT JOIN opportunity.document_projection_state s
               ON s.workspace_id = d.workspace_id AND s.document_id = d.document_id
        LEFT JOIN opportunity.stored_object o
               ON o.workspace_id = d.workspace_id AND
        """;

    private const string DocumentObjectWhere = """
        AND o.document_id = d.document_id AND o.area = @area
        WHERE d.workspace_id = @ws AND d.document_id = @doc AND s.is_deleted IS NOT TRUE
        """;

    private const string PageImageSql = """
        SELECT d.control_number, d.file_extension, o.object_id, o.logical_key, o.sha256, o.size_bytes, o.state, img.format,
               d.text_truncated, d.text_missing, d.text_encoding_warning
        FROM opportunity.document d
        LEFT JOIN opportunity.document_projection_state s
               ON s.workspace_id = d.workspace_id AND s.document_id = d.document_id
        LEFT JOIN LATERAL (
            SELECT pi.object_id, pi.format
            FROM opportunity.page_image pi
            WHERE pi.workspace_id = d.workspace_id AND pi.page_set_id = d.active_page_set_id AND pi.ordinal = @page
              AND pi.purpose = ANY(@purposes) AND pi.format = ANY(@formats)
            ORDER BY array_position(@purposes, pi.purpose)
            LIMIT 1) img ON true
        LEFT JOIN opportunity.stored_object o
               ON o.workspace_id = d.workspace_id AND o.object_id = img.object_id AND o.area = ANY(@areas)
        WHERE d.workspace_id = @ws AND d.document_id = @doc AND s.is_deleted IS NOT TRUE
        """;

    private static readonly short[] InlineFormats = [(short)PageImageFormat.Jpeg, (short)PageImageFormat.Png, (short)PageImageFormat.WebP];

    // Rendered review images live under rend/ (Rendition); imported images under image/ (Image).
    private static readonly short[] ImageAreas = [(short)ObjectArea.Image, (short)ObjectArea.Rendition];

    public async Task<DocumentContent?> FindAsync(
        Guid workspaceId, Guid documentId, ContentRendition rendition, int? pageNumber, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = Build(tx, rendition, pageNumber);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("doc", documentId);

        DocumentContent? content = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ContentLocation? location = null;
                if (!reader.IsDBNull(2))
                {
                    location = new ContentLocation(
                        reader.GetGuid(2),
                        reader.GetString(3),
                        reader.GetFieldValue<byte[]>(4),
                        reader.GetInt64(5),
                        ContentType(rendition, reader.IsDBNull(7) ? null : (PageImageFormat)reader.GetInt16(7)),
                        (StoredObjectState)reader.GetInt16(6) == StoredObjectState.Quarantined);
                }

                content = new DocumentContent(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    location,
                    reader.GetBoolean(8),
                    reader.GetBoolean(9),
                    reader.GetBoolean(10));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return content;
    }

    private static NpgsqlCommand Build(WorkspaceTransaction tx, ContentRendition rendition, int? pageNumber)
    {
        switch (rendition)
        {
            case ContentRendition.Native:
            case ContentRendition.Text:
                {
                    var native = rendition == ContentRendition.Native;
                    var command = tx.Command(native ? NativeSql : TextSql);
                    command.Parameters.AddWithValue("area", (short)(native ? ObjectArea.Native : ObjectArea.Text));
                    return command;
                }

            case ContentRendition.PageImage:
            case ContentRendition.Thumbnail:
                {
                    var command = tx.Command(PageImageSql);
                    command.Parameters.AddWithValue("page", pageNumber ?? 0);
                    short[] purposes = rendition == ContentRendition.Thumbnail
                        ? [(short)PageImagePurpose.Thumbnail]
                        : [(short)PageImagePurpose.Review, (short)PageImagePurpose.Original];
                    command.Parameters.Add(new NpgsqlParameter<short[]>("purposes", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { TypedValue = purposes });
                    command.Parameters.Add(new NpgsqlParameter<short[]>("formats", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { TypedValue = InlineFormats });
                    command.Parameters.Add(new NpgsqlParameter<short[]>("areas", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { TypedValue = ImageAreas });
                    return command;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(rendition), rendition, null);
        }
    }

    /// <summary>The allow-listed response type (ADR-015 D12.2); never the stored or load-file-declared type.</summary>
    private static string ContentType(ContentRendition rendition, PageImageFormat? format) => rendition switch
    {
        ContentRendition.Native => NativeContentType,
        ContentRendition.Text => TextContentType,
        _ => format switch
        {
            PageImageFormat.Png => "image/png",
            PageImageFormat.Jpeg => "image/jpeg",
            PageImageFormat.WebP => "image/webp",
            _ => NativeContentType,
        },
    };
}
