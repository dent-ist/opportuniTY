using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;

using Opportunity.Benchmarks;
using Opportunity.Benchmarks.Bundles;
using Opportunity.Benchmarks.Capture;
using Opportunity.Benchmarks.Gates;
using Opportunity.Benchmarks.Infrastructure;

// ---- capture-env -------------------------------------------------------------------------------------------------
var profileOption = new Option<string>("--profile")
{
    Description = "developer-regression (Compose dev profile) or enterprise-reference (§29).",
    DefaultValueFactory = _ => "developer-regression",
};
var outOption = new Option<FileInfo?>("--out") { Description = "Write the manifest here (default: stdout)." };
var roleOption = new Option<string>("--role") { Description = "Host role recorded for this machine.", DefaultValueFactory = _ => "all-in-one" };
var noHostnameOption = new Option<bool>("--no-hostname") { Description = "Do not record the host name." };
var cloudOption = new Option<string?>("--cloud-provider") { Description = "Cloud provider (aws, azure, gcp, hetzner, ...) when not auto-detected." };
var instanceTypeOption = new Option<string?>("--instance-type") { Description = "Instance shape, e.g. r7i.2xlarge." };
var regionOption = new Option<string?>("--region") { Description = "Cloud region." };
var versionsOption = new Option<FileInfo?>("--versions") { Description = "versions.env (default: found above the working directory)." };
var composeProjectOption = new Option<string?>("--compose-project") { Description = "Inspect every container of this Compose project, e.g. opportunity-dev." };
var containerOption = new Option<string[]>("--container") { Description = "Also inspect this container (name or id). Repeatable.", AllowMultipleArgumentsPerToken = true };
var postgresOption = new Option<string[]>("--postgres")
{
    Description = "name=<connection string> or name=env:VAR (keeps the password off the command line). Repeatable (primary, replica).",
    AllowMultipleArgumentsPerToken = true,
};
var openSearchOption = new Option<string?>("--opensearch") { Description = "OpenSearch REST endpoint, e.g. http://127.0.0.1:9200 (user:password@ allowed), or env:VAR." };
var systemIndicesOption = new Option<bool>("--include-system-indices") { Description = "Also record indices whose name starts with '.'." };
var rabbitOption = new Option<string?>("--rabbitmq-management") { Description = "RabbitMQ management URL (http://user:password@127.0.0.1:15672) or env:VAR." };
var objectStoreOption = new Option<string?>("--object-store") { Description = "Provider[=implementation], e.g. S3=seaweedfs 4.48 or FileSystem. Default: detected from containers." };
var workerOption = new Option<string[]>("--worker") { Description = "type=instances[:concurrency], e.g. indexing=4:8. Repeatable. Default: detected from containers.", AllowMultipleArgumentsPerToken = true };
var relaxedOption = new Option<string?>("--relaxed-durability") { Description = "Justification required when durability is relaxed (Q-05: developer profile only)." };
var noteOption = new Option<string[]>("--note") { Description = "Free-text note recorded in the manifest. Repeatable.", AllowMultipleArgumentsPerToken = true };
var noDockerOption = new Option<bool>("--no-docker") { Description = "Skip the container runtime (bare-metal services)." };

