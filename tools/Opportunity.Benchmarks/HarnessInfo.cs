namespace Opportunity.Benchmarks;

/// <summary>Identity of the harness recorded in every manifest and bundle.</summary>
public static class HarnessInfo
{
    public const string Name = "opportunity-bench";

    /// <summary>Bump the minor version for new optional manifest content, the major version with a new schema major.</summary>
    public const string Version = "1.0.0";

    /// <summary>Schema version written to manifests and bundles (schema files are <c>*.v1.schema.json</c>).</summary>
    public const string SchemaVersion = "1.0";
}
