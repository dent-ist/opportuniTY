using System.Text.Json;
using System.Text.Json.Nodes;

using Json.Schema;

namespace Opportunity.Benchmarks.Bundles;

/// <summary>The embedded v1 JSON Schemas (draft 2020-12) and schema validation with readable error lists.</summary>
public static class BundleSchemas
{
    public const string EnvironmentManifestResource = "schema/environment-manifest.v1.schema.json";
    public const string ResultBundleResource = "schema/result-bundle.v1.schema.json";
    public const string ShadowLedgerVerdictResource = "schema/shadow-ledger-verdict.v1.schema.json";

    private static readonly Lazy<(JsonSchema Environment, JsonSchema Bundle, JsonSchema Verdict)> Schemas = new(Load);

    public static JsonSchema EnvironmentManifest => Schemas.Value.Environment;

    public static JsonSchema ResultBundle => Schemas.Value.Bundle;

    /// <summary>verdict.json of the shadow-ledger oracle (E17-T07); its <c>shadowLedger</c> is the bundle's oracle object.</summary>
    public static JsonSchema ShadowLedgerVerdict => Schemas.Value.Verdict;

    public static string ReadResource(string name)
    {
        using Stream stream = typeof(BundleSchemas).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static IReadOnlyList<string> ValidateEnvironment(JsonNode? instance) => Validate(EnvironmentManifest, instance);

    public static IReadOnlyList<string> ValidateBundle(JsonNode? instance) => Validate(ResultBundle, instance);

    public static IReadOnlyList<string> ValidateShadowLedgerVerdict(JsonNode? instance) => Validate(ShadowLedgerVerdict, instance);

    /// <summary>Returns one line per failed leaf keyword: <c>/instance/path: message (schema keyword path)</c>.</summary>
    public static IReadOnlyList<string> Validate(JsonSchema schema, JsonNode? instance)
    {
        ArgumentNullException.ThrowIfNull(schema);
        EvaluationResults results = schema.Evaluate(JsonSerializer.SerializeToElement(instance), NewOptions());
        if (results.IsValid)
        {
            return [];
        }

        var errors = new SortedSet<string>(StringComparer.Ordinal);
        // An `if` that does not match is not an error; only report keywords outside `if` subschemas.
        foreach (EvaluationResults detail in new[] { results }.Concat(results.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 } && !d.EvaluationPath.ToString().Contains("/if", StringComparison.Ordinal)))
        {
            foreach (KeyValuePair<string, string> error in detail.Errors!)
            {
                string location = detail.InstanceLocation.ToString();
                errors.Add($"{(location.Length == 0 ? "/" : location)}: {error.Value} ({detail.EvaluationPath}/{error.Key})");
            }
        }

        if (errors.Count == 0)
        {
            errors.Add("/: document does not match the schema.");
        }

        return [.. errors];
    }

    private static EvaluationOptions NewOptions() => new()
    {
        OutputFormat = OutputFormat.List,
        RequireFormatValidation = true,
    };

    private static (JsonSchema, JsonSchema, JsonSchema) Load()
    {
        JsonSchema environment = JsonSchema.FromText(ReadResource(EnvironmentManifestResource));
        JsonSchema bundle = JsonSchema.FromText(ReadResource(ResultBundleResource));
        JsonSchema verdict = JsonSchema.FromText(ReadResource(ShadowLedgerVerdictResource));
        // The bundle schema references the environment schema by its $id; the verdict references the bundle's oracle.
        SchemaRegistry.Global.Register(environment);
        SchemaRegistry.Global.Register(bundle);
        SchemaRegistry.Global.Register(verdict);
        return (environment, bundle, verdict);
    }

    public static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Infrastructure.BenchJson.Options);
}
