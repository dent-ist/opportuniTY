using System.Text.Json.Serialization;

namespace Opportunity.Benchmarks.Capture;

// Mirrors schema/environment-manifest.v1.schema.json. Property names serialize camelCase; enums kebab-case.

public enum BenchmarkProfile
{
    DeveloperRegression,
    EnterpriseReference,
}

public enum DiskType
{
    Nvme,
    Ssd,
    Hdd,
    Virtual,
    Unknown,
}

public enum PlatformKind
{
    BareMetal,
    Vm,
    Cloud,
    Unknown,
}

public enum PostgresRole
{
    Primary,
    Replica,
}

public enum DurabilityMode
{
    Production,
    Relaxed,
}

public sealed record ToolIdentity(string Name, string Version);

public sealed record EnvironmentManifest
{
    public string SchemaVersion { get; init; } = HarnessInfo.SchemaVersion;

    public required DateTime CapturedUtc { get; init; }

    public ToolIdentity CapturedBy { get; init; } = new(HarnessInfo.Name, HarnessInfo.Version);

    public required BenchmarkProfile Profile { get; init; }

    public required IReadOnlyList<HostInfo> Hosts { get; init; }

    public required SoftwareInfo Software { get; init; }

    public IReadOnlyList<ContainerInfo>? Containers { get; init; }

    public IReadOnlyList<PostgresInstance>? Postgres { get; init; }

    public OpenSearchCluster? Opensearch { get; init; }

    public RabbitMqInfo? Rabbitmq { get; init; }

    public ObjectStoreInfo? ObjectStore { get; init; }

    public IReadOnlyList<WorkerPool>? Workers { get; init; }

    public required DurabilityInfo Durability { get; init; }

    public IReadOnlyList<string>? Notes { get; init; }
}

public sealed record HostInfo
{
    public required string Role { get; init; }

    public string? Hostname { get; init; }

    public required CpuInfo Cpu { get; init; }

    public required MemoryInfo Memory { get; init; }

    public required IReadOnlyList<DiskInfo> Disks { get; init; }

    public required OsInfo Os { get; init; }

    public ContainerRuntimeInfo? ContainerRuntime { get; init; }

    public required PlatformInfo Platform { get; init; }
}

public sealed record CpuInfo
{
    public required string Model { get; init; }

    public string? Vendor { get; init; }

    public required string Architecture { get; init; }

    public required int LogicalCores { get; init; }

    public required int PhysicalCores { get; init; }

    public required int Sockets { get; init; }

    public double? MaxMhz { get; init; }

    public bool? Hypervisor { get; init; }

    public string? ScalingGovernor { get; init; }

    public IReadOnlyList<string>? Flags { get; init; }
}

public sealed record MemoryInfo
{
    public required long TotalBytes { get; init; }

    public long? SwapTotalBytes { get; init; }

    public string? TransparentHugePages { get; init; }
}

public sealed record DiskInfo
{
    public required string Name { get; init; }

    public string? Model { get; init; }

    public required DiskType Type { get; init; }

    public bool? Rotational { get; init; }

    public required long SizeBytes { get; init; }

    public string? Scheduler { get; init; }

    public IReadOnlyList<string>? Backs { get; init; }
}

public sealed record OsInfo
{
    public required string Kernel { get; init; }

    public string? Distribution { get; init; }

    public long? MaxMapCount { get; init; }

    public int? Swappiness { get; init; }
}

public sealed record ContainerRuntimeInfo
{
    public required string Name { get; init; }

    public string? Version { get; init; }

    public string? ComposeVersion { get; init; }

    public string? StorageDriver { get; init; }

    public string? CgroupVersion { get; init; }

    public string? CgroupDriver { get; init; }

    public string? RootDir { get; init; }

    public int? Cpus { get; init; }

    public long? MemoryBytes { get; init; }
}

public sealed record PlatformInfo
{
    public required PlatformKind Kind { get; init; }

    public string? Vendor { get; init; }

    public string? Product { get; init; }

    public string? CloudProvider { get; init; }

    public string? InstanceType { get; init; }

    public string? Region { get; init; }
}

public sealed record SoftwareInfo
{
    public required VersionsEnvInfo VersionsEnv { get; init; }

    public required IReadOnlyList<PinnedImage> Images { get; init; }

    public string? DotnetRuntime { get; init; }

    public IReadOnlyList<string>? ApplicationImages { get; init; }
}

public sealed record VersionsEnvInfo
{
    public string? Path { get; init; }

    public required string Sha256 { get; init; }

    public required IReadOnlyDictionary<string, string> Values { get; init; }
}

public sealed record PinnedImage
{
    public required string Key { get; init; }

    public required string Repository { get; init; }

    public required string Tag { get; init; }

    public string? Digest { get; init; }

    [JsonIgnore]
    public string Reference => Digest is null ? $"{Repository}:{Tag}" : $"{Repository}:{Tag}@{Digest}";
}

