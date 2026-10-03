using System.Globalization;

using Opportunity.Application.Search.Indexing;

namespace Opportunity.Search.Indexing;

/// <summary>
/// Physical naming (ADR-006 §3). The only place index, alias and template names are built; they never leave
/// <c>Opportunity.Search</c> (architecture test).
/// </summary>
internal sealed class IndexNames(string prefix)
{
    public string Prefix { get; } = prefix;

    /// <summary>Stable, unfiltered alias of a shared pool: <c>{prefix}-shared-{nnn}</c>.</summary>
    public string SharedAlias(int pool) => string.Create(CultureInfo.InvariantCulture, $"{Prefix}-shared-{pool:000}");

    /// <summary>Stable alias of a dedicated index: <c>{prefix}-ws-{workspaceId:N}</c>.</summary>
    public string DedicatedAlias(Guid workspaceId) => $"{Prefix}-ws-{workspaceId:N}";

    public string Alias(Guid workspaceId, IndexPlacementKind kind, int? sharedPool) => kind switch
    {
        IndexPlacementKind.Shared => SharedAlias(sharedPool ?? throw new InvalidOperationException("A shared placement needs a pool.")),
        IndexPlacementKind.Dedicated => DedicatedAlias(workspaceId),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>Physical index behind <paramref name="alias"/> for generation <paramref name="generation"/>.</summary>
    public static string Physical(string alias, int generation) => string.Create(CultureInfo.InvariantCulture, $"{alias}-g{generation}");

    public string Template(int generation) => string.Create(CultureInfo.InvariantCulture, $"{Prefix}-projection-g{generation}");

    public string[] TemplatePatterns(int generation) =>
    [
        string.Create(CultureInfo.InvariantCulture, $"{Prefix}-shared-*-g{generation}"),
        string.Create(CultureInfo.InvariantCulture, $"{Prefix}-ws-*-g{generation}"),
    ];
}
