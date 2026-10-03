using System.Text.RegularExpressions;

using AwesomeAssertions;

namespace Opportunity.ArchitectureTests;

/// <summary>
/// E04-T04: the interim coding schema is swappable after the coding spike (E18-T03 / ADR-004a). Only the PostgreSQL
/// coding adapter and the migrations may name its tables; everything else goes through <c>ICodingRepository</c>.
/// </summary>
public partial class CodingStoreIsolationTests
{
    private static readonly string[] Allowed =
    [
        Path.Combine("src", "Opportunity.Data", "Coding", "CodingRepository.cs"),
        Path.Combine("src", "Opportunity.Data", "Migrations", "Scripts"),
    ];

    [Fact]
    public void Only_the_coding_adapter_references_the_physical_coding_tables()
    {
        var root = FindRepositoryRoot();
        string[] offenders = [.. Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".sql", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .Where(relative => !Allowed.Any(a => relative.StartsWith(a, StringComparison.Ordinal)))
            .Where(relative => CodingTable().IsMatch(File.ReadAllText(Path.Combine(root, relative))))];

        offenders.Should().BeEmpty("coding storage is reached only through ICodingRepository");
    }

    [Fact]
    public void The_coding_port_exposes_no_storage_types()
    {
        var port = typeof(Application.Coding.ICodingRepository);
        var types = port.GetMethods()
            .SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType))
            .SelectMany(Flatten)
            .Distinct();

        types.Should().OnlyContain(t => t.Namespace!.StartsWith("System", StringComparison.Ordinal)
            || t.Namespace.StartsWith("Opportunity.Application", StringComparison.Ordinal)
            || t.Namespace.StartsWith("Opportunity.Core", StringComparison.Ordinal));
    }

    private static IEnumerable<Type> Flatten(Type type) =>
        type.IsGenericType ? type.GetGenericArguments().SelectMany(Flatten).Append(type.GetGenericTypeDefinition()) : [type];

    [GeneratedRegex(@"\b(document_coding_field|document_coding_choice|coding_event|coding_write)\b", RegexOptions.CultureInvariant)]
    private static partial Regex CodingTable();

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Opportunity.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (Opportunity.slnx) not found.");
    }
}
