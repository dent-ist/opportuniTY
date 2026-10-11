using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Opportunity.Application.Audit;

/// <summary>
/// The audit export file (E14-T04): RFC 4180 CSV, UTF-8 with a byte-order mark, one row per event in time order. The
/// file is the header followed by the parts the export's chunks wrote. Cells a spreadsheet would run as a formula get a
/// leading apostrophe. Audit holds identifiers only (ADR-013 §7); the Control Number of a document event is resolved for
/// the exporter at run time, and search text is a column only when the export asked for it (Q-16).
/// </summary>
public static class AuditExportCsv
{
    public const string ContentType = "text/csv; charset=utf-8";

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly string[] Columns =
    [
        "Occurred At (UTC)", "Event ID", "Category", "Action", "Outcome", "Reason", "Actor Type", "Actor ID", "Actor",
        "On Behalf Of", "Access Path", "Resource Type", "Resource ID", "Control Number", "Job ID", "Chunk", "Snapshot ID",
        "Search Generation", "Correlation ID", "Client IP", "Details", "Recorded At (UTC)", "Sequence",
    ];

    /// <summary>The byte-order mark and the header row.</summary>
    public static byte[] Header(bool includeSearchText)
    {
        var text = new StringBuilder();
        Append(text, includeSearchText ? [.. Columns, "Search Text"] : Columns);
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    /// <summary>Appends one event's row.</summary>
    public static void AppendRow(StringBuilder text, StoredAuditEvent stored, bool includeSearchText)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(stored);
        var e = stored.Event;
        var cells = new List<string?>(Columns.Length + 1)
        {
            Time(e.OccurredAt),
            e.EventId.ToString("D"),
            e.Category,
            e.Action,
            e.Outcome.ToString(),
            e.ReasonCode,
            e.ActorType.ToString(),
            e.ActorId,
            e.ActorDisplay,
            e.OnBehalfOf?.ToString("D"),
            e.AccessPath.ToString(),
            e.ResourceType,
            e.ResourceId,
            stored.DocumentControlNumber,
            e.JobId?.ToString("D"),
            e.ChunkSequence?.ToString(CultureInfo.InvariantCulture),
            e.SnapshotId?.ToString("D"),
            e.SearchGeneration?.ToString(CultureInfo.InvariantCulture),
            e.CorrelationId,
            e.ClientIp,
            e.Details.Count == 0 ? null : JsonSerializer.Serialize(e.Details, Json),
            Time(stored.RecordedAt),
            stored.Sequence?.ToString(CultureInfo.InvariantCulture),
        };
        if (includeSearchText)
        {
            cells.Add(e.RestrictedDetails is { Count: > 0 } restricted ? JsonSerializer.Serialize(restricted, Json) : null);
        }

        Append(text, cells);
    }

    public static string FileName(Guid exportId, DateTimeOffset createdAt) =>
        "audit-log-" + createdAt.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + exportId.ToString("N")[..8] + ".csv";

    private static string Time(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    private static void Append(StringBuilder text, IEnumerable<string?> cells) =>
        text.AppendJoin(',', cells.Select(Cell)).Append("\r\n");

    private static string Cell(string? value)
    {
        var v = value ?? string.Empty;
        if (v.Length > 0 && (v[0] is '=' or '+' or '@' or '\t' or '\r' || (v[0] == '-' && (v.Length == 1 || !char.IsDigit(v[1])))))
        {
            v = "'" + v;
        }

        return v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }
}
