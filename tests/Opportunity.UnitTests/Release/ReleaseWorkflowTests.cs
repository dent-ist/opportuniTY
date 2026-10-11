using System.Text.Json;
using System.Text.RegularExpressions;

using AwesomeAssertions;

using Opportunity.UnitTests.Security;

using YamlDotNet.Serialization;

namespace Opportunity.UnitTests.Release;

/// <summary>
/// E01-T06: the release workflow cannot run on GitHub here, so its load-bearing structure is pinned by tests: releases
/// start only from a merged release PR, nothing is published before the N-1 → N upgrade test, the Compose bundle is
/// built from verified digests before the tag exists, actions are pinned by commit, and the release-please files agree.
/// </summary>
public sealed partial class ReleaseWorkflowTests
{
    private static readonly string Root = PermissionMatrixTests.RepositoryRoot();

    [Fact]
    public void Releases_start_from_main_only_never_from_a_pushed_tag()
    {
        var on = Map(Workflow("release.yml")["on"]);
        on.Keys.Should().BeEquivalentTo(["push", "workflow_dispatch"]);
        var push = Map(on["push"]);
        push.Keys.Should().BeEquivalentTo(["branches"], "a tag push must not publish a second, untested release");
        List(push["branches"]).Should().Equal("main");
    }

    [Fact]
    public void Nothing_is_published_before_the_upgrade_test_passed()
    {
        var jobs = Jobs("release.yml");

        Needs(jobs["upgrade-test"]).Should().Contain("release-please");
        Scalar(jobs["upgrade-test"], "if").Should().Contain("needs.release-please.outputs.release == 'true'");
        var upgradeRuns = Runs(jobs["upgrade-test"]);
        upgradeRuns.Should().Contain(r => r.Contains("--filter-class Opportunity.IntegrationTests.Migrations.UpgradeTests", StringComparison.Ordinal));
        upgradeRuns.Should().Contain(r => r.Contains("tools/release/upgrade-test.sh", StringComparison.Ordinal) && r.Contains("--to-api", StringComparison.Ordinal));

        Needs(jobs["images"]).Should().Contain("upgrade-test");
        Scalar(jobs["images"], "uses").Should().Be("./.github/workflows/container-images.yml");
        var with = Map(Map(jobs["images"])["with"]);
        with["publish"].Should().Be("true");
        ((string)with["version"]).Should().Contain("needs.release-please.outputs.version");

        Needs(jobs["github-release"]).Should().Contain("images");
    }

    [Fact]
    public void The_tag_and_github_release_come_after_the_bundle_notice_and_matrix()
    {
        var runs = Runs(Jobs("release.yml")["github-release"]);
        int Index(string fragment) => runs.FindIndex(r => r.Contains(fragment, StringComparison.Ordinal));

        var digests = Index("cosign verify");
        var matrix = Index("compatibility --version");
        var notice = Index("tools/release/generate-notice.py");
        var bundle = Index("tools/release/build-compose-bundle.sh");
        var tag = Index("github-release");
        var upload = Index("gh release upload");

        new[] { digests, matrix, notice, bundle, tag, upload }.Should().NotContain(-1);
        digests.Should().BeLessThan(bundle);
        matrix.Should().BeLessThan(bundle);
        notice.Should().BeLessThan(bundle);
        bundle.Should().BeLessThan(tag);
        tag.Should().BeLessThan(upload);
        runs[bundle].Should().Contain("--verify", "the bundle fails unless every image is referenced by digest");
    }

    [Fact]
    public void Release_workflow_actions_are_pinned_to_full_commit_shas()
    {
        foreach (var file in new[] { "release.yml", "pr-title.yml", "container-images.yml" })
        {
            var text = File.ReadAllText(Path.Combine(Root, ".github", "workflows", file));
            foreach (Match use in UsesLine().Matches(text))
            {
                var target = use.Groups[1].Value;
                if (!target.StartsWith("./", StringComparison.Ordinal))
                {
                    target.Should().MatchRegex("@[0-9a-f]{40}$", "{0}: third-party actions are pinned by commit SHA", file);
                }
            }
        }
    }

    [Fact]
    public void Release_please_files_agree_and_the_cli_is_pinned_in_versions_env()
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "release-please-config.json")));
        var package = config.RootElement.GetProperty("packages").GetProperty(".");
        package.GetProperty("release-type").GetString().Should().Be("simple");
        File.Exists(Path.Combine(Root, package.GetProperty("changelog-path").GetString()!)).Should().BeTrue();
        config.RootElement.GetProperty("include-v-in-tag").GetBoolean().Should().BeTrue("detect-release.sh and the bundle use vX.Y.Z tags");

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, ".release-please-manifest.json")));
        var version = manifest.RootElement.GetProperty(".").GetString()!;
        version.Should().MatchRegex(@"^\d+\.\d+\.\d+$");
        File.ReadAllText(Path.Combine(Root, "version.txt")).Trim().Should().Be(version, "release-please updates both together");

        File.ReadLines(Path.Combine(Root, "versions.env")).Should().Contain(l => SemVerLine().IsMatch(l), "RELEASE_PLEASE pins the CLI");
    }

    [Fact]
    public void Release_scripts_are_executable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var script in Directory.EnumerateFiles(Path.Combine(Root, "tools", "release"))
                     .Where(f => f.EndsWith(".sh", StringComparison.Ordinal) || f.EndsWith(".py", StringComparison.Ordinal)))
        {
            File.GetUnixFileMode(script).Should().HaveFlag(UnixFileMode.UserExecute, "{0} is run directly by the workflows", script);
        }
    }

    private static Dictionary<object, object> Workflow(string name) =>
        new DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(
            File.ReadAllText(Path.Combine(Root, ".github", "workflows", name)));

    private static Dictionary<object, object> Jobs(string name) => Map(Workflow(name)["jobs"]);

    private static Dictionary<object, object> Map(object node) => (Dictionary<object, object>)node;

    private static List<string> List(object node) => [.. ((List<object>)node).Select(o => (string)o)];

    private static string Scalar(object job, string key) => (string)Map(job)[key];

    private static List<string> Needs(object job) => Map(job)["needs"] switch
    {
        string one => [one],
        var many => List(many),
    };

    private static List<string> Runs(object job) =>
        [.. ((List<object>)Map(job)["steps"]).Select(s => (Dictionary<object, object>)s)
            .Select(s => s.TryGetValue("run", out var run) ? (string)run : s.TryGetValue("uses", out var uses) ? (string)uses : "")];

    [GeneratedRegex(@"^\s*(?:-\s*)?uses:\s*(\S+)", RegexOptions.Multiline)]
    private static partial Regex UsesLine();

    [GeneratedRegex(@"^RELEASE_PLEASE=\d+\.\d+\.\d+$")]
    private static partial Regex SemVerLine();
}
