using Npgsql;

namespace Opportunity.Benchmarks.Capture;

/// <summary>
/// The benchmark-relevant subset of <c>SHOW ALL</c> (read from <c>pg_settings</c>, which carries unit and source),
/// plus role and streaming-replication state. Credentials are never recorded.
/// </summary>
public static class PostgresCapture
{
    /// <summary>Settings recorded with every result: durability/WAL, checkpoints, memory, parallelism, planner, autovacuum.</summary>
    public static IReadOnlyList<string> SettingNames { get; } =
    [
        // Durability (Q-05)
        "fsync", "synchronous_commit", "full_page_writes", "wal_sync_method", "wal_level", "synchronous_standby_names",
        "data_checksums", "commit_delay", "commit_siblings", "wal_writer_delay", "wal_writer_flush_after",
        // WAL volume and checkpoints
        "wal_buffers", "wal_compression", "max_wal_size", "min_wal_size", "checkpoint_timeout", "checkpoint_completion_target",
        "wal_keep_size", "max_wal_senders", "max_replication_slots", "hot_standby", "hot_standby_feedback", "archive_mode",
        // Memory and connections
        "shared_buffers", "effective_cache_size", "work_mem", "maintenance_work_mem", "huge_pages", "max_connections",
        "temp_buffers",
        // Parallelism and I/O
        "max_worker_processes", "max_parallel_workers", "max_parallel_workers_per_gather", "max_parallel_maintenance_workers",
        "effective_io_concurrency", "maintenance_io_concurrency", "io_method", "io_workers",
        // Planner
        "random_page_cost", "seq_page_cost", "default_statistics_target", "jit", "plan_cache_mode",
        // Autovacuum (§29 Phase 2 bloat/WAL behaviour)
        "autovacuum", "autovacuum_max_workers", "autovacuum_naptime", "autovacuum_vacuum_scale_factor",
        "autovacuum_vacuum_insert_scale_factor", "autovacuum_analyze_scale_factor", "autovacuum_vacuum_cost_limit",
        "autovacuum_vacuum_cost_delay",
        // Misc that changes timings
        "track_io_timing", "shared_preload_libraries", "server_encoding", "lc_collate", "default_toast_compression",
    ];

    public static async Task<PostgresInstance> CaptureAsync(string name, string connectionString, CancellationToken cancellationToken = default)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { ApplicationName = HarnessInfo.Name + " capture-env" };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var settings = new SortedDictionary<string, PgSetting>(StringComparer.Ordinal);
            var command = new NpgsqlCommand("SELECT name, setting, unit, source FROM pg_settings WHERE name = ANY(@names)", connection);
            await using (command.ConfigureAwait(false))
            {
                command.Parameters.AddWithValue("names", SettingNames.ToArray());
                var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        settings[reader.GetString(0)] = new PgSetting
                        {
                            Value = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                            Unit = reader.IsDBNull(2) ? null : reader.GetString(2),
                            Source = reader.IsDBNull(3) ? null : reader.GetString(3),
                        };
                    }
                }
            }

            string version = (string)(await ScalarAsync(connection, "SHOW server_version", cancellationToken).ConfigureAwait(false))!;
            bool inRecovery = (bool)(await ScalarAsync(connection, "SELECT pg_is_in_recovery()", cancellationToken).ConfigureAwait(false))!;
            IReadOnlyList<PostgresReplica> replicas = inRecovery ? [] : await ReplicationAsync(connection, cancellationToken).ConfigureAwait(false);

            return new PostgresInstance
            {
                Name = name,
                Endpoint = $"{builder.Host}:{builder.Port}/{builder.Database}",
                ServerVersion = version,
                Role = inRecovery ? PostgresRole.Replica : PostgresRole.Primary,
                Settings = settings,
                Durability = DurabilityOf(settings),
                Replication = replicas.Count == 0 ? null : replicas,
            };
        }
    }

    public static PostgresDurability DurabilityOf(IReadOnlyDictionary<string, PgSetting> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string Get(string key) => settings.TryGetValue(key, out PgSetting? s) ? s.Value : string.Empty;
        return new PostgresDurability
        {
            Fsync = Get("fsync"),
            SynchronousCommit = Get("synchronous_commit"),
            FullPageWrites = Get("full_page_writes"),
            WalLevel = Get("wal_level"),
            WalSyncMethod = Get("wal_sync_method"),
            SynchronousStandbyNames = Get("synchronous_standby_names"),
            DataChecksums = settings.TryGetValue("data_checksums", out PgSetting? checksums) ? checksums.Value : null,
        };
    }

    private static async Task<IReadOnlyList<PostgresReplica>> ReplicationAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var replicas = new List<PostgresReplica>();
        var command = new NpgsqlCommand("SELECT coalesce(application_name, ''), coalesce(state, ''), coalesce(sync_state, '') FROM pg_stat_replication ORDER BY 1", connection);
        await using (command.ConfigureAwait(false))
        {
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    replicas.Add(new PostgresReplica(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
                }
            }
        }

        return replicas;
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
#pragma warning disable CA2100 // Constant SQL only.
        var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await using (command.ConfigureAwait(false))
        {
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
