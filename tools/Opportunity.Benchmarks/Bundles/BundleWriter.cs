using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Opportunity.Benchmarks.Infrastructure;

namespace Opportunity.Benchmarks.Bundles;

/// <summary>A bundle (or manifest) that does not satisfy the schema or the bundle rules.</summary>
public sealed class BundleRejectedException : Exception
{
    public BundleRejectedException()
    {
        Errors = [];
    }

    public BundleRejectedException(string message)
        : base(message)
    {
        Errors = [message];
    }

    public BundleRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Errors = [message];
    }

    public BundleRejectedException(string message, IReadOnlyList<string> errors)
        : base(message + Environment.NewLine + string.Join(Environment.NewLine, errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}

/// <summary>
/// Assembles one run directory: copies the referenced files (corpus manifest, gates.yaml, workload scripts, metric
/// series) with their hashes, then writes bundle.json and refuses to write one that fails validation.
/// </summary>
public sealed class BundleWriter
{
    private readonly SortedDictionary<string, BundleFile> _files = new(StringComparer.Ordinal);

    public BundleWriter(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = System.IO.Path.GetFullPath(directory);
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }

    public IReadOnlyCollection<BundleFile> Files => _files.Values;

    public BundleFile AddFile(string sourcePath, string relativePath, string? mediaType = null, string? description = null)
    {
        string target = Target(relativePath);
        if (!string.Equals(System.IO.Path.GetFullPath(sourcePath), target, StringComparison.Ordinal))
        {
            File.Copy(sourcePath, target, overwrite: true);
        }

        return Record(relativePath, target, mediaType, description);
    }

    public BundleFile AddText(string relativePath, string content, string? mediaType = null, string? description = null)
    {
        string target = Target(relativePath);
        File.WriteAllText(target, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return Record(relativePath, target, mediaType, description);
    }

    /// <summary>Writes bundle.json with the recorded files; validates first (schema + bundle rules) and throws when invalid.</summary>
    public string Write(ResultBundle bundle, BundleValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ResultBundle complete = bundle with { Files = [.. _files.Values] };
        JsonNode node = JsonSerializer.SerializeToNode(complete, BenchJson.Options)!;
        BundleValidationReport report = BundleValidator.Validate(node, Directory, options ?? new BundleValidationOptions());
        if (!report.IsValid)
        {
            throw new BundleRejectedException($"Refusing to write an invalid bundle to {Directory}:", report.Errors);
        }

        string path = System.IO.Path.Combine(Directory, ResultBundle.FileName);
        File.WriteAllText(path, BenchJson.Serialize(complete), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private BundleFile Record(string relativePath, string target, string? mediaType, string? description)
    {
        var file = new BundleFile
        {
            Path = relativePath,
            Sha256 = BenchJson.Sha256OfFile(target),
            Bytes = new FileInfo(target).Length,
            MediaType = mediaType ?? MediaTypeOf(relativePath),
            Description = description,
        };
        _files[relativePath] = file;
        return file;
    }

    private string Target(string relativePath)
    {
        if (!BundleValidator.IsSafeRelativePath(relativePath) || relativePath == ResultBundle.FileName)
        {
            throw new ArgumentException($"'{relativePath}' is not a valid bundle file path.", nameof(relativePath));
        }

        string target = System.IO.Path.GetFullPath(System.IO.Path.Combine(Directory, relativePath));
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        return target;
    }

    private static string MediaTypeOf(string path) => System.IO.Path.GetExtension(path) switch
    {
        ".json" => "application/json",
        ".jsonl" => "application/jsonl",
        ".yaml" or ".yml" => "application/yaml",
        ".js" => "text/javascript",
        ".csv" => "text/csv",
        ".hlog" or ".txt" or ".md" => "text/plain",
        _ => "application/octet-stream",
    };
}
