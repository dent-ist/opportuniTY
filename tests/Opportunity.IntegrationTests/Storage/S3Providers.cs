using System.Globalization;
using System.Text;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

using Opportunity.Testing.Images;

namespace Opportunity.IntegrationTests.Storage;

/// <summary>Where and as whom the S3 contract suite talks to a provider.</summary>
public sealed record S3Endpoint(Uri ServiceUrl, string AccessKey, string SecretKey, string Region);

/// <summary>
/// An S3-compatible store the S3 contract suite can run against (ADR-020). The default is the bundled store; set
/// <c>OPPORTUNITY_TEST_S3_PROVIDER</c> to evaluate another candidate with the same, unchanged tests:
/// <c>seaweedfs</c> (default), <c>garage</c>, <c>rustfs</c>, <c>versitygw</c>, <c>s3proxy</c>, <c>cloudserver</c>,
/// or <c>external</c> for a running endpoint such as AWS S3 (<c>OPPORTUNITY_TEST_S3_ENDPOINT</c>,
/// <c>_ACCESS_KEY</c>, <c>_SECRET_KEY</c>, <c>_REGION</c>; the bucket comes from <c>OPPORTUNITY_TEST_S3_BUCKET</c>).
/// Candidate images can be replaced with <c>OPPORTUNITY_TEST_IMAGE_&lt;KEY&gt;</c> like every fixture image.
/// </summary>
public abstract class S3Provider : IAsyncDisposable
{
    public const string ProviderVariable = "OPPORTUNITY_TEST_S3_PROVIDER";
    public const string VariablePrefix = "OPPORTUNITY_TEST_S3_";

    protected const string AccessKey = "opportunity";
    protected const string SecretKey = "opportunity-secret";
    protected const string DefaultRegion = "us-east-1";

    public abstract string Name { get; }

    /// <summary>Bucket for the run; external endpoints usually need a pre-created one.</summary>
    public virtual string Bucket => "contract";

    public static S3Provider FromEnvironment()
    {
        var name = Environment.GetEnvironmentVariable(ProviderVariable);
        return (string.IsNullOrWhiteSpace(name) ? "seaweedfs" : name.Trim().ToLowerInvariant()) switch
        {
            "seaweedfs" => new SeaweedFsProvider(),
            "garage" => new GarageProvider(),
            "rustfs" => new RustFsProvider(),
            "versitygw" => new VersityGatewayProvider(),
            "s3proxy" => new S3ProxyProvider(),
            "cloudserver" => new CloudServerProvider(),
            "external" => new ExternalProvider(),
            var other => throw new InvalidOperationException($"Unknown {ProviderVariable} '{other}'."),
        };
    }

    public abstract Task<S3Endpoint> StartAsync(CancellationToken cancellationToken);

    public abstract ValueTask DisposeAsync();

    /// <summary>Image for a candidate that is not pinned in <c>versions.env</c>: the evaluated tag and digest.</summary>
    protected static string CandidateImage(string key, string reference)
    {
        var overridden = Environment.GetEnvironmentVariable(ContainerImages.OverridePrefix + key);
        return string.IsNullOrWhiteSpace(overridden) ? reference : overridden.Trim();
    }
}

/// <summary>A provider running in a throwaway container.</summary>
public abstract class ContainerS3Provider : S3Provider
{
    private IContainer? _container;

    protected abstract int Port { get; }

    protected abstract ContainerBuilder Configure(ContainerBuilder builder);

    protected abstract string Image { get; }

    /// <summary>The default probe runs a shell in the container; distroless images override it.</summary>
    protected virtual IWaitForContainerOS WaitStrategy => Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(Port);

    /// <summary>Credentials/keys to create after the container is up.</summary>
    protected virtual Task<(string AccessKey, string SecretKey)> BootstrapAsync(IContainer container, CancellationToken cancellationToken) =>
        Task.FromResult((AccessKey, SecretKey));

