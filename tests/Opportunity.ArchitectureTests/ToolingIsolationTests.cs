using AwesomeAssertions;

namespace Opportunity.ArchitectureTests;

/// <summary>Tooling under tools/ (e.g. the synthetic corpus generator) must never become a production dependency.</summary>
public class ToolingIsolationTests
{
    [Fact]
    public void Production_projects_do_not_reference_tool_projects()
    {
        string root = FindRepositoryRoot();
        string[] offenders = [.. Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(path =>
            {
                string text = File.ReadAllText(path);
                return text.Contains("tools", StringComparison.OrdinalIgnoreCase)
                    && (text.Contains("Opportunity.DataGenerator", StringComparison.Ordinal) || text.Contains("Opportunity.Benchmarks", StringComparison.Ordinal));
            })
            .Select(path => Path.GetRelativePath(root, path))];

        offenders.Should().BeEmpty("production code under src/ must not depend on tools/ projects");
    }

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