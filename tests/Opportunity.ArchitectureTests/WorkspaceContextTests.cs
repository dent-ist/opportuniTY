using System.Text.RegularExpressions;

using AwesomeAssertions;

namespace Opportunity.ArchitectureTests;

/// <summary>
/// E05-T03 / ADR-015 D7.2: the RLS workspace context is set in exactly one place, <c>WorkspaceTransaction</c>, and
/// repositories reach PostgreSQL only through it. The migrator (schema and history, no tenant rows) is the exception.
/// </summary>
public partial class WorkspaceContextTests
{
    private static readonly string Helper = Path.Combine("src", "Opportunity.Data", "WorkspaceTransaction.cs");

    private static readonly string Migrations = Path.Combine("src", "Opportunity.Data", "Migrations");

    [Fact]
    public void Only_the_workspace_transaction_sets_the_rls_context()
    {
        Offenders(path => path != Helper && !path.StartsWith(Migrations, StringComparison.Ordinal), ContextSetting())
            .Should().BeEmpty("app.workspace_id is set only by WorkspaceTransaction, transaction-locally");
    }

    [Fact]
    public void No_runtime_code_switches_row_security_off_or_changes_role()
    {
        Offenders(path => !path.StartsWith(Migrations, StringComparison.Ordinal), RlsBypass())
            .Should().BeEmpty("ADR-015 D7.4.5: no runtime path sets row_security = off or switches role");
    }

    [Fact]
    public void Data_repositories_open_connections_only_through_the_workspace_transaction()
    {
        var data = Path.Combine("src", "Opportunity.Data");
        Offenders(path => path.StartsWith(data, StringComparison.Ordinal) && path != Helper
                && !path.StartsWith(Migrations, StringComparison.Ordinal), DirectConnection())
            .Should().BeEmpty("repositories use WorkspaceTransaction.BeginAsync so RLS always has a context");
    }

    private static string[] Offenders(Func<string, bool> include, Regex pattern)
    {
        var root = FindRepositoryRoot();
        return [.. Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .Where(include)
            .Where(relative => pattern.IsMatch(File.ReadAllText(Path.Combine(root, relative))))];
    }

    [GeneratedRegex(@"app\.workspace_id|set_config|SET\s+LOCAL", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ContextSetting();

    [GeneratedRegex(@"row_security|BYPASSRLS|SET\s+(LOCAL\s+)?ROLE|SESSION\s+AUTHORIZATION", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RlsBypass();

    [GeneratedRegex(@"OpenConnection|CreateConnection|CreateCommand\(|CreateBatch\(|new\s+NpgsqlConnection|BeginTransaction", RegexOptions.CultureInvariant)]
    private static partial Regex DirectConnection();

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
