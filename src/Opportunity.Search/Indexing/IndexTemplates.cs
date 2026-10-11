using System.Net;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

using Opportunity.Application.Bootstrap;

using static Opportunity.Search.Indexing.JsonBodies;

namespace Opportunity.Search.Indexing;

/// <summary>Builds and installs the composable index template of each projection generation (ADR-006 §6, ADR-007 R2).</summary>
internal sealed class IndexTemplates(OpenSearchConnection connection, IndexNames names, ProjectionMappings mappings, OpenSearchOptions options)
{
    private const int BasePriority = 100;

    public IndexNames Names => names;

    public ProjectionMappings Mappings => mappings;

    public JsonObject Build(int generation)
    {
        var mapping = mappings.Load(generation);
        var settings = (JsonObject?)mapping["settings"]?.DeepClone() ?? [];
        settings["index"] = Obj(
            ("number_of_replicas", options.Replicas),
            ("refresh_interval", options.RefreshInterval),
            ("max_result_window", options.MaxResultWindow),

            // Delete tombstones must outlive every in-flight external-versioned write (ADR-001 §4 R2).
            ("gc_deletes", "10m"),
            ("mapping", Obj(("total_fields", Obj(("limit", options.TotalFieldsLimit))))));

        return Obj(
            ("index_patterns", new JsonArray([.. names.TemplatePatterns(generation).Select(p => (JsonNode)p)])),
            ("priority", BasePriority + generation),
            ("version", generation),
            ("template", Obj(("settings", settings), ("mappings", mapping["mappings"]!.DeepClone()))),
            ("_meta", Obj(
                ("managed_by", "opportunity-migrator"),
                ("projection_generation", generation),
                ("mapping_sha256", mappings.Checksum(generation)))));
    }

    /// <summary>Creates or replaces every generation's template; idempotent (PUT replaces in place).</summary>
    public async Task InstallAllAsync(CancellationToken cancellationToken)
    {
        foreach (var generation in mappings.Generations)
        {
            await connection.SendAsync(
                HttpMethod.Put, $"_index_template/{Escape(names.Template(generation))}", Build(generation), cancellationToken)
                .ConfigureAwait(false);
            await AddMissingPropertiesAsync(generation, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Additive mapping changes within a generation (e.g. #179's folder fields): top-level properties this build's
    /// mapping has and an existing index of the generation lacks are added in place (a new field needs no reindex; its
    /// documents carry it once re-projected). Changed or removed properties still need a new generation.
    /// </summary>
    private async Task AddMissingPropertiesAsync(int generation, CancellationToken cancellationToken)
    {
        var wanted = mappings.Load(generation)["mappings"]?["properties"] as JsonObject ?? [];
        var patterns = string.Join(',', names.TemplatePatterns(generation).Select(Escape));
        var response = await connection.SendAsync(
            HttpMethod.Get, $"{patterns}/_mapping?ignore_unavailable=true&allow_no_indices=true", null, cancellationToken, HttpStatusCode.NotFound)
            .ConfigureAwait(false);
        if (response.Status != HttpStatusCode.OK || response.Body is not JsonObject indexes)
        {
            return;
        }

        foreach (var (index, value) in indexes)
        {
            var existing = value?["mappings"]?["properties"] as JsonObject;
            var missing = new JsonObject();
            foreach (var (name, definition) in wanted)
            {
                if (existing?.ContainsKey(name) != true)
                {
                    missing[name] = definition?.DeepClone();
                }
            }

            if (missing.Count > 0)
            {
                await connection.SendAsync(HttpMethod.Put, $"{Escape(index)}/_mapping", Obj(("properties", missing)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>True when generation <paramref name="generation"/>'s template is installed with this build's mapping.</summary>
    public async Task<bool> IsInstalledAsync(int generation, CancellationToken cancellationToken)
    {
        var response = await connection.SendAsync(
            HttpMethod.Get, $"_index_template/{Escape(names.Template(generation))}", null, cancellationToken, HttpStatusCode.NotFound)
            .ConfigureAwait(false);
        if (response.Status == HttpStatusCode.NotFound)
        {
            return false;
        }

        var installed = response.Body?["index_templates"]?[0]?["index_template"]?["_meta"]?["mapping_sha256"]?.GetValue<string>();
        return installed == mappings.Checksum(generation);
    }
}

/// <summary>
/// Migrator step (E04-T01): installs the projection index templates. Indexes themselves are created on demand by
/// <see cref="IndexManager"/>; templates must exist first so no index is ever created without the strict mapping.
/// </summary>
internal sealed partial class IndexTemplateBootstrapStep(IndexTemplates templates, ILogger<IndexTemplateBootstrapStep> logger)
    : IInfrastructureBootstrapStep
{
    public string Name => "opensearch-index-templates";

    public int Order => 100;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await templates.InstallAllAsync(cancellationToken).ConfigureAwait(false);
        LogInstalled(logger, templates.Mappings.Generations.Count, templates.Mappings.CurrentGeneration, templates.Names.Prefix);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Installed {Count} projection index template(s), current generation {Generation}, prefix {Prefix}")]
    private static partial void LogInstalled(ILogger logger, int count, int generation, string prefix);
}
