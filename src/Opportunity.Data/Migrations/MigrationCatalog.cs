using System.Reflection;

namespace Opportunity.Data.Migrations;

/// <summary>The migration scripts embedded in this assembly (<c>Migrations/Scripts/*.sql</c>), ordered by version.</summary>
public static class MigrationCatalog
{
    private const string ResourcePrefix = "Opportunity.Data.Migrations.Scripts.";

    private static readonly Lazy<IReadOnlyList<MigrationScript>> Embedded = new(LoadEmbedded);

    public static IReadOnlyList<MigrationScript> Scripts => Embedded.Value;

    /// <summary>Schema version this build of the application expects (highest embedded script version).</summary>
    public static int LatestVersion => Scripts.Count == 0 ? 0 : Scripts[^1].Version;

    /// <summary>Orders scripts by version and rejects duplicate versions.</summary>
    public static IReadOnlyList<MigrationScript> Order(IEnumerable<MigrationScript> scripts)
    {
        var ordered = scripts.OrderBy(s => s.Version).ToList();
        var duplicate = ordered.GroupBy(s => s.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new MigrationException(
                $"Duplicate migration version {duplicate.Key}: {string.Join(", ", duplicate.Select(s => s.Name))}.");
        }

        return ordered;
    }

    private static IReadOnlyList<MigrationScript> LoadEmbedded()
    {
        var assembly = typeof(MigrationCatalog).Assembly;
        return Order(assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(n => MigrationScript.Create(n[ResourcePrefix.Length..], Read(assembly, n))));
    }

    private static string Read(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new MigrationException($"Embedded migration '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
