using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

using AwesomeAssertions;

using Opportunity.Application.Telemetry;
using Opportunity.UnitTests.Security;

using YamlDotNet.Serialization;

namespace Opportunity.UnitTests.Operations;

/// <summary>
/// E19-T05: the alert rules and the provisioned Grafana dashboards of the observability profile are well formed, query
/// only metrics that exist (the Prometheus translation of the application metric catalog, or a known exporter), and
/// every alert has an induced-failure case. <c>promtool</c> itself runs in <c>ObservabilityRuleTests</c> and CI.
/// </summary>
public sealed partial class ObservabilityConfigTests
{
    /// <summary>The alerts the ticket requires to fire in an induced-failure test.</summary>
    private static readonly string[] RequiredAlerts =
        ["InteractiveCommitToSearchableSlow", "BulkIndexLagHigh", "DeadLetterQueueNotEmpty", "WalArchiveLagHigh", "OutboxOldestAgeHigh"];

    /// <summary>Metric prefixes of the exporters and the .NET/OpenTelemetry instrumentation scraped or received.</summary>
    private static readonly string[] ExporterPrefixes =
        ["pg_", "elasticsearch_", "rabbitmq_", "http_server_", "db_client_", "dotnet_", "up"];

    private static readonly string Observability =
        Path.Combine(PermissionMatrixTests.RepositoryRoot(), "deploy", "docker-compose", "observability");

    [Fact]
    public void Every_alert_has_a_severity_texts_and_a_firing_induced_failure_case()
    {
        var alerts = Alerts();
        alerts.Select(a => a.Name).Should().Contain(RequiredAlerts).And.OnlyHaveUniqueItems();
        var tests = Yaml(Path.Combine(Observability, "alerts.test.yaml"));
        ((List<object>)tests["rule_files"]).Should().Equal("alerts.yaml");
        var firing = ((List<object>)tests["tests"]).Cast<Dictionary<object, object>>()
            .SelectMany(t => ((List<object>)t["alert_rule_test"]).Cast<Dictionary<object, object>>())
            .Where(c => c.TryGetValue("exp_alerts", out var expected) && expected is List<object> { Count: > 0 })
            .Select(c => (string)c["alertname"])
            .ToHashSet(StringComparer.Ordinal);

        foreach (var alert in alerts)
        {
            alert.Labels.Should().ContainKey("severity", alert.Name);
            alert.Labels["severity"].Should().BeOneOf(["critical", "warning"], alert.Name);
            alert.Annotations.Keys.Should().Contain(["summary", "description", "runbook_url"], alert.Name);
            firing.Should().Contain(alert.Name, "alert {0} needs a case in alerts.test.yaml where it fires", alert.Name);
        }
    }

    [Fact]
    public void Alert_rules_and_their_tests_use_only_catalog_metrics_and_attributes()
    {
        var texts = Alerts().Select(a => (a.Name, a.Expression)).ToList();
        texts.Add(("alerts.test.yaml", File.ReadAllText(Path.Combine(Observability, "alerts.test.yaml"))));
        AssertKnownMetrics(texts);
    }

    [Fact]
    public void Dashboards_are_well_formed_and_query_only_known_metrics()
    {
        var datasources = ((List<object>)Yaml(Path.Combine(Observability, "grafana", "provisioning", "datasources", "datasources.yaml"))["datasources"])
            .Cast<Dictionary<object, object>>().Select(d => (string)d["uid"]).ToHashSet(StringComparer.Ordinal);
        var files = Directory.GetFiles(Path.Combine(Observability, "grafana", "dashboards"), "*.json").Order(StringComparer.Ordinal).ToList();
        files.Select(Path.GetFileName).Should().Contain(["opportunity-overview.json", "opportunity-pipeline.json"]);

        var uids = new List<string>();
        var expressions = new List<(string, string)>();
        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            var uid = root.GetProperty("uid").GetString()!;
            uids.Add(uid);
            Path.GetFileNameWithoutExtension(file).Should().Be(uid, "the file is named after the dashboard uid");
            root.GetProperty("editable").GetBoolean().Should().BeFalse("dashboards are provisioned from the repository");

            var panels = root.GetProperty("panels").EnumerateArray().ToList();
            panels.Select(p => p.GetProperty("id").GetInt32()).Should().OnlyHaveUniqueItems(uid);
            foreach (var panel in panels)
            {
                var grid = panel.GetProperty("gridPos");
                (grid.GetProperty("x").GetInt32() + grid.GetProperty("w").GetInt32()).Should().BeLessThanOrEqualTo(24, uid);
                if (panel.TryGetProperty("datasource", out var datasource))
                {
                    datasources.Should().Contain(datasource.GetProperty("uid").GetString(), "{0} panel {1}", uid, panel.GetProperty("title"));
                }

                foreach (var target in panel.TryGetProperty("targets", out var targets) ? targets.EnumerateArray() : Enumerable.Empty<JsonElement>())
                {
                    if (target.GetProperty("datasource").GetProperty("uid").GetString() == "prometheus")
                    {
                        var expr = target.GetProperty("expr").GetString();
                        expr.Should().NotBeNullOrWhiteSpace();
                        Balanced(expr!).Should().BeTrue("{0}: {1}", uid, expr);
                        expressions.Add(($"{uid}: {panel.GetProperty("title")}", expr!));
                    }
                }
            }
        }

