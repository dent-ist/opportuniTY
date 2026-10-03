using System.Text;

namespace Opportunity.Core.Security;

/// <summary>
/// Renders <c>docs/security/permission-matrix.md</c> from <see cref="PermissionCatalog"/>, <see cref="RoleCatalog"/>
/// and <see cref="RestrictionClasses"/> (ADR-015 D5.6). A unit test fails when the committed file differs.
/// </summary>
public static class PermissionMatrix
{
    public static string Render()
    {
        var roles = RoleCatalog.All;
        var md = new StringBuilder();
        md.Append("""
            # Permission matrix

            <!-- Generated from Opportunity.Core.Security (PermissionCatalog, RoleCatalog, RestrictionClasses). Do not edit by hand:
                 run the unit tests with OPPORTUNITY_UPDATE_GOLDEN=1 to regenerate. -->

            The closed permission set and built-in workspace roles of the policy decision point (ADR-015 D5). Roles are
            assigned per workspace to users or IdP groups; a principal holds the union of its roles' permissions. Default
            deny; deny overrides allow. Restriction classes and ethical walls then hide documents (404) regardless of
            role, Workspace Admin included; only an active break-glass activation lifts them, and only for the
            permissions in the Break-glass column (Q-45).

            ## Roles × permissions

            """).Append('\n');

        md.Append("| Permission |");
        foreach (var role in roles)
        {
            md.Append(' ').Append(role.DisplayName).Append(" |");
        }

        md.Append('\n').Append("|---|");
        foreach (var _ in roles)
        {
            md.Append(":---:|");
        }

        md.Append('\n');
        foreach (var permission in PermissionCatalog.All)
        {
            md.Append("| `").Append(permission.Name).Append("` |");
            foreach (var role in roles)
            {
                md.Append(role.Grants.Contains(permission.Permission) ? " ✔ |" : " |");
            }

            md.Append('\n');
        }

        md.Append("""

            Break-glass is not membership on its own: it counts only while the holder has an active activation in the
            workspace (reason and MFA required, default 60 minutes, at most 4 hours). Decisions that rely on it are
            flagged, so the audit event of the action carries access path `BreakGlass`.

            ## Permissions

            | Permission | Allows |
            |---|---|

            """);
        foreach (var permission in PermissionCatalog.All)
        {
            md.Append("| `").Append(permission.Name).Append("` | ").Append(permission.Description).Append(" |\n");
        }

        md.Append("""

            ## Role keys

            | Role | Key (`workspace_role_assignment.role`) |
            |---|---|

            """);
        foreach (var role in roles)
        {
            md.Append("| ").Append(role.DisplayName).Append(" | `").Append(role.Key).Append("` |\n");
        }

        md.Append("""

            ## Default restriction-class grants (Q-11)

            Seeded for every workspace; workspace admins may tighten them. A document carrying a class is visible only
            to principals holding a role granted that class.

            | Class |
            """);
        var gradable = roles.Where(r => r.Role != WorkspaceRole.BreakGlass).ToList();
        foreach (var role in gradable)
        {
            md.Append(' ').Append(role.DisplayName).Append(" |");
        }

        md.Append('\n').Append("|---|");
        foreach (var _ in gradable)
        {
            md.Append(":---:|");
        }

        md.Append('\n');
        foreach (var (classKey, _, granted) in RestrictionClasses.BuiltIn)
        {
            md.Append("| `").Append(classKey).Append("` |");
            foreach (var role in gradable)
            {
                md.Append(granted.Contains(role.Role) ? " ✔ |" : " |");
            }

            md.Append('\n');
        }

        return md.ToString();
    }
}