    public override async Task<S3Endpoint> StartAsync(CancellationToken cancellationToken)
    {
        _container = Configure(new ContainerBuilder(Image).WithPortBinding(Port, assignRandomHostPort: true))
            .WithWaitStrategy(WaitStrategy)
            .Build();
        await _container.StartAsync(cancellationToken);
        var (access, secret) = await BootstrapAsync(_container, cancellationToken);
        return new S3Endpoint(new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(Port)}/"), access, secret, DefaultRegion);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    protected static async Task<string> ExecAsync(IContainer container, CancellationToken cancellationToken, params string[] command)
    {
        var result = await container.ExecAsync(command, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"'{string.Join(' ', command)}' exited {result.ExitCode}: {result.Stderr}{result.Stdout}");
        }

        return result.Stdout;
    }
}

/// <summary>SeaweedFS (Apache-2.0): the bundled S3 store, pinned in <c>versions.env</c>. SigV4 is enforced.</summary>
public sealed class SeaweedFsProvider : ContainerS3Provider
{
    private const string IdentitiesJson = $$"""
        {"identities":[{"name":"opportunity","credentials":[{"accessKey":"{{AccessKey}}","secretKey":"{{SecretKey}}"}],
          "actions":["Admin","Read","List","Tagging","Write"]}]}
        """;

    public override string Name => "SeaweedFS";

    protected override int Port => 8333;

    protected override string Image => StorageImages.ObjectStore;

    protected override ContainerBuilder Configure(ContainerBuilder builder) => builder
        .WithResourceMapping(Encoding.UTF8.GetBytes(IdentitiesJson), "/etc/seaweedfs/s3.json")
        .WithCommand("server", "-dir=/data", "-ip.bind=0.0.0.0", "-volume.max=0", "-s3", "-s3.config=/etc/seaweedfs/s3.json");
}

/// <summary>Garage (AGPL-3.0), single node, replication factor 1.</summary>
public sealed class GarageProvider : ContainerS3Provider
{
    // Garage key IDs are "GK" + 24 hex digits and secrets 64 hex digits.
    private const string KeyId = "GK0123456789abcdef01234567";
    private const string KeySecret = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string Config = """
        metadata_dir = "/var/lib/garage/meta"
        data_dir = "/var/lib/garage/data"
        db_engine = "sqlite"
        replication_factor = 1
        rpc_bind_addr = "0.0.0.0:3901"
        rpc_public_addr = "127.0.0.1:3901"
        rpc_secret = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210"

        [s3_api]
        s3_region = "us-east-1"
        api_bind_addr = "0.0.0.0:3900"
        root_domain = ".s3.garage.localhost"
        """;

    public override string Name => "Garage";

    protected override int Port => 3900;

    protected override string Image =>
        CandidateImage("GARAGE", "dxflrs/garage:v2.4.1@sha256:9c96caa2612d3411acc5b0e6701fb238dbfba33e533a6d7d3d811a4b12d0d020");

    protected override IWaitForContainerOS WaitStrategy => Wait.ForUnixContainer().UntilMessageIsLogged("S3 API server listening");

    protected override ContainerBuilder Configure(ContainerBuilder builder) =>
        builder.WithResourceMapping(Encoding.UTF8.GetBytes(Config), "/etc/garage.toml");

    protected override async Task<(string AccessKey, string SecretKey)> BootstrapAsync(IContainer container, CancellationToken cancellationToken)
    {
        var nodeId = (await ExecAsync(container, cancellationToken, "/garage", "node", "id", "-q")).Trim().Split('@')[0];
        await ExecAsync(container, cancellationToken, "/garage", "layout", "assign", "-z", "dc1", "-c", "1G", nodeId);
        await ExecAsync(container, cancellationToken, "/garage", "layout", "apply", "--version", "1");
        await ExecAsync(container, cancellationToken, "/garage", "key", "import", "--yes", "-n", "opportunity", KeyId, KeySecret);
        await ExecAsync(container, cancellationToken, "/garage", "key", "allow", "--create-bucket", KeyId);
        return (KeyId, KeySecret);
    }
}

