using System.Globalization;
using System.Text;
using System.Text.Json;

using Opportunity.Benchmarks.Infrastructure;

namespace Opportunity.Benchmarks.Bundles;

/// <summary>Builds the bundle's references to the corpus, workload and source revision.</summary>
public static class References
{
    public const string CorpusManifestFile = "corpus-manifest.json";

    /// <summary>Reads the generator's corpus-manifest.json (E17-T01): seed, profile hash, generator identity, document count.</summary>
    public static CorpusReference Corpus(string manifestPath, CorpusLoadPath loadPath, string? bundledAs = CorpusManifestFile)
    {
        byte[] bytes = File.ReadAllBytes(manifestPath);
        using JsonDocument doc = JsonDocument.Parse(bytes);
        JsonElement root = doc.RootElement;
        JsonElement generator = root.GetProperty("generator");
        return new CorpusReference
        {
            ManifestFile = bundledAs,
            ManifestSha256 = BenchJson.Sha256Hex(bytes),
            Generator = new GeneratorIdentity(generator.GetProperty("name").GetString()!, generator.GetProperty("version").GetString()!),
            Seed = root.GetProperty("seed").GetUInt64(),
            ProfileName = root.GetProperty("profileName").GetString()!,
            ProfileHash = root.GetProperty("profileHash").GetString()!,
            DocumentCount = root.GetProperty("counts").GetProperty("documents").GetInt64(),
            LoadPath = loadPath,
        };
    }

    /// <summary>SHA-256 over the sorted lines <c>&lt;sha256&gt;  &lt;path&gt;</c> (sha256sum format) of the scripts.</summary>
    public static string WorkloadSha256(IEnumerable<WorkloadScript> scripts)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        var text = new StringBuilder();
        foreach (WorkloadScript script in scripts.OrderBy(s => s.Path, StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture, $"{script.Sha256}  {script.Path}\n");
        }

        return BenchJson.Sha256Hex(Encoding.UTF8.GetBytes(text.ToString()));
    }

    public sealed record GitState(string Sha, bool Dirty, string? Ref);

    /// <summary>HEAD, dirty flag and branch of the repository containing <paramref name="directory"/>.</summary>
    public static async Task<GitState> GitAsync(string directory, IProcessRunner? runner = null, CancellationToken cancellationToken = default)
    {
        runner ??= ProcessRunner.Instance;
        ProcessResult head = await runner.RunAsync("git", ["-C", directory, "rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
        if (!head.Succeeded)
        {
            throw new InvalidOperationException($"git rev-parse failed in {directory}: {head.StandardError.Trim()}");
        }

        ProcessResult status = await runner.RunAsync("git", ["-C", directory, "status", "--porcelain", "--untracked-files=no"], cancellationToken).ConfigureAwait(false);
        ProcessResult branch = await runner.RunAsync("git", ["-C", directory, "rev-parse", "--abbrev-ref", "HEAD"], cancellationToken).ConfigureAwait(false);
        return new GitState(
            head.StandardOutput.Trim(),
            status.Succeeded && status.StandardOutput.Trim().Length > 0,
            branch.Succeeded ? branch.StandardOutput.Trim() : null);
    }

    /// <summary>Run ids sort by start time: <c>20261003t120000z-adr004-degradation-simple-candidate-a-r1</c>.</summary>
    public static string RunId(DateTime startedUtc, string suite, string? candidate, int repetition)
    {
        ArgumentNullException.ThrowIfNull(suite);
        string slug = Slug($"{suite}-{candidate}");
        string id = string.Create(CultureInfo.InvariantCulture, $"{startedUtc.ToUniversalTime():yyyyMMdd't'HHmmss'z'}-{slug}-r{repetition}");
        return id.Length <= 128 ? id : id[..128];
    }

    private static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        }

        string slug = builder.ToString();
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return slug.Trim('-');
    }
}
