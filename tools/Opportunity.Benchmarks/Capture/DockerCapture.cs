using System.Text.Json;
using System.Text.RegularExpressions;

using Opportunity.Benchmarks.Infrastructure;

namespace Opportunity.Benchmarks.Capture;

/// <summary>Container runtime facts and per-container image/limits via the <c>docker</c> CLI (honours DOCKER_HOST and contexts).</summary>
public sealed partial class DockerCapture(IProcessRunner runner, string docker = "docker")
{
    public const string ComposeProjectLabel = "com.docker.compose.project";
    public const string ComposeServiceLabel = "com.docker.compose.service";

    /// <summary>Returns null when no Docker daemon is reachable.</summary>
    public async Task<ContainerRuntimeInfo?> CaptureRuntimeAsync(CancellationToken cancellationToken = default)
    {
        ProcessResult info = await runner.RunAsync(docker, ["info", "--format", "{{json .}}"], cancellationToken).ConfigureAwait(false);
        if (!info.Succeeded || string.IsNullOrWhiteSpace(info.StandardOutput))
        {
            return null;
        }

        using JsonDocument doc = JsonDocument.Parse(info.StandardOutput);
        JsonElement root = doc.RootElement;
        ProcessResult compose = await runner.RunAsync(docker, ["compose", "version", "--short"], cancellationToken).ConfigureAwait(false);
        return new ContainerRuntimeInfo
        {
            Name = "docker",
            Version = Str(root, "ServerVersion"),
            ComposeVersion = compose.Succeeded ? compose.StandardOutput.Trim().TrimStart('v') : null,
            StorageDriver = Str(root, "Driver"),
            CgroupVersion = Str(root, "CgroupVersion"),
            CgroupDriver = Str(root, "CgroupDriver"),
            RootDir = Str(root, "DockerRootDir"),
            Cpus = root.TryGetProperty("NCPU", out JsonElement cpus) && cpus.ValueKind == JsonValueKind.Number ? cpus.GetInt32() : null,
            MemoryBytes = root.TryGetProperty("MemTotal", out JsonElement mem) && mem.ValueKind == JsonValueKind.Number ? mem.GetInt64() : null,
        };
    }

    /// <summary>Container ids of every container of a Compose project (running or not).</summary>
    public async Task<IReadOnlyList<string>> ListComposeProjectAsync(string project, CancellationToken cancellationToken = default)
    {
        ProcessResult result = await runner.RunAsync(
            docker, ["ps", "--all", "--no-trunc", "--filter", $"label={ComposeProjectLabel}={project}", "--format", "{{.ID}}"], cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? [.. result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : throw new InvalidOperationException($"docker ps failed: {result.StandardError.Trim()}");
    }

    /// <summary>Inspects containers (ids or names) and their images; pins are checked against versions.env.</summary>
    public async Task<IReadOnlyList<ContainerInfo>> InspectAsync(IReadOnlyCollection<string> containers, IReadOnlyList<PinnedImage> pins, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(containers);
        if (containers.Count == 0)
        {
            return [];
        }

        ProcessResult result = await runner.RunAsync(docker, ["inspect", "--type", "container", .. containers], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"docker inspect failed: {result.StandardError.Trim()}");
        }

        using JsonDocument doc = JsonDocument.Parse(result.StandardOutput);
        var parsed = doc.RootElement.EnumerateArray().Select(ParseContainer).ToList();
        IReadOnlyDictionary<string, IReadOnlyList<string>> repoDigests = await RepoDigestsAsync(
            [.. parsed.Select(c => c.Image.Id).Distinct(StringComparer.Ordinal)], cancellationToken).ConfigureAwait(false);

        return [.. parsed
            .Select(c =>
            {
                IReadOnlyList<string>? digests = repoDigests.TryGetValue(c.Image.Id, out IReadOnlyList<string>? d) && d.Count > 0 ? d : null;
                return c with
                {
                    Image = c.Image with { RepoDigests = digests },
                    Pin = MatchPin(c.Image.Reference, digests, pins),
                };
            })
            .OrderBy(c => c.Service ?? c.Name, StringComparer.Ordinal)
            .ThenBy(c => c.Name, StringComparer.Ordinal)];
    }

    /// <summary>Which versions.env pin an image reference belongs to, and whether the running image is that digest.</summary>
    public static ImagePin? MatchPin(string reference, IReadOnlyList<string>? repoDigests, IReadOnlyList<PinnedImage> pins)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(pins);
        string repository = NormalizeRepository(reference.Split('@')[0]);
        int colon = repository.LastIndexOf(':');
        if (colon > repository.LastIndexOf('/'))
        {
            repository = repository[..colon];
        }

        PinnedImage? pin = pins.FirstOrDefault(p => NormalizeRepository(p.Repository) == repository);
        if (pin?.Digest is null)
        {
            return null;
        }

        bool matches = reference.EndsWith("@" + pin.Digest, StringComparison.Ordinal)
            || (repoDigests?.Any(d => d.EndsWith("@" + pin.Digest, StringComparison.Ordinal)) ?? false);
        return new ImagePin(pin.Key, pin.Digest, matches);
    }

