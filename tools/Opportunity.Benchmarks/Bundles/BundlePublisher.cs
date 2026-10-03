using System.Text.Json;

using Opportunity.Benchmarks.Infrastructure;

namespace Opportunity.Benchmarks.Bundles;

/// <summary>One line of <c>index.jsonl</c>: enough to find a run and check that its bundle is the one that was published.</summary>
public sealed record PublishedRun
{
    public required string RunId { get; init; }

    public required string Location { get; init; }

    public required string BundleSha256 { get; init; }

    public required string Profile { get; init; }

    public required string Tier { get; init; }

    public required string Suite { get; init; }

    public string? Candidate { get; init; }

    public required int Repetition { get; init; }

    public required string GitSha { get; init; }

    public required DateTime StartedUtc { get; init; }

    public required string GatesSha256 { get; init; }

    public required string GatesStatus { get; init; }
}

/// <summary>
/// Publishes a validated run directory, write-once, to <c>&lt;destination&gt;/&lt;profile&gt;/&lt;runId&gt;/</c> and appends
/// it to <c>&lt;destination&gt;/index.jsonl</c>. The destination is a directory: local disk, a mounted bucket, or a
/// staging directory that is then synced to object storage (docs/benchmarks/reference-environments.md §6).
/// A bundle that fails validation is rejected and nothing is copied.
/// </summary>
public static class BundlePublisher
{
    public const string IndexFile = "index.jsonl";

    public static PublishedRun Publish(string bundleDirectory, string destination, BundleValidationOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        BundleValidationReport report = BundleValidator.Validate(bundleDirectory, options);
        if (!report.IsValid)
        {
            throw new BundleRejectedException($"Rejected {bundleDirectory}: the bundle is incomplete or inconsistent.", report.Errors);
        }

        string bundlePath = Path.Combine(bundleDirectory, ResultBundle.FileName);
        ResultBundle bundle = BenchJson.Deserialize<ResultBundle>(File.ReadAllText(bundlePath));
        string profile = JsonNamingPolicy.KebabCaseLower.ConvertName(bundle.Environment.Profile.ToString());
        string relative = $"{profile}/{bundle.RunId}";
        string target = Path.Combine(destination, profile, bundle.RunId);
        string bundleSha = BenchJson.Sha256OfFile(bundlePath);

        var entry = new PublishedRun
        {
            RunId = bundle.RunId,
            Location = relative,
            BundleSha256 = bundleSha,
            Profile = profile,
            Tier = bundle.Run.Tier,
            Suite = bundle.Run.Suite,
            Candidate = bundle.Run.Candidate,
            Repetition = bundle.Run.Repetition.Index,
            GitSha = bundle.Run.GitSha,
            StartedUtc = bundle.Run.StartedUtc,
            GatesSha256 = bundle.Gates.Sha256,
            GatesStatus = JsonNamingPolicy.KebabCaseLower.ConvertName(bundle.Gates.Status.ToString()),
        };

        string publishedBundle = Path.Combine(target, ResultBundle.FileName);
        if (File.Exists(publishedBundle))
        {
            // Write-once: re-publishing the identical bundle is a no-op; anything else is a conflict.
            return BenchJson.Sha256OfFile(publishedBundle) == bundleSha
                ? entry
                : throw new BundleRejectedException($"{relative} is already published with different content; runs are immutable.");
        }

        string staging = target + ".partial-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(staging);
        foreach (string file in bundle.Files.Select(f => f.Path).Append(ResultBundle.FileName))
        {
            string to = Path.Combine(staging, file);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(Path.Combine(bundleDirectory, file), to);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(staging, target);
        File.AppendAllText(Path.Combine(destination, IndexFile), JsonSerializer.Serialize(entry, IndexOptions) + "\n");
        return entry;
    }

    private static readonly JsonSerializerOptions IndexOptions = new(BenchJson.Options) { WriteIndented = false };
}