public sealed record ContainerInfo
{
    public required string Name { get; init; }

    public string? Service { get; init; }

    public string? ComposeProject { get; init; }

    public required ContainerImage Image { get; init; }

    public ImagePin? Pin { get; init; }

    public IReadOnlyList<string>? Command { get; init; }

    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    public long? NanoCpus { get; init; }

    public long? MemoryLimitBytes { get; init; }

    public long? ShmSizeBytes { get; init; }
}

public sealed record ContainerImage
{
    public required string Reference { get; init; }

    public required string Id { get; init; }

    public IReadOnlyList<string>? RepoDigests { get; init; }
}

public sealed record ImagePin(string Key, string ExpectedDigest, bool Matches);

public sealed record PgSetting
{
    public required string Value { get; init; }

    public string? Unit { get; init; }

    public string? Source { get; init; }
}

public sealed record PostgresInstance
{
    public required string Name { get; init; }

    public string? Endpoint { get; init; }

    public required string ServerVersion { get; init; }

    public required PostgresRole Role { get; init; }

    public required IReadOnlyDictionary<string, PgSetting> Settings { get; init; }

    public required PostgresDurability Durability { get; init; }

    public IReadOnlyList<PostgresReplica>? Replication { get; init; }
}

public sealed record PostgresDurability
{
    public required string Fsync { get; init; }

    public required string SynchronousCommit { get; init; }

    public required string FullPageWrites { get; init; }

    public required string WalLevel { get; init; }

    public required string WalSyncMethod { get; init; }

    public required string SynchronousStandbyNames { get; init; }

    public string? DataChecksums { get; init; }
}

public sealed record PostgresReplica(string ApplicationName, string State, string SyncState);

public sealed record OpenSearchCluster
{
    public string? Endpoint { get; init; }

    public required string ClusterName { get; init; }

    public required string Version { get; init; }

    public string? Distribution { get; init; }

    public required IReadOnlyList<OpenSearchNode> Nodes { get; init; }

    public required OpenSearchClusterSettings ClusterSettings { get; init; }

    public required IReadOnlyList<OpenSearchIndex> Indices { get; init; }
}

public sealed record OpenSearchNode
{
    public required string Name { get; init; }

    public required IReadOnlyList<string> Roles { get; init; }

    public required JvmInfo Jvm { get; init; }

    public int? AvailableProcessors { get; init; }

    public int? AllocatedProcessors { get; init; }
}

public sealed record JvmInfo
{
    public required string Version { get; init; }

    public string? VmName { get; init; }

    public string? VmVendor { get; init; }

    public long? HeapInitBytes { get; init; }

    public required long HeapMaxBytes { get; init; }

    public required IReadOnlyList<string> InputArguments { get; init; }

    public IReadOnlyList<string>? GcCollectors { get; init; }
}

public sealed record OpenSearchClusterSettings
{
    public required IReadOnlyDictionary<string, string> Persistent { get; init; }

    public required IReadOnlyDictionary<string, string> Transient { get; init; }

    public IReadOnlyDictionary<string, string>? Defaults { get; init; }
}

public sealed record OpenSearchIndex
{
    public required string Name { get; init; }

    public required int PrimaryShards { get; init; }

    public required int Replicas { get; init; }

    public required string RefreshInterval { get; init; }

    public required string TranslogDurability { get; init; }

    public string? TranslogSyncInterval { get; init; }

    public string? Codec { get; init; }

    public long? DocsCount { get; init; }

    public long? StoreBytes { get; init; }

    public IReadOnlyList<OpenSearchShard>? Shards { get; init; }
}

public sealed record OpenSearchShard(int Shard, bool Primary, string State, string? Node);

public sealed record RabbitMqInfo
{
    public required string Version { get; init; }

    public string? ErlangVersion { get; init; }

    public string? ClusterName { get; init; }

    public IReadOnlyList<string>? Nodes { get; init; }

    public IReadOnlyList<RabbitMqQueueInfo>? Queues { get; init; }
}

public sealed record RabbitMqQueueInfo(string Name, string Vhost, string Type, bool Durable);

public sealed record ObjectStoreInfo
{
    /// <summary>FileSystem, S3 or AzureBlob (the <c>ObjectStorage__Provider</c> values).</summary>
    public required string Provider { get; init; }

    public string? Implementation { get; init; }

    public string? Endpoint { get; init; }
}

public sealed record WorkerPool
{
    public required string Type { get; init; }

    public required int Instances { get; init; }

    public int? Concurrency { get; init; }
}

public sealed record DurabilityInfo
{
    public required DurabilityMode Mode { get; init; }

    public required IReadOnlyList<DurabilityDeviation> Deviations { get; init; }

    public string? Justification { get; init; }
}

public sealed record DurabilityDeviation(string Component, string Instance, string Setting, string Actual, string Expected);
