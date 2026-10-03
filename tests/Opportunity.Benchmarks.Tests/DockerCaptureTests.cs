using AwesomeAssertions;

using Opportunity.Benchmarks.Capture;
using Opportunity.Benchmarks.Infrastructure;

namespace Opportunity.Benchmarks.Tests;

public class DockerCaptureTests
{
    private const string Digest = "sha256:d74eeac9a635390a49bc21bd49fccd973de707e2a53a76ac49b552b8712ec46f";

    private static readonly PinnedImage[] Pins =
    [
        new() { Key = "POSTGRES", Repository = "postgres", Tag = "17", Digest = Digest },
        new() { Key = "OPENSEARCH", Repository = "opensearchproject/opensearch", Tag = "3.2.0", Digest = "sha256:" + new string('b', 64) },
    ];

    [Theory]
    [InlineData("postgres:17@" + Digest, null, true)]
    [InlineData("docker.io/library/postgres:17@" + Digest, null, true)]
    [InlineData("postgres:17", "postgres@" + Digest, true)]
    [InlineData("postgres:17", "postgres@sha256:0000000000000000000000000000000000000000000000000000000000000000", false)]
    public void Matches_running_images_to_versions_env_pins(string reference, string? repoDigest, bool matches)
    {
        ImagePin? pin = DockerCapture.MatchPin(reference, repoDigest is null ? null : [repoDigest], Pins);

        pin.Should().Be(new ImagePin("POSTGRES", Digest, matches));
    }

    [Fact]
    public void Unpinned_images_have_no_pin() =>
        DockerCapture.MatchPin("opportunity-local/opportunity-api:dev", null, Pins).Should().BeNull();

    [Fact]
    public void Records_jvm_and_cluster_settings_but_never_credentials()
    {
        IReadOnlyDictionary<string, string>? kept = DockerCapture.FilterEnvironment(
        [
            "OPENSEARCH_JAVA_OPTS=-Xms4g -Xmx4g",
            "discovery.type=single-node",
            "OPENSEARCH_INITIAL_ADMIN_PASSWORD=hunter2",
            "POSTGRES_PASSWORD=hunter2",
            "Workers__Enabled=all",
            "ObjectStorage__Provider=S3",
            "ObjectStorage__S3__SecretKey=hunter2",
            "ConnectionStrings__App=Host=postgres;Password=hunter2",
            "PATH=/usr/bin",
        ]);

        kept.Should().Equal(new Dictionary<string, string>
        {
            ["OPENSEARCH_JAVA_OPTS"] = "-Xms4g -Xmx4g",
            ["ObjectStorage__Provider"] = "S3",
            ["Workers__Enabled"] = "all",
            ["discovery.type"] = "single-node",
        });
    }

    [Fact]
    public async Task Inspect_parses_limits_command_and_compose_labels()
    {
        var runner = new FakeRunner();
        var docker = new DockerCapture(runner);

        IReadOnlyList<ContainerInfo> containers = await docker.InspectAsync(["pg"], Pins, TestContext.Current.CancellationToken);

        ContainerInfo pg = containers.Should().ContainSingle().Subject;
        pg.Name.Should().Be("opportunity-dev-postgres-1");
        pg.Service.Should().Be("postgres");
        pg.ComposeProject.Should().Be("opportunity-dev");
        pg.Command.Should().Equal("-c", "fsync=off", "-c", "ssl_key_password=<redacted>");
        pg.MemoryLimitBytes.Should().Be(4_294_967_296);
        pg.NanoCpus.Should().Be(4_000_000_000);
        pg.ShmSizeBytes.Should().Be(268_435_456);
        pg.Environment.Should().BeNull();
        pg.Image.RepoDigests.Should().Equal("postgres@" + Digest);
        pg.Pin.Should().Be(new ImagePin("POSTGRES", Digest, true));
    }

    [Fact]
    public void Workers_and_object_store_are_detected_from_compose_services()
    {
        static ContainerInfo Container(string service, string image, Dictionary<string, string>? env = null) => new()
        {
            Name = service,
            Service = service,
            Image = new ContainerImage { Reference = image, Id = "sha256:" + new string('c', 64) },
            Environment = env,
        };

        ContainerInfo[] containers =
        [
            Container("worker", "opportunity-worker:dev", new() { ["Workers__Enabled"] = "all", ["ObjectStorage__Provider"] = "S3" }),
            Container("seaweedfs", "chrislusf/seaweedfs:4.48@sha256:" + new string('d', 64)),
            Container("api", "opportunity-api:dev"),
        ];

        EnvironmentCapturer.DetectWorkers(containers).Should().Equal(new WorkerPool { Type = "all", Instances = 1 });
        EnvironmentCapturer.DetectObjectStore(containers).Should().Be(new ObjectStoreInfo { Provider = "S3", Implementation = "seaweedfs 4.48" });
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(arguments[0] switch
            {
                "inspect" => new ProcessResult(0, $$"""
                    [{
                      "Name": "/opportunity-dev-postgres-1",
                      "Image": "sha256:{{new string('e', 64)}}",
                      "Args": ["-c", "fsync=off", "-c", "ssl_key_password=secret"],
                      "Config": {
                        "Image": "postgres:17",
                        "Env": ["POSTGRES_PASSWORD=secret", "LANG=en_US.utf8"],
                        "Labels": { "com.docker.compose.project": "opportunity-dev", "com.docker.compose.service": "postgres" }
                      },
                      "HostConfig": { "NanoCpus": 4000000000, "Memory": 4294967296, "ShmSize": 268435456 }
                    }]
                    """, string.Empty),
                "image" => new ProcessResult(0, $$"""[{ "Id": "sha256:{{new string('e', 64)}}", "RepoDigests": ["postgres@{{Digest}}"] }]""", string.Empty),
                _ => new ProcessResult(1, string.Empty, "unexpected"),
            });
    }
}
