using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Opportunity.Contracts.Messaging;

/// <summary>
/// <c>"major.minor"</c> version of a message payload schema (ADR-019 §3.2). A minor bump is additive only; anything
/// else is a new major.
/// </summary>
[JsonConverter(typeof(SchemaVersionJsonConverter))]
public readonly record struct SchemaVersion : IComparable<SchemaVersion>
{
    public SchemaVersion(int major, int minor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(major, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        Major = major;
        Minor = minor;
    }

    public int Major { get; }

    public int Minor { get; }

    public static SchemaVersion Parse(string value) =>
        TryParse(value, out var version)
            ? version
            : throw new FormatException($"'{value}' is not a schema version of the form 'major.minor'.");

    public static bool TryParse(string? value, out SchemaVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var dot = value.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0
            || !int.TryParse(value.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(value.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || major < 1)
        {
            return false;
        }

        version = new SchemaVersion(major, minor);
        return true;
    }

    public int CompareTo(SchemaVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);

    public static bool operator <(SchemaVersion left, SchemaVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(SchemaVersion left, SchemaVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(SchemaVersion left, SchemaVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(SchemaVersion left, SchemaVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}");
}

internal sealed class SchemaVersionJsonConverter : JsonConverter<SchemaVersion>
{
    public override SchemaVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && SchemaVersion.TryParse(reader.GetString(), out var version)
            ? version
            : throw new JsonException("schemaVersion must be a string of the form 'major.minor'.");

    public override void Write(Utf8JsonWriter writer, SchemaVersion value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
