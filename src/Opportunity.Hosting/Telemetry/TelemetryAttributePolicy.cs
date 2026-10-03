using System.Collections.Frozen;
using System.Diagnostics;
using OpenTelemetry;
using Opportunity.Application.Telemetry;

namespace Opportunity.Hosting.Telemetry;

/// <summary>
/// The span attribute allow-list (ADR-015 D10.5, ADR-017 R6). Telemetry carries IDs, enums, counts and durations
/// only: anything not listed here is removed before export, whichever library added it. Notably dropped: query
/// strings (<c>url.query</c>, search text), request/response headers (authorization, cookies), client IP and user
/// agent, the Npgsql data source name (a connection string) and SQL text (<c>db.query.text</c>, unless
/// <c>Telemetry:RecordSqlStatements</c> is set for local debugging).
/// </summary>
public static class TelemetryAttributePolicy
{
    private static readonly FrozenSet<string> AllowedKeys = new[]
    {
        // HTTP server/client (OpenTelemetry HTTP semantic conventions).
        "http.request.method", "http.request.method_original", "http.response.status_code", "http.route",
        "http.request.resend_count", "url.scheme", "url.path", "url.full", "server.address", "server.port",
        "network.protocol.name", "network.protocol.version", "network.transport", "error.type",
        // PostgreSQL (Npgsql) without statement text or data source.
        "db.system", "db.system.name", "db.namespace", "db.operation.name", "db.collection.name",
        "db.stored_procedure.name", "db.response.status_code", "db.npgsql.connection_id", "db.npgsql.prepared",
        "db.npgsql.rows",
        // Messaging.
        "messaging.system", "messaging.operation.name", "messaging.operation.type", "messaging.destination.name",
        "messaging.message.id", "messaging.consumer.group.name", "messaging.batch.message_count",
        TelemetryAttributes.WorkerType,
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly string[] AllowedPrefixes = ["opportunity.", "aspnetcore.", "messaging.rabbitmq.", "otel."];

    private static readonly FrozenSet<string> SqlTextKeys =
        new[] { "db.query.text", "db.statement" }.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsAllowed(string key, bool recordSqlStatements = false)
    {
        ArgumentNullException.ThrowIfNull(key);
        return AllowedKeys.Contains(key)
            || (recordSqlStatements && SqlTextKeys.Contains(key))
            || AllowedPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>Removes the query string and fragment (and any user info) from an absolute or relative URL.</summary>
    public static string StripQuery(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var end = url.IndexOfAny(['?', '#']);
        var stripped = end < 0 ? url : url[..end];
        if (Uri.TryCreate(stripped, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0)
        {
            stripped = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.GetLeftPart(UriPartial.Path);
        }

        return stripped;
    }
}

/// <summary>
/// Enforces <see cref="TelemetryAttributePolicy"/> on every finished span before any exporter sees it, and drops
/// error status descriptions (exception messages can quote data values; <c>error.type</c> stays).
/// </summary>
internal sealed class TelemetryScrubbingProcessor(bool recordSqlStatements) : BaseProcessor<Activity>
{
    public override void OnEnd(Activity data)
    {
        List<string>? remove = null;
        string? fullUrl = null;
        foreach (var tag in data.TagObjects)
        {
            if (!TelemetryAttributePolicy.IsAllowed(tag.Key, recordSqlStatements))
            {
                (remove ??= []).Add(tag.Key);
            }
            else if (tag.Key == "url.full" && tag.Value is string url)
            {
                fullUrl = url;
            }
        }

        if (remove is not null)
        {
            foreach (var key in remove)
            {
                data.SetTag(key, null);
            }
        }

        if (fullUrl is not null)
        {
            data.SetTag("url.full", TelemetryAttributePolicy.StripQuery(fullUrl));
        }

        if (data.StatusDescription is not null)
        {
            data.SetStatus(data.Status);
        }
    }
}
