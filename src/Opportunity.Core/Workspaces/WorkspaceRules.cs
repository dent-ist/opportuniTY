using System.Text.RegularExpressions;

namespace Opportunity.Core.Workspaces;

/// <summary>Workspace settings limits, mirrored by the V0002/V0014 check constraints.</summary>
public static partial class WorkspaceRules
{
    public const int MaxNameLength = 200;
    public const int MaxMatterNumberLength = 100;
    public const int MaxTimeZoneLength = 64;
    public const string DefaultStorageProfile = "default";

    public static bool IsStorageProfileName(string? value) => value is not null && StorageProfilePattern().IsMatch(value);

    [GeneratedRegex("^[a-z][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex StorageProfilePattern();
}
