using System.Diagnostics.CodeAnalysis;

namespace Opportunity.Application.Storage;

/// <summary>
/// A validated key prefix for listing and deletion. Always ends with <c>/</c> and is rooted at <c>ws/{workspaceId}/</c> or
/// <c>sys/{area}/</c>. Only the shapes in ADR-011 §7.1 are <see cref="IsDeletable"/>.
/// </summary>
public sealed class ObjectPrefix : IEquatable<ObjectPrefix>
{
    private ObjectPrefix(string value, Guid? workspaceId, bool isDeletable)
    {
        Value = value;
        WorkspaceId = workspaceId;
        IsDeletable = isDeletable;
    }

    public string Value { get; }

    public Guid? WorkspaceId { get; }

    /// <summary>
    /// True for <c>ws/{ws}/</c>, <c>ws/{ws}/{area}/</c>, <c>ws/{ws}/docs/{documentId}/</c> and <c>ws/{ws}/tmp/{jobId}/</c>.
    /// </summary>
    public bool IsDeletable { get; }

    public static ObjectPrefix Workspace(Guid workspaceId) => Parse($"ws/{ObjectKeyGrammar.Id(workspaceId)}/");

    public static ObjectPrefix WorkspaceArea(Guid workspaceId, string area) =>
        Parse($"ws/{ObjectKeyGrammar.Id(workspaceId)}/{area}/");

    public static ObjectPrefix Document(Guid workspaceId, Guid documentId) =>
        Parse($"ws/{ObjectKeyGrammar.Id(workspaceId)}/docs/{ObjectKeyGrammar.Id(documentId)}/");

    public static ObjectPrefix JobScratch(Guid workspaceId, Guid jobId) =>
        Parse($"ws/{ObjectKeyGrammar.Id(workspaceId)}/tmp/{ObjectKeyGrammar.Id(jobId)}/");

    public static ObjectPrefix Parse(string value) =>
        TryParse(value, out var prefix) ? prefix : throw new ArgumentException("Not a valid object prefix (ADR-011 §1, §7).", nameof(value));

    public static bool TryParse(string? value, [NotNullWhen(true)] out ObjectPrefix? prefix)
    {
        prefix = null;
        var s = ObjectKeyGrammar.SplitSegments(value, trailingSlash: true);
        if (s is null)
        {
            return false;
        }

        if (s[0] == ObjectKeyGrammar.WorkspaceRoot && s.Length >= 2 && ObjectKeyGrammar.IsId(s[1]))
        {
            if (s.Length >= 3 && !ObjectKeyGrammar.WorkspaceAreas.Contains(s[2]))
            {
                return false;
            }

            var deletable = s.Length switch
            {
                2 or 3 => true,
                4 => s[2] is "docs" or "tmp" && ObjectKeyGrammar.IsId(s[3]),
                _ => false,
            };
            prefix = new ObjectPrefix(value!, Guid.ParseExact(s[1], "N"), deletable);
            return true;
        }

        if (s[0] == ObjectKeyGrammar.SystemRoot && s.Length >= 2 && ObjectKeyGrammar.SystemAreas.Contains(s[1]))
        {
            prefix = new ObjectPrefix(value!, null, isDeletable: false);
            return true;
        }

        return false;
    }

    public bool Equals(ObjectPrefix? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as ObjectPrefix);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}
