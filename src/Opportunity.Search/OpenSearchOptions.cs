using System.Text.RegularExpressions;

using Microsoft.Extensions.Configuration;

namespace Opportunity.Search;

/// <summary>
/// OpenSearch connection and index-management settings (section <c>OpenSearch</c>, endpoint from
/// <c>ConnectionStrings:OpenSearch</c>). Defaults are the Lite profile; Full raises <see cref="SharedPrimaryShards"/>
/// to 3 and <see cref="Replicas"/> to 1 (ADR-006 §2, §6).
/// </summary>
public sealed partial class OpenSearchOptions
{
    public const string SectionName = "OpenSearch";
    public const string ConnectionStringName = "OpenSearch";

    /// <summary>Hard ceiling of a single-shard shared workspace (ADR-006 R3).</summary>
    public const long MaxShardBytes = 50L * 1024 * 1024 * 1024;

    private const long GiB = 1024L * 1024 * 1024;

    /// <summary>REST endpoint, e.g. <c>http://opensearch:9200</c>.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>Basic-auth user when the security plugin is enabled (Full); never logged.</summary>
    public string? Username { get; set; }

    public string? Password { get; set; }

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Installation prefix of every index, alias and template name (ADR-006 §3).</summary>
    public string IndexPrefix { get; set; } = "opp";

    public int SharedPrimaryShards { get; set; } = 1;

    public int Replicas { get; set; }

    public string RefreshInterval { get; set; } = "1s";

    public int TotalFieldsLimit { get; set; } = 2000;

    /// <summary>Deep paging goes through PIT + search_after only (Q-32).</summary>
    public int MaxResultWindow { get; set; } = 10_000;

    /// <summary>Placement thresholds (ADR-006 §2).</summary>
    public IndexPlacementOptions Placement { get; set; } = new();

    /// <summary>Binds <see cref="SectionName"/> and, unless <see cref="Endpoint"/> is set there, the connection string.</summary>
    public static OpenSearchOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new OpenSearchOptions();
        configuration.GetSection(SectionName).Bind(options);
        if (options.Endpoint is null && configuration.GetConnectionString(ConnectionStringName) is { Length: > 0 } cs)
        {
            options.Endpoint = Uri.TryCreate(cs, UriKind.Absolute, out var uri) ? uri
                : throw new InvalidOperationException($"ConnectionStrings:{ConnectionStringName} must be an absolute http(s) URI.");
        }

        return options;
    }

    /// <summary>Start-up validation; throws <see cref="InvalidOperationException"/> naming the bad setting.</summary>
    public void Validate()
    {
        if (Endpoint is null || !Endpoint.IsAbsoluteUri || (Endpoint.Scheme != Uri.UriSchemeHttp && Endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"ConnectionStrings:{ConnectionStringName} must be an absolute http(s) URI.");
        }

        if (!PrefixPattern().IsMatch(IndexPrefix ?? string.Empty))
        {
            throw new InvalidOperationException(
                $"{SectionName}:IndexPrefix must be 1-40 lowercase letters, digits, '-' or '_' and start with a letter or digit.");
        }

        Require(RequestTimeout > TimeSpan.Zero, nameof(RequestTimeout));
        Require(SharedPrimaryShards >= 1, nameof(SharedPrimaryShards));
        Require(Replicas >= 0, nameof(Replicas));
        Require(!string.IsNullOrWhiteSpace(RefreshInterval), nameof(RefreshInterval));
        Require(TotalFieldsLimit >= 1000, nameof(TotalFieldsLimit));
        Require(MaxResultWindow >= 1, nameof(MaxResultWindow));

        var p = Placement ?? throw new InvalidOperationException($"{SectionName}:Placement is required.");
        Require(p.DedicatedDocuments >= 1, "Placement:DedicatedDocuments");

        // R3: routing_partition_size = 1, so a shared workspace lives on one shard and can never exceed one shard.
        Require(p.DedicatedBytes is > 0 and <= MaxShardBytes, "Placement:DedicatedBytes (at most 50 GB while shared routing uses one shard)");
        Require(p.MultiShardBytes > 0, "Placement:MultiShardBytes");
        Require(p.TargetShardBytes is >= 10 * GiB and <= 50 * GiB, "Placement:TargetShardBytes (10-50 GB)");
        Require(p.SharedIndexCloseAtFraction is > 0 and <= 1, "Placement:SharedIndexCloseAtFraction");
        Require(p.MaxSharedIndexes is >= 1 and <= 999, "Placement:MaxSharedIndexes");
        Require(p.SizeCalibrationFactor > 0, "Placement:SizeCalibrationFactor");
        Require(p.CacheTtl >= TimeSpan.Zero && p.CacheTtl <= TimeSpan.FromSeconds(5), "Placement:CacheTtl (at most 5 s, ADR-006 R2)");
    }

    private static void Require(bool condition, string setting)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{SectionName}:{setting} is out of range.");
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();
}

/// <summary>Placement thresholds, installation settings validated at start-up (ADR-006 §2, Q-02).</summary>
public sealed class IndexPlacementOptions
{
    private const long GiB = 1024L * 1024 * 1024;

    /// <summary>Workspaces expected to reach this many documents are placed dedicated.</summary>
    public long DedicatedDocuments { get; set; } = 5_000_000;

    /// <summary>Calibrated primary-store estimate at which a workspace is placed dedicated.</summary>
    public long DedicatedBytes { get; set; } = 50 * GiB;

    /// <summary>Projected primary store at which a dedicated index gets more than one primary.</summary>
    public long MultiShardBytes { get; set; } = 50 * GiB;

    public long TargetShardBytes { get; set; } = 30 * GiB;

    /// <summary>A shared index takes no new workspaces once its assigned bytes reach this share of its capacity.</summary>
    public double SharedIndexCloseAtFraction { get; set; } = 0.7;

    public int MaxSharedIndexes { get; set; } = 32;

    /// <summary>Multiplier from the PostgreSQL size estimate to primary-store bytes (ADR-006 R5).</summary>
    public double SizeCalibrationFactor { get; set; } = 1.3;

    /// <summary>How long a process may serve a cached placement (ADR-006 R2: at most 5 s).</summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromSeconds(5);
}
