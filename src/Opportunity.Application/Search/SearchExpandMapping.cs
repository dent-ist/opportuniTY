using Opportunity.Contracts.Search;
using Opportunity.Core.Documents;

namespace Opportunity.Application.Search;

/// <summary>Maps the API's <see cref="SearchExpand"/> to the one expansion definition (<see cref="RelationshipExpansion"/>) and back.</summary>
public static class SearchExpandMapping
{
    public static RelationshipExpansion ToExpansion(this SearchExpand? expand) =>
        expand is null ? RelationshipExpansion.None : new RelationshipExpansion(expand.Family == true, expand.Duplicates == true, expand.Thread == true);

    /// <summary>Null when nothing is expanded, so responses omit the expansion of an ordinary search.</summary>
    public static SearchExpand? ToContract(this RelationshipExpansion expansion) =>
        expansion.IsNone ? null : new SearchExpand(expansion.Family, expansion.Duplicates, expansion.Thread);

    public static SearchExpandedBy ToExpandedBy(this RelationshipKind kind) => kind switch
    {
        RelationshipKind.Family => SearchExpandedBy.Family,
        RelationshipKind.Duplicate => SearchExpandedBy.Duplicate,
        RelationshipKind.Thread => SearchExpandedBy.Thread,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown relationship kind."),
    };
}
