using System.Globalization;
using System.Text.Json.Nodes;

namespace Opportunity.Core.Documents;

/// <summary>Which hash computed duplicate grouping compares (E09-T04). Stored as smallint; values are fixed forever.</summary>
public enum DedupeHashSource : short
{
    /// <summary>
    /// The Q-09 / ADR-009 R14 default: the parent's upstream dedupe hash (or, without one, its upstream email hash) when
    /// present, otherwise the SHA-256 of its native.
    /// </summary>
    Auto = 1,

    /// <summary>SHA-256 of the native only.</summary>
    Sha256 = 2,

    /// <summary>The MD5 the load file supplied (or that was computed from the native).</summary>
    Md5 = 3,

    /// <summary>The SHA-1 the load file supplied (or that was computed from the native).</summary>
    Sha1 = 4,

    /// <summary>The upstream dedupe hash, or the upstream email hash where no dedupe hash was mapped (Q-63).</summary>
    UpstreamHash = 5,
}

/// <summary>
/// A workspace's computed duplicate grouping policy (E09-T04, Q-09, ADR-009 R14). Grouping is family-level: only
/// top-level parents (and standalone documents) are compared and attachments inherit their parent's group. Documents
/// with an upstream group are never regrouped. Groups are labels only: nothing is suppressed or coded.
/// </summary>
/// <param name="Enabled">Off: a run removes the computed groups (upstream groups stay).</param>
/// <param name="CustodianFieldId">
/// <see cref="DuplicateGroupScope.Custodial"/>: the field holding a document's custodian (a metadata Keyword or Text
/// field); null means the workspace's <c>custodian</c> field.
/// </param>
public sealed record DedupePolicy(
    bool Enabled = false,
    DedupeHashSource HashSource = DedupeHashSource.Auto,
    DuplicateGroupScope Scope = DuplicateGroupScope.Global,
    int? CustodianFieldId = null)
{
    /// <summary>The policy of a workspace that never set one.</summary>
    public static DedupePolicy Default { get; } = new();

    /// <summary>The job parameters of a run (identifiers and settings only, ADR-010 §9).</summary>
    public JsonObject ToJson() => new()
    {
        ["kind"] = ParameterKind,
        ["enabled"] = Enabled,
        ["hashSource"] = HashSource.ToString(),
        ["scope"] = Scope.ToString(),
        ["custodianFieldId"] = CustodianFieldId,
    };

    /// <summary>The <c>kind</c> of a dedupe run's job parameters (RelationshipFixup jobs may do other fix-ups later).</summary>
    public const string ParameterKind = "dedupe";

    /// <summary>Reads <see cref="ToJson"/>; throws <see cref="FormatException"/> for anything else.</summary>
    public static DedupePolicy Parse(JsonObject? parameters)
    {
        if (parameters?["kind"]?.GetValue<string>() != ParameterKind
            || parameters["enabled"] is not JsonValue enabled || !enabled.TryGetValue<bool>(out var on)
            || !Enum.TryParse<DedupeHashSource>(parameters["hashSource"]?.GetValue<string>(), out var source) || !Enum.IsDefined(source)
            || !Enum.TryParse<DuplicateGroupScope>(parameters["scope"]?.GetValue<string>(), out var scope) || !Enum.IsDefined(scope))
        {
            throw new FormatException("The job parameters are not a dedupe policy.");
        }

        int? custodian = parameters["custodianFieldId"] is JsonValue c ? c.GetValue<int>() : null;
        return new DedupePolicy(on, source, scope, custodian);
    }

    /// <summary>What the per-group <see cref="DuplicateHashKind"/> is for a hash column of the policy.</summary>
    public static DuplicateHashKind KindOf(DedupeHashSource source) => source switch
    {
        DedupeHashSource.Sha256 => DuplicateHashKind.Sha256Native,
        DedupeHashSource.Md5 => DuplicateHashKind.Md5,
        DedupeHashSource.Sha1 => DuplicateHashKind.Sha1,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Auto and UpstreamHash take the kind each document holds."),
    };

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{(Enabled ? "on" : "off")}, {HashSource}, {Scope}{(CustodianFieldId is { } f ? $" (field {f})" : string.Empty)}");
}
