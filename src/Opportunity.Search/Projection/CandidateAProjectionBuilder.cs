using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Opportunity.Application.Search.Projection;
using Opportunity.Core.Documents;
using Opportunity.Core.Fields;

namespace Opportunity.Search.Projection;

/// <summary>
/// Interim Candidate A (§25, ADR-004b): one unified projection document per DocumentId holding structural fields,
/// metadata slots, coding slots and text, written as a full external-versioned <c>index</c> (ADR-001 §3).
/// Conforms to <c>projection.v2.json</c>.
/// </summary>
internal sealed class CandidateAProjectionBuilder : IProjectionBuilder
{
    public const int ProjectionGeneration = 2;

    public int Generation => ProjectionGeneration;

    public ProjectionDocument Build(ProjectionSource source, FieldCatalog catalog, ProjectionText? text)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(catalog);
        var id = source.DocumentId.ToString("D");
        switch (source.State)
        {
            case ProjectionSourceState.Missing:
                return Result(source, null, new ProjectionWrite(id, ProjectionWriteKind.DeleteUnconditional, null, null), []);
            case ProjectionSourceState.Deleted:
                return Result(source, Version(source), new ProjectionWrite(id, ProjectionWriteKind.Delete, Version(source), null), []);
        }

        var document = source.Document
            ?? throw new ArgumentException("A live projection source carries its document.", nameof(source));
        if (document.WorkspaceId != source.WorkspaceId || document.DocumentId != source.DocumentId)
        {
            throw new ArgumentException("The document does not match the projection source.", nameof(source));
        }

        var version = Version(source);
        var body = new JsonObject
        {
            ["workspaceId"] = source.WorkspaceId.ToString("D"),
            ["documentId"] = id,
            ["projectionVersion"] = version,
        };

        AddStructural(body, document);
        body["securityTags"] = new JsonArray([.. source.SecurityTags.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(t => (JsonNode)JsonValue.Create(t))]);

        var dropped = new List<int>();
        var metadata = ParseMetadata(document.Metadata);
        var slots = new JsonObject();
        var codingSlots = new JsonObject();
        var overflow = new JsonObject();
        var codingOverflow = new JsonObject();
        foreach (var field in catalog.Fields.OrderBy(f => f.FieldId))
        {
            if (field.Storage == FieldStorage.Column || ProjectionFieldPaths.For(field) is null)
            {
                continue;
            }

            var coding = field.Storage == FieldStorage.Coding;
            var value = coding ? source.Coding.GetValueOrDefault(field.FieldId) : metadata?[field.Key];
            if (value is null)
            {
                continue;
            }

            var slot = field.SearchSlot!;
            if (slot == FieldRules.OverflowSlot)
            {
                if (OverflowValue(field, value) is { } flat)
                {
                    (coding ? codingOverflow : overflow)[field.Key] = flat;
                }
                else
                {
                    dropped.Add(field.FieldId);
                }

                continue;
            }

            var dot = slot.IndexOf('.', StringComparison.Ordinal);
            var kind = slot[..dot];
            if (SlotValue(field, kind, value) is not { } projected)
            {
                dropped.Add(field.FieldId);
                continue;
            }

            var container = coding ? codingSlots : slots;
            if (container[kind] is not JsonObject kindObject)
            {
                container[kind] = kindObject = [];
            }

            kindObject[slot[(dot + 1)..]] = projected;
        }

        Put(body, ProjectionFieldPaths.Metadata, slots);
        Put(body, ProjectionFieldPaths.MetadataOverflow, overflow);
        Put(body, ProjectionFieldPaths.Coding, codingSlots);
        Put(body, ProjectionFieldPaths.CodingOverflow, codingOverflow);

        body["textLength"] = document.TextLength ?? (text is { Truncated: false } ? text.Text.Length : null);
        body["textTruncated"] = document.TextTruncated || text?.Truncated == true;
        body["textMissing"] = document.TextMissing;
        body["nativeMissing"] = document.NativeMissing;
        body["imagesIncomplete"] = document.ImagesIncomplete;
        if (text is { Text.Length: > 0 })
        {
            body["text"] = text.Text;
        }

