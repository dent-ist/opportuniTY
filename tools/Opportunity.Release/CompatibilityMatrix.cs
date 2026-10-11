using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Opportunity.Contracts.Messaging;
using Opportunity.Data.Migrations;
using Opportunity.Search.Indexing;

namespace Opportunity.Release;

/// <summary>
/// What a release can be combined with (E01-T06): the PostgreSQL schema version its migrator brings a database to, the
/// search projection generation and the mapping checksums it can read, and the message contracts it writes and reads.
/// The three are versioned independently of the product SemVer (ADR-019 §3.5, Q-43). Built from the code that ships, so
/// the matrix cannot drift from the release it describes.
/// </summary>
public sealed partial record CompatibilityMatrix(
    int Format,
    string Release,
    string? Commit,
    DatabaseCompatibility Database,
    SearchCompatibility Search,
    MessagingCompatibility Messaging,
    UpgradeCompatibility Upgrade)
{
    public const int CurrentFormat = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    /// <summary>Builds the matrix of this build for <paramref name="release"/> (SemVer without a leading <c>v</c>).</summary>
    /// <param name="previousRelease">The release this one was upgrade-tested from (N-1), if there is one.</param>
    public static CompatibilityMatrix FromCode(
        string release, string? commit, IReadOnlyDictionary<string, string> versions, string? previousRelease = null)
    {
        ArgumentNullException.ThrowIfNull(versions);
        if (!SemVer().IsMatch(release))
        {
            throw new ArgumentException($"'{release}' is not a SemVer version (X.Y.Z, no leading v).", nameof(release));
        }

        var scripts = MigrationCatalog.Scripts;
        var mappings = ProjectionMappings.Embedded;
        var registry = MessageContracts.CreateRegistry();
        var contracts = registry.Current
            .OrderBy(c => c.MessageType, StringComparer.Ordinal)
            .Select(c => new MessageContractCompatibility(
                c.MessageType,
                c.Version.ToString(),
                [.. registry.Readable
                    .Where(r => r.Wire.MessageType == c.MessageType)
                    .Select(r => r.Wire.Version.Major)
                    .Order()]))
            .ToList();

        return new CompatibilityMatrix(
            CurrentFormat,
            release,
            string.IsNullOrWhiteSpace(commit) ? null : commit,
            new DatabaseCompatibility(
                "PostgreSQL",
                Value(versions, "POSTGRES"),
                MigrationCatalog.LatestVersion,
                scripts.Count == 0 ? null : scripts[^1].Name,
                AcceptsNewerSchema: true),
            new SearchCompatibility(
                "OpenSearch",
                Value(versions, "OPENSEARCH"),
                mappings.CurrentGeneration,
                [.. mappings.Generations.Select(g => new MappingGeneration(g, mappings.Checksum(g)))]),
            new MessagingCompatibility("RabbitMQ", Value(versions, "RABBITMQ"), contracts),
            new UpgradeCompatibility(
                string.IsNullOrWhiteSpace(previousRelease) ? PreviousMinor(release) : previousRelease.TrimStart('v'),
                "Upgrade from the previous minor release (N-1) only; skip-version upgrades go through each minor in turn. " +
                "Run the migrator of the new release before starting its API and workers (docs/releasing.md)."));
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json) + "\n";

    public string ToMarkdown()
    {
        var md = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        md.AppendLine(inv, $"# opportuniTY {Release}: compatibility matrix");
        md.AppendLine();
        if (Commit is not null)
        {
            md.AppendLine(inv, $"Built from commit `{Commit}`.");
            md.AppendLine();
        }

        md.AppendLine("| Component | Version in this release | Compatibility |");
        md.AppendLine("|---|---|---|");
        md.AppendLine(inv, $"| Database schema ({Database.Engine} {Database.EngineVersion}) | {Database.SchemaVersion} (`{Database.LatestMigration}`) | The migrator upgrades any older schema of a supported upgrade path; API and workers also run against a newer schema (expand/contract) |");
        md.AppendLine(inv, $"| Search projection ({Search.Engine} {Search.EngineVersion}) | generation {Search.ProjectionGeneration} | Reads indexes of generations {string.Join(", ", Search.MappingGenerations.Select(g => g.Generation))}; a new generation is placed by an alias-based reindex |");
        foreach (var c in Messaging.Contracts)
        {
            md.AppendLine(inv, $"| Message `{c.MessageType}` ({Messaging.Broker} {Messaging.BrokerVersion}) | writes {c.Writes} | Consumers read major {string.Join(" and ", c.ReadsMajors)} |");
        }

        md.AppendLine();
        md.AppendLine("## Upgrade");
        md.AppendLine();
        md.AppendLine(inv, $"Supported upgrade source: **{Upgrade.SupportedFrom}**. {Upgrade.Policy}");
        md.AppendLine();
        md.AppendLine(
            "During a rolling upgrade, consumers of a message type accept its current major N and the previous major N-1; " +
            "producers write N. API and workers report not-ready until the schema is at least the version above.");
        md.AppendLine();
        md.AppendLine("## Search mapping checksums");
        md.AppendLine();
        md.AppendLine("| Generation | SHA-256 of `projection.v{G}.json` |");
        md.AppendLine("|---|---|");
        foreach (var g in Search.MappingGenerations)
        {
            md.AppendLine(inv, $"| {g.Generation} | `{g.Sha256}` |");
        }

        return md.ToString().ReplaceLineEndings("\n");
    }

    /// <summary>Reads <c>KEY=value</c> lines of <c>versions.env</c>; comments and blank lines are skipped.</summary>
    public static IReadOnlyDictionary<string, string> ReadVersionsEnv(string path) =>
        File.ReadLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && l.Contains('=', StringComparison.Ordinal))
            .Select(l => l.Split('=', 2))
            .GroupBy(p => p[0], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last()[1].Trim().Trim('"'), StringComparer.Ordinal);

    internal static string PreviousMinor(string release)
    {
        var parts = release.Split('-', '+')[0].Split('.').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        return (parts[0], parts[1]) switch
        {
            (0, 0) => "none",
            (_, > 0) => string.Create(CultureInfo.InvariantCulture, $"{parts[0]}.{parts[1] - 1}.x"),
            _ => string.Create(CultureInfo.InvariantCulture, $"the last {parts[0] - 1}.x release"),
        };
    }

    private static string Value(IReadOnlyDictionary<string, string> versions, string key) =>
        versions.TryGetValue(key, out var value) && value.Length > 0
            ? value
            : throw new InvalidOperationException($"versions.env does not define {key}.");

    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemVer();
}

public sealed record DatabaseCompatibility(
    string Engine, string EngineVersion, int SchemaVersion, string? LatestMigration, bool AcceptsNewerSchema);

public sealed record SearchCompatibility(
    string Engine, string EngineVersion, int ProjectionGeneration, IReadOnlyList<MappingGeneration> MappingGenerations);

public sealed record MappingGeneration(int Generation, string Sha256);

public sealed record MessagingCompatibility(string Broker, string BrokerVersion, IReadOnlyList<MessageContractCompatibility> Contracts);

/// <summary>A message type: the version producers of this release write, and the majors its consumers accept.</summary>
public sealed record MessageContractCompatibility(string MessageType, string Writes, IReadOnlyList<int> ReadsMajors);

public sealed record UpgradeCompatibility(string SupportedFrom, string Policy);