    /// <summary>
    /// Environment variables safe to record: JVM options and node/cluster settings, worker selection and object-storage
    /// provider. Anything that looks like a credential is dropped even when its prefix is allowed.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? FilterEnvironment(IEnumerable<string> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var kept = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string variable in variables)
        {
            int eq = variable.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            string name = variable[..eq];
            if (AllowedVariable().IsMatch(name) && !SecretVariable().IsMatch(name))
            {
                kept[name] = variable[(eq + 1)..];
            }
        }

        return kept.Count == 0 ? null : kept;
    }

    private static ContainerInfo ParseContainer(JsonElement c)
    {
        JsonElement config = c.GetProperty("Config");
        JsonElement host = c.GetProperty("HostConfig");
        Dictionary<string, string> labels = config.TryGetProperty("Labels", out JsonElement l) && l.ValueKind == JsonValueKind.Object
            ? l.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal)
            : [];
        string[] env = config.TryGetProperty("Env", out JsonElement e) && e.ValueKind == JsonValueKind.Array
            ? [.. e.EnumerateArray().Select(v => v.GetString() ?? string.Empty)]
            : [];
        string[] args = c.TryGetProperty("Args", out JsonElement a) && a.ValueKind == JsonValueKind.Array
            ? [.. a.EnumerateArray().Select(v => v.GetString() ?? string.Empty)]
            : [];
        string imageId = c.GetProperty("Image").GetString() ?? string.Empty;
        return new ContainerInfo
        {
            Name = (c.GetProperty("Name").GetString() ?? string.Empty).TrimStart('/'),
            Service = labels.GetValueOrDefault(ComposeServiceLabel),
            ComposeProject = labels.GetValueOrDefault(ComposeProjectLabel),
            Image = new ContainerImage { Reference = config.GetProperty("Image").GetString() ?? string.Empty, Id = imageId },
            Command = args.Length == 0 ? null : RedactArguments(args),
            Environment = FilterEnvironment(env),
            NanoCpus = PositiveLong(host, "NanoCpus"),
            MemoryLimitBytes = PositiveLong(host, "Memory"),
            ShmSizeBytes = PositiveLong(host, "ShmSize"),
        };
    }

    private static string[] RedactArguments(string[] args) =>
        [.. args.Select(arg => SecretArgument().IsMatch(arg) ? SecretArgument().Replace(arg, "$1=<redacted>") : arg)];

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> RepoDigestsAsync(IReadOnlyList<string> imageIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (imageIds.Count == 0)
        {
            return result;
        }

        ProcessResult inspect = await runner.RunAsync(docker, ["image", "inspect", .. imageIds], cancellationToken).ConfigureAwait(false);
        if (!inspect.Succeeded)
        {
            return result;
        }

        using JsonDocument doc = JsonDocument.Parse(inspect.StandardOutput);
        foreach (JsonElement image in doc.RootElement.EnumerateArray())
        {
            string id = image.GetProperty("Id").GetString() ?? string.Empty;
            result[id] = image.TryGetProperty("RepoDigests", out JsonElement digests) && digests.ValueKind == JsonValueKind.Array
                ? [.. digests.EnumerateArray().Select(d => d.GetString() ?? string.Empty).Order(StringComparer.Ordinal)]
                : [];
        }

        return result;
    }

    private static string NormalizeRepository(string repository)
    {
        string r = repository;
        foreach (string prefix in new[] { "docker.io/library/", "docker.io/", "index.docker.io/library/", "index.docker.io/" })
        {
            if (r.StartsWith(prefix, StringComparison.Ordinal))
            {
                r = r[prefix.Length..];
                break;
            }
        }

        return r.StartsWith("library/", StringComparison.Ordinal) ? r["library/".Length..] : r;
    }

    private static long? PositiveLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.GetInt64() > 0 ? value.GetInt64() : null;

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"^(OPENSEARCH_JAVA_OPTS|ES_JAVA_OPTS|JAVA_OPTS|discovery\..+|cluster\..+|node\..+|bootstrap\..+|indices\..+|thread_pool\..+|search\..+|PGDATA|POSTGRES_INITDB_ARGS|RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS|RABBITMQ_VM_MEMORY_HIGH_WATERMARK|Workers__.+|ObjectStorage__Provider|DOTNET_gc.+|DOTNET_GC.+|DOTNET_TieredPGO|DOTNET_ENVIRONMENT)$", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedVariable();

    [GeneratedRegex("(PASSWORD|PASSWD|SECRET|TOKEN|CREDENTIAL|ACCESS_?KEY|PRIVATE)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretVariable();

    [GeneratedRegex("(?i)^(-?-?[a-z0-9_.]*(?:password|secret|token|key)[a-z0-9_.]*)=.*$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretArgument();
}
