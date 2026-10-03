using Opportunity.Benchmarks.Infrastructure;

namespace Opportunity.Benchmarks.Capture;

public sealed record PostgresTarget(string Name, string ConnectionString);

public sealed record CaptureOptions
{
    public required BenchmarkProfile Profile { get; init; }

    public HostOverrides Host { get; init; } = new();

    /// <summary>versions.env to record; located upwards from the working directory when null.</summary>
    public string? VersionsEnvPath { get; init; }

    /// <summary>Compose project whose containers are inspected (e.g. <c>opportunity-dev</c>).</summary>
    public string? ComposeProject { get; init; }

    /// <summary>Extra containers (names or ids) to inspect, e.g. Testcontainers or hand-started services.</summary>
    public IReadOnlyList<string> Containers { get; init; } = [];

    public IReadOnlyList<PostgresTarget> Postgres { get; init; } = [];

    public Uri? OpenSearch { get; init; }

    public bool IncludeSystemIndices { get; init; }

    public Uri? RabbitMqManagement { get; init; }

    /// <summary>Overrides detection from containers.</summary>
    public ObjectStoreInfo? ObjectStore { get; init; }

    /// <summary>Overrides detection from containers.</summary>
    public IReadOnlyList<WorkerPool> Workers { get; init; } = [];

    /// <summary>Required when any durability deviation is found (Q-05).</summary>
    public string? RelaxedDurabilityJustification { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Filesystem root for /proc and /sys (tests point this at a fake tree).</summary>
    public string SystemRoot { get; init; } = "/";

    public bool SkipDocker { get; init; }
}

/// <summary>Collects an <see cref="EnvironmentManifest"/> from the host, Docker, PostgreSQL, OpenSearch and RabbitMQ.</summary>
public sealed class EnvironmentCapturer(IProcessRunner? runner = null, TimeProvider? clock = null)
{
    private readonly IProcessRunner _runner = runner ?? ProcessRunner.Instance;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<EnvironmentManifest> CaptureAsync(CaptureOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        string versionsPath = options.VersionsEnvPath ?? VersionsEnvFile.Locate()
            ?? throw new FileNotFoundException("versions.env not found; pass --versions.");
        VersionsEnvFile versions = VersionsEnvFile.Load(versionsPath);
        IReadOnlyList<PinnedImage> pins = versions.PinnedImages();

        var docker = new DockerCapture(_runner);
        ContainerRuntimeInfo? runtime = options.SkipDocker ? null : await docker.CaptureRuntimeAsync(cancellationToken).ConfigureAwait(false);
        HostInfo host = new HostCapture(options.SystemRoot).Capture(options.Host, runtime);

        IReadOnlyList<ContainerInfo> containers = [];
        if (runtime is not null)
        {
            var ids = new List<string>(options.Containers);
            if (options.ComposeProject is { Length: > 0 } project)
            {
                ids.AddRange(await docker.ListComposeProjectAsync(project, cancellationToken).ConfigureAwait(false));
            }

            containers = await docker.InspectAsync([.. ids.Distinct(StringComparer.Ordinal)], pins, cancellationToken).ConfigureAwait(false);
        }
        else if (options.ComposeProject is not null || options.Containers.Count > 0)
        {
            throw new InvalidOperationException("Containers were requested but no Docker daemon is reachable.");
        }

        var postgres = new List<PostgresInstance>();
        foreach (PostgresTarget target in options.Postgres)
        {
            postgres.Add(await PostgresCapture.CaptureAsync(target.Name, target.ConnectionString, cancellationToken).ConfigureAwait(false));
        }

        OpenSearchCluster? openSearch = options.OpenSearch is null
            ? null
            : await OpenSearchCapture.CaptureAsync(options.OpenSearch, options.IncludeSystemIndices, cancellationToken: cancellationToken).ConfigureAwait(false);
        RabbitMqInfo? rabbit = options.RabbitMqManagement is null
            ? null
            : await RabbitMqCapture.CaptureAsync(options.RabbitMqManagement, cancellationToken: cancellationToken).ConfigureAwait(false);

        IReadOnlyList<WorkerPool> workers = options.Workers.Count > 0 ? options.Workers : DetectWorkers(containers);
        var manifest = new EnvironmentManifest
        {
            CapturedUtc = _clock.GetUtcNow().UtcDateTime,
            Profile = options.Profile,
            Hosts = [host],
            Software = new SoftwareInfo
            {
                VersionsEnv = versions.ToInfo(VersionsEnvFile.FileName),
                Images = pins,
                DotnetRuntime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                ApplicationImages = ApplicationImages(containers),
            },
            Containers = containers.Count == 0 ? null : containers,
            Postgres = postgres.Count == 0 ? null : postgres,
            Opensearch = openSearch,
            Rabbitmq = rabbit,
            ObjectStore = options.ObjectStore ?? DetectObjectStore(containers),
            Workers = workers.Count == 0 ? null : workers,
            Durability = new DurabilityInfo { Mode = DurabilityMode.Production, Deviations = [] },
            Notes = options.Notes.Count == 0 ? null : options.Notes,
        };

        return manifest with { Durability = DurabilityPolicy.Describe(DurabilityPolicy.Evaluate(manifest), options.RelaxedDurabilityJustification) };
    }

    /// <summary>Worker containers grouped by Compose service; the type is the <c>Workers__Enabled</c> selection when set.</summary>
    public static IReadOnlyList<WorkerPool> DetectWorkers(IReadOnlyList<ContainerInfo> containers)
    {
        ArgumentNullException.ThrowIfNull(containers);
        return [.. containers
            .Where(c => c.Service?.StartsWith("worker", StringComparison.Ordinal) == true)
            .GroupBy(c => c.Environment?.GetValueOrDefault("Workers__Enabled") ?? c.Service!, StringComparer.Ordinal)
            .Select(g => new WorkerPool { Type = g.Key, Instances = g.Count() })
            .OrderBy(w => w.Type, StringComparer.Ordinal)];
    }

    public static ObjectStoreInfo? DetectObjectStore(IReadOnlyList<ContainerInfo> containers)
    {
        ArgumentNullException.ThrowIfNull(containers);
        string? provider = containers
            .Select(c => c.Environment?.GetValueOrDefault("ObjectStorage__Provider"))
            .FirstOrDefault(p => !string.IsNullOrEmpty(p));
        ContainerInfo? seaweed = containers.FirstOrDefault(c => c.Image.Reference.Contains("seaweedfs", StringComparison.Ordinal));
        if (provider is null && seaweed is null)
        {
            return null;
        }

        provider ??= "S3";
        return new ObjectStoreInfo
        {
            Provider = provider,
            Implementation = provider switch
            {
                "S3" when seaweed is not null => "seaweedfs " + seaweed.Image.Reference.Split('@')[0].Split(':').LastOrDefault(),
                "FileSystem" => "filesystem (named volume)",
                _ => null,
            },
        };
    }

    private static string[]? ApplicationImages(IReadOnlyList<ContainerInfo> containers)
    {
        string[] images = [.. containers
            .Where(c => c.Image.Reference.Contains("opportunity-", StringComparison.Ordinal))
            .Select(c => $"{c.Image.Reference}@{c.Image.Id}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
        return images.Length == 0 ? null : images;
    }
}
