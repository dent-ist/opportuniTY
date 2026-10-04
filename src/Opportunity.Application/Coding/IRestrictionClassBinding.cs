using System.Text.Json.Nodes;

using Opportunity.Core.Fields;

namespace Opportunity.Application.Coding;

/// <summary>
/// Which restriction classes (ADR-015 D6.1, Q-11) a document carries because of its security-affecting coding. The
/// coding store asks this inside the coding transaction whenever a write changes a security-affecting field and stores
/// the result in <c>DocumentRestriction</c> in that same transaction (§24 rule 1), so the PDP enforces the change on
/// the next request whatever the search projection's lag.
/// </summary>
/// <remarks>
/// The configuration of the binding (which field and choice drives which class) belongs to E05-T06. Until it is
/// registered, <see cref="NoRestrictionClassBinding"/> binds nothing and coding never changes a document's classes.
/// Implementations are pure: no I/O, the same answer for the same input.
/// </remarks>
public interface IRestrictionClassBinding
{
    /// <summary>
    /// The class keys this workspace derives from coding. Coding adds and removes only these; a class outside the set
    /// (applied by another path) is left alone. Empty: nothing is bound.
    /// </summary>
    IReadOnlySet<string> BoundClasses(FieldCatalog catalog);

    /// <summary>
    /// The bound classes a document should carry, given its current canonical values of the security-affecting fields
    /// (fields without a value are absent from <paramref name="securityValues"/>).
    /// </summary>
    IReadOnlySet<string> Derive(FieldCatalog catalog, IReadOnlyDictionary<int, JsonNode> securityValues);
}

/// <summary>The default until E05-T06 configures bindings: no class is derived from coding.</summary>
public sealed class NoRestrictionClassBinding : IRestrictionClassBinding
{
    public static NoRestrictionClassBinding Instance { get; } = new();

    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    public IReadOnlySet<string> BoundClasses(FieldCatalog catalog) => None;

    public IReadOnlySet<string> Derive(FieldCatalog catalog, IReadOnlyDictionary<int, JsonNode> securityValues) => None;
}

/// <summary>A restriction class added to or removed from a document by a coding write.</summary>
public sealed record RestrictionClassChange(Guid DocumentId, string ClassKey, bool Added);
