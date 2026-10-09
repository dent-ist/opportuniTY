using AwesomeAssertions;

using DotNet.Testcontainers.Builders;

using Opportunity.Testing.Images;

namespace Opportunity.IntegrationTests.Observability;

/// <summary>
/// E19-T05: the Prometheus configuration and alert rules of the observability profile are valid for the pinned
/// Prometheus, and every alert fires in its induced-failure case of <c>alerts.test.yaml</c> (interactive lag p95 > 1 s,
/// bulk lag > 2 min, DLQ > 0, WAL archive lag > 4 min, outbox age > 60 s and the rest) and stays quiet on healthy
/// series. Runs <c>promtool</c> from the pinned image; the Compose stack is not started.
/// </summary>
public sealed class ObservabilityRuleTests
{
    private const string ConfigDirectory = "/etc/prometheus/";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Prometheus_config_is_valid_and_every_alert_fires_on_its_induced_failure()
    {
        var observability = new DirectoryInfo(Path.Combine(
            Path.GetDirectoryName(VersionsFile.Locate())!, "deploy", "docker-compose", "observability"));
        await using var promtool = new ContainerBuilder(ContainerImages.Prometheus)
            .WithEntrypoint("sleep")
            .WithCommand("600")
            .WithResourceMapping(new FileInfo(Path.Combine(observability.FullName, "prometheus.yaml")), ConfigDirectory)
            .WithResourceMapping(new FileInfo(Path.Combine(observability.FullName, "alerts.yaml")), ConfigDirectory)
            .WithResourceMapping(new FileInfo(Path.Combine(observability.FullName, "alerts.test.yaml")), ConfigDirectory)
            .Build();
        await promtool.StartAsync(Ct);

        var config = await promtool.ExecAsync(["promtool", "check", "config", ConfigDirectory + "prometheus.yaml"], Ct);
        config.ExitCode.Should().Be(0, "promtool check config:\n{0}{1}", config.Stdout, config.Stderr);

        var rules = await promtool.ExecAsync(["promtool", "test", "rules", ConfigDirectory + "alerts.test.yaml"], Ct);
        rules.ExitCode.Should().Be(0, "promtool test rules:\n{0}{1}", rules.Stdout, rules.Stderr);
    }
}
