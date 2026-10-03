using System.Diagnostics;

using Opportunity.Benchmarks.Capture;
using Opportunity.Benchmarks.Infrastructure;
using Opportunity.Testing.Images;

namespace Opportunity.Benchmarks.Tests;

public sealed record K6Result(int ExitCode, string Output, string RawPath, string SummaryPath);

/// <summary>
/// Locates the pinned k6 (versions.env K6): <c>K6_BINARY</c>, else a cached copy, else the binary extracted from the
/// pinned grafana/k6 image (<c>OPPORTUNITY_TEST_IMAGE_K6</c> overrides the image). Tests skip when none is available,
/// except on CI (<c>CI=true</c>) where a missing k6 fails them.
/// </summary>
internal static class K6Tool
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _path;

    public static string ScriptsDirectory => Path.Combine(Path.GetDirectoryName(VersionsEnvFile.Locate()!)!, "tools", "Opportunity.Benchmarks", "k6");

    public static async Task<string> RequireAsync()
    {
        string? path = await LocateAsync().ConfigureAwait(false);
        if (path is null)
        {
            string message = "k6 is not available: set K6_BINARY, or make Docker able to pull " + ContainerImages.Resolve("K6", "grafana/k6");
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(message);
            }

            Assert.Skip(message);
        }

        return path!;
    }

    public static async Task<K6Result> RunAsync(string script, IReadOnlyDictionary<string, string> environment, string outputDirectory)
    {
        string k6 = await RequireAsync().ConfigureAwait(false);
        Directory.CreateDirectory(outputDirectory);
        string raw = Path.Combine(outputDirectory, "k6-raw.json.gz");
        string summary = Path.Combine(outputDirectory, "k6-summary.json");
        var info = new ProcessStartInfo(k6)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = ScriptsDirectory,
        };
        foreach (string argument in new[] { "run", "--quiet", "--no-color", "--out", "json=" + raw, "--summary-export", summary, Path.Combine(ScriptsDirectory, script) })
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment)
        {
            info.Environment[name] = value;
        }

        using Process process = Process.Start(info)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        return new K6Result(process.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false), raw, summary);
    }

    private static async Task<string?> LocateAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_path is not null)
            {
                return _path;
            }

            if (Environment.GetEnvironmentVariable("K6_BINARY") is { Length: > 0 } configured && File.Exists(configured))
            {
                return _path = configured;
            }

            string image = ContainerImages.Resolve("K6", "grafana/k6");
            string cache = Path.Combine(Path.GetTempPath(), "opportunity-bench-k6", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(image)))[..16]);
            string binary = Path.Combine(cache, "k6");
            if (File.Exists(binary))
            {
                return _path = binary;
            }

            ProcessRunner docker = ProcessRunner.Instance;
            ProcessResult created = await docker.RunAsync("docker", ["create", image]).ConfigureAwait(false);
            if (!created.Succeeded)
            {
                return null;
            }

            string container = created.StandardOutput.Trim();
            try
            {
                Directory.CreateDirectory(cache);
                string partial = binary + ".partial";
                ProcessResult copied = await docker.RunAsync("docker", ["cp", container + ":/usr/bin/k6", partial]).ConfigureAwait(false);
                if (!copied.Succeeded)
                {
                    return null;
                }

                File.Move(partial, binary, overwrite: true);
                return _path = binary;
            }
            finally
            {
                await docker.RunAsync("docker", ["rm", container]).ConfigureAwait(false);
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}