        uids.Should().OnlyHaveUniqueItems();
        AssertKnownMetrics(expressions);
    }

    [Fact]
    public void Committed_dashboards_are_the_generator_output()
    {
        var output = Directory.CreateTempSubdirectory("opportunity-dashboards-");
        try
        {
            var generator = Path.Combine(Observability, "grafana", "generate-dashboard.py");
            using var python = Process.Start(new ProcessStartInfo("python3", ["-I", generator, output.FullName])
            {
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            var error = python.StandardError.ReadToEnd();
            python.WaitForExit();
            python.ExitCode.Should().Be(0, error);

            foreach (var generated in output.GetFiles("*.json"))
            {
                var committed = Path.Combine(Observability, "grafana", "dashboards", generated.Name);
                File.Exists(committed).Should().BeTrue("{0} is generated but not committed", generated.Name);
                File.ReadAllText(committed).Should().Be(File.ReadAllText(generated.FullName),
                    "{0} must be regenerated: python3 generate-dashboard.py dashboards", generated.Name);
            }

            output.GetFiles("*.json").Should().HaveCount(Directory.GetFiles(Path.Combine(Observability, "grafana", "dashboards"), "*.json").Length);
        }
        finally
        {
            output.Delete(recursive: true);
        }
    }

    /// <summary>The Prometheus translation (ADR-017 §5) of the catalog: series names and label names.</summary>
    internal static IReadOnlySet<string> CatalogPrometheusNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var metric in OpportunityMetricCatalog.All)
        {
            var name = metric.Name.Replace('.', '_') + metric.Unit switch { "s" => "_seconds", "By" => "_bytes", _ => string.Empty };
            switch (metric.Kind)
            {
                case MetricKind.Counter:
                    names.Add(name + "_total");
                    break;
                case MetricKind.Histogram:
                    names.UnionWith([name + "_bucket", name + "_count", name + "_sum"]);
                    break;
                default:
                    names.Add(name);
                    break;
            }

            names.UnionWith(metric.Attributes.Select(a => a.Replace('.', '_')));
        }

        return names;
    }

    private static void AssertKnownMetrics(IEnumerable<(string Source, string Text)> texts)
    {
        var known = CatalogPrometheusNames();
        foreach (var (source, text) in texts)
        {
            foreach (var token in ApplicationToken().Matches(text).Select(m => m.Value).Distinct())
            {
                known.Should().Contain(token, "{0} uses {1}, which is not a catalog metric or attribute", source, token);
            }

            foreach (var metric in MetricBeforeSelector().Matches(text).Select(m => m.Groups["name"].Value).Distinct())
            {
                (known.Contains(metric) || ExporterPrefixes.Any(p => metric.StartsWith(p, StringComparison.Ordinal)))
                    .Should().BeTrue("{0} queries {1}, which no exporter or catalog metric provides", source, metric);
            }
        }
    }

    private static List<AlertRule> Alerts() =>
        [.. ((List<object>)Yaml(Path.Combine(Observability, "alerts.yaml"))["groups"])
            .Cast<Dictionary<object, object>>()
            .SelectMany(g => ((List<object>)g["rules"]).Cast<Dictionary<object, object>>())
            .Select(r => new AlertRule(
                (string)r["alert"],
                (string)r["expr"],
                Strings(r, "labels"),
                Strings(r, "annotations")))];

    private static Dictionary<string, string> Strings(Dictionary<object, object> rule, string key) =>
        rule.TryGetValue(key, out var value) && value is Dictionary<object, object> map
            ? map.ToDictionary(e => (string)e.Key, e => (string)e.Value, StringComparer.Ordinal)
            : [];

    private static Dictionary<string, object> Yaml(string path) =>
        new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));

    private static bool Balanced(string expression)
    {
        var depth = 0;
        foreach (var c in expression)
        {
            depth += c switch { '(' or '{' or '[' => 1, ')' or '}' or ']' => -1, _ => 0 };
            if (depth < 0)
            {
                return false;
            }
        }

        return depth == 0;
    }

    private sealed record AlertRule(string Name, string Expression, Dictionary<string, string> Labels, Dictionary<string, string> Annotations);

    /// <summary>Application metric and label names: <c>opportunity_*</c> and the semantic-convention <c>messaging_*</c>.</summary>
    [GeneratedRegex(@"(?<![a-z0-9_])(?:opportunity|messaging)_[a-z0-9_]+")]
    private static partial Regex ApplicationToken();

    /// <summary>A metric name directly followed by a label selector or range, e.g. <c>pg_up{…}</c> or <c>up[5m]</c>.</summary>
    [GeneratedRegex(@"(?<![a-z0-9_.$])(?<name>[a-z_][a-z0-9_]*)\s*[{\[]")]
    private static partial Regex MetricBeforeSelector();
}
