using NpgsqlTypes;

using Opportunity.Core.Documents;

namespace Opportunity.Data.Documents;

/// <summary>
/// The writable columns of <c>opportunity.document</c>, in one place so that INSERT, UPDATE and COPY agree.
/// <see cref="Column.Projected"/> marks projection inputs (ADR-001 §2): a change to any of them bumps DocumentVersion.
/// The list is conservative: an unnecessary bump only costs a re-index, a missing one serves stale search results.
/// </summary>
internal static class DocumentColumns
{
    internal sealed record Column(string Name, NpgsqlDbType Type, Func<Document, object?> Get, bool Mutable, bool Projected);

    public static readonly IReadOnlyList<Column> All =
    [
        Identity("workspace_id", NpgsqlDbType.Uuid, d => d.WorkspaceId),
        Identity("document_id", NpgsqlDbType.Uuid, d => d.DocumentId),
        Identity("control_number", NpgsqlDbType.Text, d => d.ControlNumber),
        Identity("control_number_norm", NpgsqlDbType.Text, d => d.ControlNumberNorm),
        Identity("first_import_batch_id", NpgsqlDbType.Uuid, d => d.FirstImportBatchId),

        Projected("beg_bates", NpgsqlDbType.Text, d => d.BegBates),
        Projected("end_bates", NpgsqlDbType.Text, d => d.EndBates),
        Projected("beg_attach", NpgsqlDbType.Text, d => d.BegAttach),
        Projected("end_attach", NpgsqlDbType.Text, d => d.EndAttach),
        Projected("family_id", NpgsqlDbType.Uuid, d => d.FamilyId),
        Projected("parent_document_id", NpgsqlDbType.Uuid, d => d.ParentDocumentId),
        Projected("family_sequence", NpgsqlDbType.Integer, d => d.FamilySequence),
        Projected("family_status", NpgsqlDbType.Smallint, d => (short)d.FamilyStatus),
        Projected("duplicate_group_id", NpgsqlDbType.Uuid, d => d.DuplicateGroupId),
        Projected("is_duplicate_primary", NpgsqlDbType.Boolean, d => d.IsDuplicatePrimary),
        Projected("email_thread_id", NpgsqlDbType.Uuid, d => d.EmailThreadId),
        Projected("email_thread_source", NpgsqlDbType.Smallint, d => (short?)d.EmailThreadSource),
        Projected("md5", NpgsqlDbType.Bytea, d => d.Md5),
        Projected("sha1", NpgsqlDbType.Bytea, d => d.Sha1),
        Projected("sha256", NpgsqlDbType.Bytea, d => d.Sha256),
        Projected("upstream_dedupe_hash", NpgsqlDbType.Text, d => d.UpstreamDedupeHash),
        Projected("upstream_dedupe_hash_kind", NpgsqlDbType.Smallint, d => (short?)d.UpstreamDedupeHashKind),
        Projected("file_name", NpgsqlDbType.Text, d => d.FileName),
        Projected("file_extension", NpgsqlDbType.Text, d => d.FileExtension),
        Projected("file_type", NpgsqlDbType.Text, d => d.FileType),
        Projected("mime_type", NpgsqlDbType.Text, d => d.MimeType),
        Projected("file_size", NpgsqlDbType.Bigint, d => d.FileSize),
        Projected("page_count", NpgsqlDbType.Integer, d => d.PageCount),
        Projected("date_sent", NpgsqlDbType.TimestampTz, d => Utc(d.DateSent)),
        Projected("date_received", NpgsqlDbType.TimestampTz, d => Utc(d.DateReceived)),
        Projected("date_created", NpgsqlDbType.TimestampTz, d => Utc(d.DateCreated)),
        Projected("date_last_modified", NpgsqlDbType.TimestampTz, d => Utc(d.DateLastModified)),
        Projected("document_date", NpgsqlDbType.TimestampTz, d => Utc(d.DocumentDate)),
        Projected("document_date_source", NpgsqlDbType.Smallint, d => (short?)d.DocumentDateSource),
        Projected("family_date", NpgsqlDbType.TimestampTz, d => Utc(d.FamilyDate)),
        Projected("native_object_id", NpgsqlDbType.Uuid, d => d.NativeObjectId),
        Projected("text_object_id", NpgsqlDbType.Uuid, d => d.TextObjectId),
        Projected("text_length", NpgsqlDbType.Bigint, d => d.TextLength),
        Projected("active_page_set_id", NpgsqlDbType.Uuid, d => d.ActivePageSetId),
        Projected("text_truncated", NpgsqlDbType.Boolean, d => d.TextTruncated),
        Projected("text_missing", NpgsqlDbType.Boolean, d => d.TextMissing),
        Projected("native_missing", NpgsqlDbType.Boolean, d => d.NativeMissing),
        Projected("images_incomplete", NpgsqlDbType.Boolean, d => d.ImagesIncomplete),
        Projected("text_encoding_warning", NpgsqlDbType.Boolean, d => d.TextEncodingWarning),
        Projected("metadata", NpgsqlDbType.Jsonb, d => d.Metadata),

        // Raw strings are shown by the viewer and overlay diffs but never searched (ADR-003 R9).
        new("metadata_raw", NpgsqlDbType.Jsonb, d => d.MetadataRaw, Mutable: true, Projected: false),
    ];

    public static readonly IReadOnlyList<Column> MutableColumns = [.. All.Where(c => c.Mutable)];

    public static readonly IReadOnlyList<Column> ProjectedColumns = [.. All.Where(c => c.Projected)];

    public static readonly IReadOnlyList<Column> NonProjectedMutableColumns = [.. All.Where(c => c.Mutable && !c.Projected)];

    public static string NameList(IEnumerable<Column> columns, string? alias = null) =>
        string.Join(", ", columns.Select(c => alias is null ? c.Name : $"{alias}.{c.Name}"));

    public static string ParameterList(IEnumerable<Column> columns) =>
        string.Join(", ", columns.Select(c => "@" + c.Name));

    private static Column Identity(string name, NpgsqlDbType type, Func<Document, object?> get) =>
        new(name, type, get, Mutable: false, Projected: false);

    private static Column Projected(string name, NpgsqlDbType type, Func<Document, object?> get) =>
        new(name, type, get, Mutable: true, Projected: true);

    // Npgsql writes timestamptz from UTC values only.
    private static DateTimeOffset? Utc(DateTimeOffset? value) => value?.ToUniversalTime();
}
