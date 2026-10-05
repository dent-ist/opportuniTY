using System.Text.Json.Nodes;

using Opportunity.Core.Fields;

namespace Opportunity.Correctness.ShadowLedger;

/// <summary>A coding field as the ledger reads it from <c>field_definition</c> (storage Coding).</summary>
public sealed record CodingFieldInfo(int FieldId, FieldType Type, string? SearchSlot, bool IsSearchable, bool IsDeleted, DatePrecision? DatePrecision)
{
    /// <summary>Whether the projection carries the field at all (ADR-007: searchable, slotted, not deleted).</summary>
    public bool IsProjected => IsSearchable && !IsDeleted && !string.IsNullOrEmpty(SearchSlot);
}

/// <summary>
/// Where one ADR-004 candidate keeps a document's version and coding values in OpenSearch. The ledger compares
/// PostgreSQL against the index through this seam, so the same oracle judges every candidate (E18-T04); each candidate
/// that gets a projection adds its layout here.
/// </summary>
public interface IProjectionLayout
{
    /// <summary>Candidate id as recorded in the result bundle (<c>run.candidate</c>).</summary>
    string Candidate { get; }

    /// <summary><c>_source</c> fields the oracle needs (kept small: a 1M-document reconciliation reads them all).</summary>
    IReadOnlyList<string> SourceIncludes { get; }

    /// <summary>The indexed value of <paramref name="field"/>, or null when absent.</summary>
    JsonNode? CodingValue(JsonObject source, CodingFieldInfo field);
}

/// <summary>
/// Interim Candidate A (unified document, ADR-001 §3): <c>projectionVersion</c> at the root, coding slots under
/// <c>coding.{kind}.{n}</c>, overflow coding fields under <c>codingOverflow.f{id}</c> (ADR-007 R3, R6).
/// </summary>
public sealed class CandidateAProjectionLayout : IProjectionLayout
{
    public const string Id = "A-interim";

    public static CandidateAProjectionLayout Instance { get; } = new();

    public string Candidate => Id;

    public IReadOnlyList<string> SourceIncludes { get; } = ["workspaceId", "projectionVersion", "coding", "codingOverflow"];

    public JsonNode? CodingValue(JsonObject source, CodingFieldInfo field)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(field);
        if (!field.IsProjected)
        {
            return null;
        }

        if (field.SearchSlot == FieldRules.OverflowSlot)
        {
            return (source["codingOverflow"] as JsonObject)?[FieldKey.For(field.FieldId)];
        }

        var dot = field.SearchSlot!.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? null : ((source["coding"] as JsonObject)?[field.SearchSlot[..dot]] as JsonObject)?[field.SearchSlot[(dot + 1)..]];
    }
}

/// <summary>The layouts the oracle knows, by candidate id. Only the interim Candidate A exists today (Q-70).</summary>
public static class ProjectionLayouts
{
    public static IReadOnlyDictionary<string, IProjectionLayout> All { get; } =
        new Dictionary<string, IProjectionLayout>(StringComparer.OrdinalIgnoreCase) { [CandidateAProjectionLayout.Id] = CandidateAProjectionLayout.Instance };

    public static IProjectionLayout For(string? candidate) =>
        string.IsNullOrWhiteSpace(candidate) ? CandidateAProjectionLayout.Instance
        : All.TryGetValue(candidate, out var layout) ? layout
        : throw new ArgumentException(
            $"No projection layout for candidate '{candidate}'. Known: {string.Join(", ", All.Keys)}. A candidate needs its layout before the oracle can judge it.",
            nameof(candidate));
}