        RemoveNulls(body);
        return Result(source, version, new ProjectionWrite(id, ProjectionWriteKind.Index, version, body), dropped);
    }

    private ProjectionDocument Result(ProjectionSource source, long? version, ProjectionWrite write, IReadOnlyList<int> dropped) =>
        new(source.WorkspaceId, source.DocumentId, version, Generation, [write], dropped);

    private static long Version(ProjectionSource source) =>
        source.DocumentVersion is { } v and >= 1
            ? v
            : throw new ArgumentException("A deleted or live projection source carries its DocumentVersion.", nameof(source));

    private static void AddStructural(JsonObject body, Document d)
    {
        body["controlNumber"] = d.ControlNumber;
        body["controlNumberSort"] = d.ControlNumberSortKey.Length > 0
            ? d.ControlNumberSortKey
            : ControlNumber.SortKey(d.ControlNumberNorm.Length > 0 ? d.ControlNumberNorm : d.ControlNumber);
        body["begBates"] = d.BegBates;
        body["begBatesSort"] = BatesSortKey(d.BegBates);
        body["endBates"] = d.EndBates;
        body["endBatesSort"] = BatesSortKey(d.EndBates);
        body["begAttach"] = d.BegAttach;
        body["endAttach"] = d.EndAttach;
        body["familyId"] = d.FamilyId.ToString("D");
        body["parentDocumentId"] = d.ParentDocumentId?.ToString("D");
        body["familySequence"] = d.FamilySequence;
        body["familyStatus"] = d.FamilyStatus.ToString().ToLowerInvariant();
        body["duplicateGroupId"] = d.DuplicateGroupId?.ToString("D");
        body["isDuplicatePrimary"] = d.IsDuplicatePrimary;
        body["emailThreadId"] = d.EmailThreadId?.ToString("D");
        body["fileName"] = d.FileName;
        body["fileExtension"] = d.FileExtension;
        body["fileType"] = d.FileType;
        body["mimeType"] = d.MimeType;
        body["fileSize"] = d.FileSize;
        body["pageCount"] = d.PageCount;
        body["documentDate"] = Instant(d.DocumentDate);
        body["familyDate"] = Instant(d.FamilyDate);
        body["dateSent"] = Instant(d.DateSent);
        body["dateReceived"] = Instant(d.DateReceived);
        body["dateCreated"] = Instant(d.DateCreated);
        body["dateLastModified"] = Instant(d.DateLastModified);
        body["md5"] = Hex(d.Md5);
        body["sha1"] = Hex(d.Sha1);
        body["sha256"] = Hex(d.Sha256);
    }

    /// <summary>Received Bates sort like control numbers (ADR-009 R5): case-folded, digit runs padded.</summary>
    private static string? BatesSortKey(string? bates) =>
        bates is null ? null
        : ControlNumber.SortKey(ControlNumber.TryNormalize(bates, caseSensitive: false, null, out var norm, out _) ? norm : bates);

    private static string? Instant(DateTimeOffset? value) => value is { } v ? FieldValues.FormatInstant(v.UtcDateTime) : null;

    private static string? Hex(byte[]? value) => value is null ? null : Convert.ToHexStringLower(value);

    private static JsonObject? ParseMetadata(string json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json) as JsonObject;

    /// <summary>The value of a slot of <paramref name="kind"/> (ADR-007 R3, R5, R7), or null when it does not fit.</summary>
    private static JsonNode? SlotValue(FieldDefinition field, string kind, JsonNode value) => kind switch
    {
        "txt" or "idt" or "kw" or "usr" => Strings(value),
        "ch" => ChoiceIds(value),
        "int" => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<long>(out var l)
            ? JsonValue.Create(l) : null,
        "dec" => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? JsonValue.Create(d) : null,
        "dt" => Date(field, value) is { } date ? JsonValue.Create(date) : null,
        "bool" => value is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False
            ? JsonValue.Create(v.GetValue<bool>()) : null,
        _ => null,
    };

    /// <summary>Overflow keeps every leaf as a string (<c>flat_object</c>, ADR-007 R6).</summary>
    private static JsonNode? OverflowValue(FieldDefinition field, JsonNode value) => field.Type switch
    {
        FieldType.Text or FieldType.Keyword or FieldType.User => Strings(value),
        FieldType.SingleChoice or FieldType.MultiChoice => ChoiceIds(value),
        FieldType.Date => Date(field, value) is { } date ? JsonValue.Create(date) : null,
        _ => value is JsonValue v && v.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
            ? JsonValue.Create(v.ToJsonString()) : null,
    };

    private static JsonNode? Strings(JsonNode value)
    {
        if (value is JsonValue single)
        {
            return single.GetValueKind() == JsonValueKind.String ? JsonValue.Create(single.GetValue<string>()) : null;
        }

        if (value is not JsonArray array || array.Count == 0
            || array.Any(n => n is not JsonValue item || item.GetValueKind() != JsonValueKind.String))
        {
            return null;
        }

        return new JsonArray([.. array.Select(n => (JsonNode)JsonValue.Create(n!.GetValue<string>()))]);
    }

    /// <summary>Choices are projected as keyword ChoiceIds (ADR-003 R15); the planner resolves names to ids.</summary>
    private static JsonNode? ChoiceIds(JsonNode value)
    {
        IReadOnlyList<JsonNode?> items = value is JsonArray array ? [.. array] : new List<JsonNode?> { value };
        var ids = new List<string>();
        foreach (var item in items)
        {
            if (item is not JsonValue v || v.GetValueKind() != JsonValueKind.Number || !v.TryGetValue<int>(out var choiceId))
            {
                return null;
            }

            ids.Add(choiceId.ToString(CultureInfo.InvariantCulture));
        }

        return ids.Count switch
        {
            0 => null,
            1 when value is not JsonArray => JsonValue.Create(ids[0]),
            _ => new JsonArray([.. ids.Select(i => (JsonNode)JsonValue.Create(i))]),
        };
    }

    /// <summary>Date-precision values index as UTC midnight and compare without zone conversion (ADR-007 R5).</summary>
    private static string? Date(FieldDefinition field, JsonNode value)
    {
        if (value is not JsonValue v || v.GetValueKind() != JsonValueKind.String)
        {
            return null;
        }

        var s = v.GetValue<string>();
        if (field.DatePrecision == DatePrecision.Date)
        {
            return DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? FieldValues.FormatInstant(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))
                : null;
        }

        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var instant)
            ? FieldValues.FormatInstant(instant)
            : null;
    }

    private static void Put(JsonObject body, string name, JsonObject container)
    {
        if (container.Count > 0)
        {
            body[name] = container;
        }
    }

    private static void RemoveNulls(JsonObject body)
    {
        foreach (var key in body.Where(p => p.Value is null).Select(p => p.Key).ToList())
        {
            body.Remove(key);
        }
    }
}
