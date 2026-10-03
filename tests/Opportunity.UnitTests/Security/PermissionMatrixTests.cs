using System.Text.RegularExpressions;

using AwesomeAssertions;

using Opportunity.Core.Security;

namespace Opportunity.UnitTests.Security;

/// <summary>
/// E05-T02: the permission matrix document is generated from code and the code reproduces the ADR-015 D5.7 table.
/// Run with <c>OPPORTUNITY_UPDATE_GOLDEN=1</c> to rewrite <c>docs/security/permission-matrix.md</c>.
/// </summary>
public sealed partial class PermissionMatrixTests
{
    private const string UpdateVariable = "OPPORTUNITY_UPDATE_GOLDEN";

    [Fact]
    public void Committed_permission_matrix_matches_the_code()
    {
        var path = Path.Combine(RepositoryRoot(), "docs", "security", "permission-matrix.md");
        var generated = PermissionMatrix.Render();
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            File.WriteAllText(path, generated);
            return;
        }

        File.Exists(path).Should().BeTrue("docs/security/permission-matrix.md is generated from code; run with {0}=1", UpdateVariable);
        File.ReadAllText(path).ReplaceLineEndings("\n").Should().Be(
            generated,
            "docs/security/permission-matrix.md diverges from Opportunity.Core.Security; regenerate it with {0}=1",
            UpdateVariable);
    }

    [Fact]
    public void Code_reproduces_the_adr_015_initial_role_matrix()
    {
        var adr = File.ReadAllLines(Path.Combine(RepositoryRoot(), "docs", "adr", "0015-security-architecture-and-trust-boundaries.md"))
            .Select(l => l.Trim()).ToArray();
        var header = Array.FindIndex(adr, l => l.StartsWith("| Permission | Workspace Admin |", StringComparison.Ordinal));
        header.Should().BeGreaterThan(0, "ADR-015 D5.7 holds the initial matrix");
        string[] columns =
            ["WorkspaceAdmin", "Reviewer", "QcReviewer", "PrivilegeReviewer", "ProductionManager", "Auditor", "BreakGlass"];

        var seen = new HashSet<Permission>();
        foreach (var row in adr.Skip(header + 2).TakeWhile(l => l.StartsWith('|')))
        {
            var cells = row.Split('|')[1..^1];
            var names = PermissionName().Matches(cells[0]).Select(m => m.Groups[1].Value).ToList();
            names.Should().NotBeEmpty(row);
            foreach (var name in names)
            {
                PermissionCatalog.TryParse(name, out var permission).Should().BeTrue("{0} from ADR-015 must be catalogued", name);
                seen.Add(permission);
                for (var i = 0; i < columns.Length; i++)
                {
                    RoleCatalog.TryParse(columns[i], out var role).Should().BeTrue();
                    role.Grants(permission).Should().Be(cells[i + 1].Contains('✔'), "{0} × {1} per ADR-015 D5.7", name, columns[i]);
                }
            }
        }

        seen.Should().BeEquivalentTo(Enum.GetValues<Permission>(), "every permission appears in ADR-015 D5.7 and vice versa");
    }

    [Fact]
    public void Catalogue_is_closed_and_consistent()
    {
        PermissionCatalog.All.Select(p => p.Permission).Should().BeEquivalentTo(Enum.GetValues<Permission>());
        PermissionCatalog.All.Select(p => p.Name).Should().OnlyHaveUniqueItems();
        PermissionCatalog.All.Should().OnlyContain(p => DottedName().IsMatch(p.Name));
        foreach (var info in PermissionCatalog.All)
        {
            PermissionCatalog.TryParse(info.Name, out var parsed).Should().BeTrue();
            parsed.Should().Be(info.Permission);
        }

        PermissionCatalog.TryParse("document.view", out _).Should().BeFalse("wire names are case-sensitive");
        RoleCatalog.All.Select(r => r.Role).Should().BeEquivalentTo(Enum.GetValues<WorkspaceRole>());
        RoleCatalog.All.Select(r => r.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Workspace_admin_holds_everything_and_break_glass_only_reads()
    {
        RoleCatalog.Get(WorkspaceRole.WorkspaceAdmin).Grants.Should().BeEquivalentTo(Enum.GetValues<Permission>());
        RoleCatalog.Get(WorkspaceRole.BreakGlass).Grants.Should().BeEquivalentTo(
            [Permission.DocumentView, Permission.SearchExecute, Permission.AuditRead], "Q-45: view, search and read audit only");
        RoleCatalog.Get(WorkspaceRole.Auditor).Grants.Should().NotContain(
            [Permission.CodingWrite, Permission.CodingBulk, Permission.ExportCreate, Permission.ProductionCreate, Permission.DocumentDownloadNative],
            "the auditor role is read-only");
    }

    internal static string RepositoryRoot()
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

    [GeneratedRegex(@"`([A-Za-z]+\.[A-Za-z]+)`")]
    private static partial Regex PermissionName();

    [GeneratedRegex(@"^[A-Z][A-Za-z]+\.[A-Z][A-Za-z]+$")]
    private static partial Regex DottedName();
}
