using System.Data;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using Opportunity.Application.Search.Projection;
using Opportunity.Core.Documents;
using Opportunity.Data.Coding;
using Opportunity.Data.Fields;

namespace Opportunity.Data.Search;

/// <summary>
/// PostgreSQL implementation of <see cref="IProjectionSourceReader"/>: the field catalogue, the document rows with
/// their DocumentVersion, the current coding and the text object key, all in one <c>REPEATABLE READ</c> snapshot
/// (ADR-001 §3 read-consistency rule).
/// </summary>
public sealed class ProjectionSourceReader(NpgsqlDataSource dataSource) : IProjectionSourceReader
{
    public const int MaxDocumentsPerRead = 5_000;

    private const string DocumentSql =
        """
        SELECT s.document_id, s.document_version, s.is_deleted,
               d.control_number, d.control_number_norm, d.control_number_sort_key, d.beg_bates, d.end_bates,
               d.beg_attach, d.end_attach, d.family_id, d.parent_document_id, d.family_sequence, d.family_status,
               d.duplicate_group_id, d.is_duplicate_primary, d.email_thread_id, d.email_thread_source,
               d.md5, d.sha1, d.sha256, d.upstream_dedupe_hash, d.file_name, d.file_extension, d.file_type, d.mime_type,
               d.file_size, d.page_count, d.date_sent, d.date_received, d.date_created, d.date_last_modified,
               d.document_date, d.document_date_source, d.family_date, d.native_object_id, d.text_object_id,
               d.text_length, d.active_page_set_id, d.text_truncated, d.text_missing, d.native_missing,
               d.images_incomplete, d.text_encoding_warning, d.metadata::text, o.logical_key
        FROM opportunity.document_projection_state s
        JOIN opportunity.document d ON d.workspace_id = s.workspace_id AND d.document_id = s.document_id
        LEFT JOIN opportunity.stored_object o ON o.workspace_id = d.workspace_id AND o.object_id = d.text_object_id
        WHERE s.workspace_id = @ws AND s.document_id = ANY(@ids)
        """;

    public async Task<ProjectionSourceBatch> ReadAsync(
        Guid workspaceId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        var ids = documentIds.Distinct().ToArray();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ids.Length, MaxDocumentsPerRead, nameof(documentIds));

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);
        var catalog = await FieldCatalogRepository.LoadCatalogAsync(tx, workspaceId, includeDeleted: false, cancellationToken)
            .ConfigureAwait(false);

        var rows = new Dictionary<Guid, (long Version, bool Deleted, Document Document, string? TextKey)>();
        await using (var command = tx.Command(DocumentSql))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("ids", ids);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var document = ReadDocument(reader, workspaceId);
                rows[document.DocumentId] = (reader.GetInt64(1), reader.GetBoolean(2), document, NullableString(reader, 45));
            }
        }

        // Coding tables stay private to the coding adapter (E04-T04); it reads them inside this snapshot.
        var coding = await CodingRepository.ReadCurrentValuesAsync(tx, workspaceId, ids, cancellationToken).ConfigureAwait(false);

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        var sources = new List<ProjectionSource>(ids.Length);
        foreach (var id in ids)
        {
            if (!rows.TryGetValue(id, out var row))
            {
                sources.Add(ProjectionSource.Missing(workspaceId, id));
            }
            else if (row.Deleted)
            {
                sources.Add(new ProjectionSource
                {
                    WorkspaceId = workspaceId,
                    DocumentId = id,
                    State = ProjectionSourceState.Deleted,
                    DocumentVersion = row.Version,
                });
            }
            else
            {
                sources.Add(new ProjectionSource
                {
                    WorkspaceId = workspaceId,
                    DocumentId = id,
                    State = ProjectionSourceState.Live,
                    DocumentVersion = row.Version,
                    Document = row.Document,
                    Coding = coding.GetValueOrDefault(id) ?? [],

                    // Restriction classes and walls (Q-11, M3) have no authoritative store yet; until they do, no
                    // document carries a security tag.
                    SecurityTags = [],
                    TextObjectKey = row.Document.TextObjectId is null ? null : row.TextKey,
                });
            }
        }

        return new ProjectionSourceBatch(workspaceId, catalog, sources);
    }

    private static Document ReadDocument(NpgsqlDataReader r, Guid workspaceId) => new()
    {
        WorkspaceId = workspaceId,
        DocumentId = r.GetGuid(0),
        ControlNumber = r.GetString(3),
        ControlNumberNorm = r.GetString(4),
        ControlNumberSortKey = r.GetString(5),
        BegBates = NullableString(r, 6),
        EndBates = NullableString(r, 7),
        BegAttach = NullableString(r, 8),
        EndAttach = NullableString(r, 9),
        FamilyId = r.GetGuid(10),
        ParentDocumentId = Value<Guid>(r, 11),
        FamilySequence = r.GetInt32(12),
        FamilyStatus = (FamilyStatus)r.GetInt16(13),
        DuplicateGroupId = Value<Guid>(r, 14),
        IsDuplicatePrimary = r.GetBoolean(15),
        EmailThreadId = Value<Guid>(r, 16),
        EmailThreadSource = (EmailThreadSource?)Value<short>(r, 17),
        Md5 = Bytes(r, 18),
        Sha1 = Bytes(r, 19),
        Sha256 = Bytes(r, 20),
        UpstreamDedupeHash = NullableString(r, 21),
        FileName = NullableString(r, 22),
        FileExtension = NullableString(r, 23),
        FileType = NullableString(r, 24),
        MimeType = NullableString(r, 25),
        FileSize = Value<long>(r, 26),
        PageCount = Value<int>(r, 27),
        DateSent = Value<DateTimeOffset>(r, 28),
        DateReceived = Value<DateTimeOffset>(r, 29),
        DateCreated = Value<DateTimeOffset>(r, 30),
        DateLastModified = Value<DateTimeOffset>(r, 31),
        DocumentDate = Value<DateTimeOffset>(r, 32),
        DocumentDateSource = (DocumentDateSource?)Value<short>(r, 33),
        FamilyDate = Value<DateTimeOffset>(r, 34),
        NativeObjectId = Value<Guid>(r, 35),
        TextObjectId = Value<Guid>(r, 36),
        TextLength = Value<long>(r, 37),
        ActivePageSetId = Value<Guid>(r, 38),
        TextTruncated = r.GetBoolean(39),
        TextMissing = r.GetBoolean(40),
        NativeMissing = r.GetBoolean(41),
        ImagesIncomplete = r.GetBoolean(42),
        TextEncodingWarning = r.GetBoolean(43),
        Metadata = r.GetString(44),
    };

    private static string? NullableString(NpgsqlDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetString(ordinal);

    private static T? Value<T>(NpgsqlDataReader r, int ordinal)
        where T : struct => r.IsDBNull(ordinal) ? null : r.GetFieldValue<T>(ordinal);

    private static byte[]? Bytes(NpgsqlDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetFieldValue<byte[]>(ordinal);
}

public static class ProjectionSourceRegistration
{
    /// <summary>The PostgreSQL <see cref="IProjectionSourceReader"/>; the data source is resolved on first use.</summary>
    public static IServiceCollection AddPostgresProjectionSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IProjectionSourceReader>(sp => new ProjectionSourceReader(sp.GetRequiredService<NpgsqlDataSource>()));
        return services;
    }
}
