using System.Globalization;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Opportunity.Application.Keys;

namespace Opportunity.Hosting.Telemetry;

/// <summary>
/// Redacts credentials from every log record before any OpenTelemetry exporter sees it (ADR-015 D10.5, E05-T09): the
/// formatted message, the message template, string attribute values and the exception's message and stack trace go
/// through <see cref="SecretRedactor"/>. A safety net for a connection string, token or signed URL that reaches a log
/// message by mistake; code still must not log secrets.
/// </summary>
internal sealed class SecretScrubbingLogProcessor : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord data)
    {
        if (data.FormattedMessage is { } formatted)
        {
            data.FormattedMessage = SecretRedactor.Redact(formatted);
        }

        if (data.Body is { } body)
        {
            data.Body = SecretRedactor.Redact(body);
        }

        if (data.Attributes is { Count: > 0 } attributes)
        {
            List<KeyValuePair<string, object?>>? scrubbed = null;
            for (var i = 0; i < attributes.Count; i++)
            {
                var value = attributes[i].Value switch
                {
                    string text => text,
                    Uri uri => uri.ToString(),
                    _ => null,
                };
                if (value is null)
                {
                    continue;
                }

                var redacted = SecretRedactor.Redact(value);
                if (!ReferenceEquals(redacted, value) && redacted != value)
                {
                    scrubbed ??= [.. attributes];
                    scrubbed[i] = new(attributes[i].Key, redacted);
                }
            }

            if (scrubbed is not null)
            {
                data.Attributes = scrubbed;
            }
        }

        if (data.Exception is { } exception && exception is not RedactedException)
        {
            var text = exception.ToString();
            if (SecretRedactor.ContainsCredential(text))
            {
                data.Exception = new RedactedException(exception);
            }
        }
    }

    /// <summary>Stands in for an exception whose message or stack trace quoted a credential.</summary>
    internal sealed class RedactedException(Exception original)
        : Exception(SecretRedactor.Redact(original.GetType().FullName + ": " + original.Message))
    {
        private readonly string _stackTrace = SecretRedactor.Redact(original.ToString());

        public override string StackTrace => _stackTrace;

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Message}{Environment.NewLine}{_stackTrace}");
    }
}
