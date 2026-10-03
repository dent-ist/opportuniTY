namespace Opportunity.Benchmarks.Capture;

/// <summary>
/// Decision Q-05: reference runs use production-like durability; relaxed settings are allowed only on the developer
/// profile and must be recorded. This computes the deviations from the captured settings, so a manifest cannot claim
/// "production" while its own settings say otherwise.
/// </summary>
public static class DurabilityPolicy
{
    private static readonly string[] DurableSynchronousCommit = ["on", "remote_write", "remote_apply"];

    public static IReadOnlyList<DurabilityDeviation> Evaluate(EnvironmentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var deviations = new List<DurabilityDeviation>();

        foreach (PostgresInstance pg in manifest.Postgres ?? [])
        {
            if (pg.Durability.Fsync != "on")
            {
                deviations.Add(new("postgres", pg.Name, "fsync", pg.Durability.Fsync, "on"));
            }

            if (!DurableSynchronousCommit.Contains(pg.Durability.SynchronousCommit, StringComparer.Ordinal))
            {
                deviations.Add(new("postgres", pg.Name, "synchronous_commit", pg.Durability.SynchronousCommit, "on | remote_write | remote_apply"));
            }

            if (pg.Durability.FullPageWrites != "on")
            {
                deviations.Add(new("postgres", pg.Name, "full_page_writes", pg.Durability.FullPageWrites, "on"));
            }
        }

        if (manifest.Opensearch is { } os)
        {
            foreach (OpenSearchIndex index in os.Indices)
            {
                if (!string.Equals(index.TranslogDurability, "request", StringComparison.OrdinalIgnoreCase))
                {
                    deviations.Add(new("opensearch", index.Name, "index.translog.durability", index.TranslogDurability, "request"));
                }

                // A single-node developer cluster cannot hold replicas; that is topology, not relaxed durability.
                if (manifest.Profile == BenchmarkProfile.EnterpriseReference && index.Replicas < 1)
                {
                    deviations.Add(new("opensearch", index.Name, "index.number_of_replicas", index.Replicas.ToString(System.Globalization.CultureInfo.InvariantCulture), ">= 1"));
                }
            }
        }

        foreach (RabbitMqQueueInfo queue in manifest.Rabbitmq?.Queues ?? [])
        {
            if (!queue.Durable)
            {
                deviations.Add(new("rabbitmq", $"{queue.Vhost}/{queue.Name}", "durable", "false", "true"));
            }
        }

        return deviations;
    }

    /// <summary>Builds the durability block; a relaxed environment must carry a justification (schema + validator enforce it).</summary>
    public static DurabilityInfo Describe(IReadOnlyList<DurabilityDeviation> deviations, string? justification)
    {
        ArgumentNullException.ThrowIfNull(deviations);
        return deviations.Count == 0
            ? new DurabilityInfo { Mode = DurabilityMode.Production, Deviations = [] }
            : new DurabilityInfo { Mode = DurabilityMode.Relaxed, Deviations = deviations, Justification = justification };
    }
}