var captureEnv = new Command("capture-env", "Capture the benchmark environment manifest: hardware, versions and digests, JVM/OpenSearch and PostgreSQL settings, durability, shard topology.")
{
    profileOption, outOption, roleOption, noHostnameOption, cloudOption, instanceTypeOption, regionOption, versionsOption,
    composeProjectOption, containerOption, postgresOption, openSearchOption, systemIndicesOption, rabbitOption, objectStoreOption,
    workerOption, relaxedOption, noteOption, noDockerOption,
};
captureEnv.SetAction(async (parse, cancellationToken) =>
{
    var options = new CaptureOptions
    {
        Profile = ParseProfile(parse.GetValue(profileOption)!),
        Host = new HostOverrides
        {
            Role = parse.GetValue(roleOption)!,
            IncludeHostname = !parse.GetValue(noHostnameOption),
            CloudProvider = parse.GetValue(cloudOption),
            InstanceType = parse.GetValue(instanceTypeOption),
            Region = parse.GetValue(regionOption),
        },
        VersionsEnvPath = parse.GetValue(versionsOption)?.FullName,
        ComposeProject = parse.GetValue(composeProjectOption),
        Containers = parse.GetValue(containerOption) ?? [],
        Postgres = [.. (parse.GetValue(postgresOption) ?? []).Select(ParsePostgres)],
        OpenSearch = parse.GetValue(openSearchOption) is { } os ? new Uri(ResolveEnv(os, "--opensearch")) : null,
        IncludeSystemIndices = parse.GetValue(systemIndicesOption),
        RabbitMqManagement = parse.GetValue(rabbitOption) is { } rabbit ? new Uri(ResolveEnv(rabbit, "--rabbitmq-management")) : null,
        ObjectStore = parse.GetValue(objectStoreOption) is { } store ? ParseObjectStore(store) : null,
        Workers = [.. (parse.GetValue(workerOption) ?? []).Select(ParseWorker)],
        RelaxedDurabilityJustification = parse.GetValue(relaxedOption),
        Notes = parse.GetValue(noteOption) ?? [],
        SkipDocker = parse.GetValue(noDockerOption),
    };

    EnvironmentManifest manifest = await new EnvironmentCapturer().CaptureAsync(options, cancellationToken).ConfigureAwait(false);
    if (manifest.Durability.Mode == DurabilityMode.Relaxed && string.IsNullOrWhiteSpace(manifest.Durability.Justification))
    {
        await Console.Error.WriteLineAsync("error: durability is relaxed; record why with --relaxed-durability \"<justification>\" (Q-05):").ConfigureAwait(false);
        foreach (DurabilityDeviation d in manifest.Durability.Deviations)
        {
            await Console.Error.WriteLineAsync($"  {d.Component} {d.Instance}: {d.Setting}={d.Actual} (production: {d.Expected})").ConfigureAwait(false);
        }

        return 3;
    }

    IReadOnlyList<string> errors = BundleSchemas.ValidateEnvironment(BundleSchemas.ToNode(manifest));
    if (errors.Count > 0)
    {
        foreach (string error in errors)
        {
            await Console.Error.WriteLineAsync("schema: " + error).ConfigureAwait(false);
        }

        return 4;
    }

    string json = BenchJson.Serialize(manifest);
    if (parse.GetValue(outOption) is { } file)
    {
        Directory.CreateDirectory(file.DirectoryName!);
        await File.WriteAllTextAsync(file.FullName, json, cancellationToken).ConfigureAwait(false);
        await Console.Error.WriteLineAsync($"Environment manifest ({parse.GetValue(profileOption)}, durability {(manifest.Durability.Mode == DurabilityMode.Production ? "production" : "relaxed")}) -> {file.FullName}").ConfigureAwait(false);
    }
    else
    {
        await Console.Out.WriteAsync(json).ConfigureAwait(false);
    }

    return 0;
});

// ---- validate / publish -----------------------------------------------------------------------------------------
var bundleOption = new Option<DirectoryInfo>("--bundle") { Description = "Run directory containing bundle.json.", Required = true };
var gatesOption = new Option<FileInfo?>("--gates") { Description = "gates.yaml the run must have used (hash check), e.g. the frozen file in the repository." };

var validate = new Command("validate", "Validate a result bundle against the schema and the bundle rules (complete manifest, hashes, Q-04/Q-05).") { bundleOption, gatesOption };
validate.SetAction(parse => Report(BundleValidator.Validate(parse.GetValue(bundleOption)!.FullName, ValidationOptions(parse))));

var destinationOption = new Option<DirectoryInfo>("--dest") { Description = "Publication root (local, mounted bucket or sync staging directory).", Required = true };
var publish = new Command("publish", "Validate and publish a run directory write-once to <dest>/<profile>/<runId>/ and append it to <dest>/index.jsonl.")
{
    bundleOption, gatesOption, destinationOption,
};
publish.SetAction(parse =>
{
    try
    {
        PublishedRun run = BundlePublisher.Publish(parse.GetValue(bundleOption)!.FullName, parse.GetValue(destinationOption)!.FullName, ValidationOptions(parse));
        Console.WriteLine(BenchJson.Serialize(run).TrimEnd());
        return 0;
    }
    catch (BundleRejectedException ex)
    {
        Console.Error.WriteLine("rejected: " + ex.Message);
        return 1;
    }
});

// ---- gates ------------------------------------------------------------------------------------------------------
var gatesFileOption = new Option<FileInfo?>("--gates") { Description = "gates.yaml (default: tools/Opportunity.Benchmarks/gates.yaml)." };
var gatesCheck = new Command("gates", "Parse and check gates.yaml, check every gate path against the result-bundle schema, print its SHA-256.") { gatesFileOption };
gatesCheck.SetAction(parse =>
{
    string path = parse.GetValue(gatesFileOption)?.FullName ?? GatesFile.Locate() ?? throw new FileNotFoundException("gates.yaml not found; pass --gates.");
    GatesFile gates = GatesFile.Load(path);
    var errors = gates.Check().Concat(gates.CheckAgainstSchema(JsonNode.Parse(BundleSchemas.ReadResource(BundleSchemas.ResultBundleResource))!)).ToList();
    foreach (string error in errors)
    {
        Console.Error.WriteLine("error: " + error);
    }

    Console.WriteLine($"{gates.Sha256}  {path}  ({gates.Document.Status}, version {gates.Document.GatesVersion}, {gates.Document.Gates.Count} gates)");
    return errors.Count == 0 ? 0 : 1;
});