/// <summary>RustFS (Apache-2.0), single node single disk.</summary>
public sealed class RustFsProvider : ContainerS3Provider
{
    public override string Name => "RustFS";

    protected override int Port => 9000;

    protected override string Image =>
        CandidateImage("RUSTFS", "rustfs/rustfs:1.0.0@sha256:8cc9801755448b71a786705ce76692c77e14936cccd87cf2fc31842e58f4d1ff");

    protected override ContainerBuilder Configure(ContainerBuilder builder) => builder
        .WithEnvironment("RUSTFS_ACCESS_KEY", AccessKey)
        .WithEnvironment("RUSTFS_SECRET_KEY", SecretKey);
}

/// <summary>Versity S3 Gateway (Apache-2.0) over its POSIX backend.</summary>
public sealed class VersityGatewayProvider : ContainerS3Provider
{
    public override string Name => "Versity S3 Gateway";

    protected override int Port => 7070;

    protected override string Image =>
        CandidateImage("VERSITYGW", "versity/versitygw:v1.8.0@sha256:30292fc2eeacc67a36993b01f7a7a5e3361a19cced0e80c1d71cfa2a4b0a2499");

    protected override ContainerBuilder Configure(ContainerBuilder builder) => builder
        .WithCommand("--access", AccessKey, "--secret", SecretKey, "--port", ":7070", "posix", "/tmp");
}

/// <summary>S3Proxy (Apache-2.0) over the jclouds filesystem blob store.</summary>
public sealed class S3ProxyProvider : ContainerS3Provider
{
    public override string Name => "S3Proxy";

    protected override int Port => 80;

    protected override string Image => CandidateImage("S3PROXY", "andrewgaul/s3proxy:4.1.1@sha256:87662b2a5afcdfa5f478a1c61650bae4c87bbd15f6ff2823236bcfd66ef7fe04");

    protected override ContainerBuilder Configure(ContainerBuilder builder) => builder
        .WithEnvironment("S3PROXY_AUTHORIZATION", "aws-v4")
        .WithEnvironment("S3PROXY_IDENTITY", AccessKey)
        .WithEnvironment("S3PROXY_CREDENTIAL", SecretKey);
}

/// <summary>Zenko CloudServer (Apache-2.0). Docker Hub only carries the 2023 image; current builds are on ghcr.io.</summary>
public sealed class CloudServerProvider : ContainerS3Provider
{
    public override string Name => "Zenko CloudServer";

    protected override int Port => 8000;

    protected override string Image => CandidateImage("CLOUDSERVER", "zenko/cloudserver:latest");

    protected override ContainerBuilder Configure(ContainerBuilder builder) => builder
        .WithEnvironment("REMOTE_MANAGEMENT_DISABLE", "1")
        .WithEnvironment("S3BACKEND", "file")
        .WithEnvironment("SCALITY_ACCESS_KEY_ID", AccessKey)
        .WithEnvironment("SCALITY_SECRET_ACCESS_KEY", SecretKey);
}

/// <summary>A running endpoint (AWS S3, a staging cluster). Nothing is started or torn down.</summary>
public sealed class ExternalProvider : S3Provider
{
    public override string Name => "External S3 endpoint";

    public override string Bucket => Required("BUCKET");

    public override Task<S3Endpoint> StartAsync(CancellationToken cancellationToken)
    {
        var region = Environment.GetEnvironmentVariable(VariablePrefix + "REGION");
        return Task.FromResult(new S3Endpoint(
            new Uri(Required("ENDPOINT")),
            Required("ACCESS_KEY"),
            Required("SECRET_KEY"),
            string.IsNullOrWhiteSpace(region) ? DefaultRegion : region));
    }

    public override ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(VariablePrefix + name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"{VariablePrefix}{name} must be set for the external provider."));
}
