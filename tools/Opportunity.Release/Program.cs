using Opportunity.Release;

// opportunity-release compatibility --version X.Y.Z [--commit SHA] [--previous vX.Y.Z] [--versions-env FILE] --json FILE [--markdown FILE]
const string Usage =
    "usage: opportunity-release compatibility --version X.Y.Z [--commit SHA] [--previous vX.Y.Z] " +
    "[--versions-env versions.env] " +
    "--json compatibility.json [--markdown COMPATIBILITY.md]";

if (args.Length == 0 || args[0] != "compatibility")
{
    await Console.Error.WriteLineAsync(Usage);
    return 2;
}

var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 1; i < args.Length; i++)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
    {
        await Console.Error.WriteLineAsync($"unexpected argument '{args[i]}'\n{Usage}");
        return 2;
    }

    options[args[i][2..]] = args[++i];
}

if (!options.TryGetValue("version", out var version) || !options.TryGetValue("json", out var jsonPath))
{
    await Console.Error.WriteLineAsync(Usage);
    return 2;
}

try
{
    var versions = CompatibilityMatrix.ReadVersionsEnv(options.GetValueOrDefault("versions-env", "versions.env"));
    var matrix = CompatibilityMatrix.FromCode(
        version.TrimStart('v'), options.GetValueOrDefault("commit"), versions, options.GetValueOrDefault("previous"));
    await File.WriteAllTextAsync(jsonPath, matrix.ToJson());
    if (options.TryGetValue("markdown", out var markdownPath))
    {
        await File.WriteAllTextAsync(markdownPath, matrix.ToMarkdown());
    }

    Console.WriteLine(matrix.ToJson());
    return 0;
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
{
    await Console.Error.WriteLineAsync("error: " + ex.Message);
    return 1;
}