var schemaNameOption = new Option<string>("--name") { Description = "result-bundle or environment-manifest.", DefaultValueFactory = _ => "result-bundle" };
var schema = new Command("schema", "Print an embedded JSON Schema.") { schemaNameOption };
schema.SetAction(parse =>
{
    Console.Write(BundleSchemas.ReadResource(parse.GetValue(schemaNameOption) switch
    {
        "environment-manifest" => BundleSchemas.EnvironmentManifestResource,
        "result-bundle" => BundleSchemas.ResultBundleResource,
        var other => throw new ArgumentException($"Unknown schema '{other}'."),
    }));
    return 0;
});

var root = new RootCommand($"{HarnessInfo.Name} {HarnessInfo.Version}: opportuniTY benchmark harness (E17). Synthetic data only.")
{
    captureEnv, validate, publish, gatesCheck, schema,
};

try
{
    return await root.Parse(args).InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false }).ConfigureAwait(false);
}
catch (Exception ex) when (ex is ArgumentException or FormatException or FileNotFoundException or InvalidOperationException or HttpRequestException or Npgsql.NpgsqlException)
{
    await Console.Error.WriteLineAsync("error: " + ex.Message).ConfigureAwait(false);
    return 2;
}

static int Report(BundleValidationReport report)
{
    foreach (string warning in report.Warnings)
    {
        Console.Error.WriteLine("warning: " + warning);
    }

    foreach (string error in report.Errors)
    {
        Console.Error.WriteLine("error: " + error);
    }

    Console.WriteLine(report.IsValid ? "bundle is valid" : string.Create(CultureInfo.InvariantCulture, $"bundle is INVALID ({report.Errors.Count} errors)"));
    return report.IsValid ? 0 : 1;
}

BundleValidationOptions ValidationOptions(ParseResult parse) => new() { GatesPath = parse.GetValue(gatesOption)?.FullName };

static BenchmarkProfile ParseProfile(string value) => value switch
{
    "developer-regression" or "dev" => BenchmarkProfile.DeveloperRegression,
    "enterprise-reference" or "reference" => BenchmarkProfile.EnterpriseReference,
    _ => throw new ArgumentException($"Unknown --profile '{value}' (developer-regression or enterprise-reference)."),
};

static string ResolveEnv(string value, string option)
{
    if (!value.StartsWith("env:", StringComparison.Ordinal))
    {
        return value;
    }

    string name = value[4..];
    return Environment.GetEnvironmentVariable(name) is { Length: > 0 } resolved
        ? resolved
        : throw new ArgumentException($"{option}: environment variable {name} is not set.");
}

static PostgresTarget ParsePostgres(string spec)
{
    int eq = spec.IndexOf('=', StringComparison.Ordinal);
    return eq > 0
        ? new PostgresTarget(spec[..eq], ResolveEnv(spec[(eq + 1)..], "--postgres"))
        : throw new ArgumentException($"--postgres '{spec}': expected name=<connection string> or name=env:VAR.");
}

static ObjectStoreInfo ParseObjectStore(string spec)
{
    string[] parts = spec.Split('=', 2);
    return parts[0] is "FileSystem" or "S3" or "AzureBlob"
        ? new ObjectStoreInfo { Provider = parts[0], Implementation = parts.Length > 1 ? parts[1] : null }
        : throw new ArgumentException($"--object-store '{spec}': provider must be FileSystem, S3 or AzureBlob.");
}

static WorkerPool ParseWorker(string spec)
{
    string[] parts = spec.Split('=', 2);
    string[] counts = parts.Length == 2 ? parts[1].Split(':', 2) : [];
    if (counts.Length == 0 || !int.TryParse(counts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int instances)
        || (counts.Length == 2 && !int.TryParse(counts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
    {
        throw new ArgumentException($"--worker '{spec}': expected type=instances[:concurrency].");
    }

    return new WorkerPool
    {
        Type = parts[0],
        Instances = instances,
        Concurrency = counts.Length == 2 ? int.Parse(counts[1], CultureInfo.InvariantCulture) : null,
    };
}
