namespace Opportunity.Testing.Images;

/// <summary>
/// Image references for the fixtures, built from <c>versions.env</c> as <c>repository:TAG@DIGEST</c>.
/// </summary>
/// <remarks>
/// A registry mirror (air-gapped hosts, or a registry the runner cannot reach) can be substituted per image
/// with <c>OPPORTUNITY_TEST_IMAGE_&lt;KEY&gt;</c>, e.g. <c>OPPORTUNITY_TEST_IMAGE_TOXIPROXY=mirror.local/toxiproxy:2.12.0</c>.
/// The override replaces the whole reference, so it should point at the same version.
/// </remarks>
public static class ContainerImages
{
    public const string OverridePrefix = "OPPORTUNITY_TEST_IMAGE_";

    public static string Postgres => Resolve("POSTGRES", "postgres");

    public static string OpenSearch => Resolve("OPENSEARCH", "opensearchproject/opensearch");

    public static string RabbitMq => Resolve("RABBITMQ", "rabbitmq");

    /// <summary>SeaweedFS (Apache-2.0) as the S3-compatible store; see decision Q-38.</summary>
    public static string ObjectStore => Resolve("SEAWEEDFS", "chrislusf/seaweedfs");

    public static string Toxiproxy => Resolve("TOXIPROXY", "ghcr.io/shopify/toxiproxy");

    /// <summary>Keycloak (Apache-2.0), the developer OIDC provider (E05-T01).</summary>
    public static string Keycloak => Resolve("KEYCLOAK", "keycloak/keycloak");

    /// <summary>Prometheus (Apache-2.0) of the observability profile; its <c>promtool</c> checks the alert rules (E19-T05).</summary>
    public static string Prometheus => Resolve("PROMETHEUS", "prom/prometheus");

    public static string Resolve(string key, string repository) => Resolve(VersionsFile.Repository, key, repository);

    public static string Resolve(VersionsFile versions, string key, string repository)
    {
        ArgumentNullException.ThrowIfNull(versions);

        var overridden = Environment.GetEnvironmentVariable(OverridePrefix + key);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden.Trim();
        }

        var reference = $"{repository}:{versions[key]}";
        var digest = versions.TryGet(key + "_DIGEST");
        return string.IsNullOrEmpty(digest) ? reference : $"{reference}@{digest}";
    }
}
