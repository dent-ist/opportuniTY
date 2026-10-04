using System.Text.RegularExpressions;

using AwesomeAssertions;

using Opportunity.UnitTests.Security;

namespace Opportunity.UnitTests.Operations;

/// <summary>
/// E06-T06: every Prometheus alert links to a runbook (<c>runbook_url</c>) that exists in docs/operations, with the
/// heading its anchor names.
/// </summary>
public sealed partial class AlertRunbookTests
{
    [Fact]
    public void Every_alert_links_to_an_existing_runbook_section()
    {
        var root = PermissionMatrixTests.RepositoryRoot();
        var rules = File.ReadAllText(Path.Combine(root, "deploy", "docker-compose", "observability", "alerts.yaml"));
        var alerts = AlertStart().Split(rules).Skip(1).ToList();
        alerts.Should().NotBeEmpty();

        foreach (var alert in alerts)
        {
            var name = alert.Split('\n')[0].Trim();
            var link = RunbookUrl().Match(alert);
            link.Success.Should().BeTrue("alert {0} needs a runbook_url annotation", name);
            var (path, anchor) = (link.Groups["path"].Value, link.Groups["anchor"].Value);
            path.Should().StartWith("docs/operations/", "alert {0}", name);
            var runbook = Path.Combine(root, path);
            File.Exists(runbook).Should().BeTrue("alert {0} links to {1}", name, path);
            if (anchor.Length > 0)
            {
                File.ReadAllLines(runbook).Where(l => l.StartsWith('#')).Select(Anchor)
                    .Should().Contain(anchor, "alert {0} links to a heading of {1}", name, path);
            }
        }
    }

    /// <summary>The GitHub-style anchor of a Markdown heading.</summary>
    private static string Anchor(string heading) =>
        NotAnchorCharacter().Replace(heading.TrimStart('#').Trim().ToLowerInvariant(), string.Empty).Replace(' ', '-');

    [GeneratedRegex(@"^\s*- alert:", RegexOptions.Multiline)]
    private static partial Regex AlertStart();

    [GeneratedRegex(@"runbook_url:\s*""?(?<path>[^""#\s]+)(?:#(?<anchor>[^""\s]+))?""?")]
    private static partial Regex RunbookUrl();

    [GeneratedRegex(@"[^a-z0-9 _-]")]
    private static partial Regex NotAnchorCharacter();
}
