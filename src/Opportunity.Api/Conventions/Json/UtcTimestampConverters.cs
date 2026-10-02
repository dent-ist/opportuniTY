using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Opportunity.Api.Conventions.Json;

/// <summary>
/// ADR-019 §2.9: timestamps are UTC ISO-8601 with <c>Z</c> and millisecond precision
/// (<c>2026-10-02T14:03:22.123Z</c>). Reading accepts any ISO-8601 offset and normalises to UTC.
/// </summary>
internal sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    internal const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTimeOffset().ToUniversalTime();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
}

internal sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTimeOffset().UtcDateTime;

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        // An unspecified kind is ambiguous; treat it as UTC rather than guessing the server's zone.
        var utc = value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value,
        };
        writer.WriteStringValue(utc.ToString(UtcDateTimeOffsetConverter.Format, CultureInfo.InvariantCulture));
    }
}
