using System.Diagnostics;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace Opportunity.Data.Migrations;

/// <summary>Outcome of a migrator run.</summary>
public sealed record MigrationResult(int? StartVersion, int? EndVersion, IReadOnlyList<MigrationScript> Applied)
{
    public bool WasNoOp => Applied.Count == 0;
}

/// <summary>
/// Forward-only SQL migration runner. Serializes runs per database with a session-level advisory lock, records each
/// script in <see cref="SchemaHistory.Table"/> with its SHA-256 checksum, and refuses to run when an applied script
/// changed, is missing, or a pending script is older than the database version.
/// </summary>
public sealed partial class PostgresMigrator
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IReadOnlyList<MigrationScript> _scripts;
    private readonly MigratorOptions _options;
    private readonly ILogger _logger;

    public PostgresMigrator(
        NpgsqlDataSource dataSource,
        IEnumerable<MigrationScript>? scripts = null,
        MigratorOptions? options = null,
        ILogger<PostgresMigrator>? logger = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _scripts = scripts is null ? MigrationCatalog.Scripts : MigrationCatalog.Order(scripts);
        _options = options ?? new MigratorOptions();
        _logger = logger ?? NullLogger<PostgresMigrator>.Instance;
    }

    public async Task<MigrationResult> MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireLockAsync(connection, cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteAsync(connection, null, SchemaHistory.CreateSql, cancellationToken).ConfigureAwait(false);
            var applied = await ReadHistoryAsync(connection, cancellationToken).ConfigureAwait(false);
            var pending = Validate(applied);
            int? startVersion = applied.Count == 0 ? null : applied.Keys.Max();

            foreach (var script in pending)
            {
                await ApplyAsync(connection, script, cancellationToken).ConfigureAwait(false);
            }

            int? endVersion = pending.Count > 0 ? pending[^1].Version : startVersion;
            LogCompleted(_logger, pending.Count, startVersion, endVersion);
            return new MigrationResult(startVersion, endVersion, pending);
        }
        finally
        {
            await ReleaseLockAsync(connection).ConfigureAwait(false);
        }
    }

    private List<MigrationScript> Validate(Dictionary<int, AppliedScript> applied)
    {
        var byVersion = _scripts.ToDictionary(s => s.Version);
        foreach (var (version, row) in applied.OrderBy(kv => kv.Key))
        {
            if (!byVersion.TryGetValue(version, out var script))
            {
                throw new MigrationException(
                    $"Database has migration {version} ('{row.ScriptName}') that this migrator does not know; " +
                    "the database is newer than this build. Refusing to run.");
            }

            if (!string.Equals(script.Checksum, row.Checksum, StringComparison.Ordinal))
            {
                throw new MigrationChecksumMismatchException(
                    $"Migration {version} ('{script.Name}') was modified after it was applied " +
                    $"(recorded checksum {row.Checksum}, current {script.Checksum}). Migrations are forward-only: " +
                    "revert the edit and add a new script instead.");
            }
        }

        var maxApplied = applied.Count == 0 ? 0 : applied.Keys.Max();
        var pending = _scripts.Where(s => !applied.ContainsKey(s.Version)).ToList();
        var outOfOrder = pending.FirstOrDefault(s => s.Version < maxApplied);
        if (outOfOrder is not null)
        {
            throw new MigrationException(
                $"Pending migration {outOfOrder.Version} ('{outOfOrder.Name}') is older than the database version " +
                $"{maxApplied}. Renumber it above {maxApplied}.");
        }

        return pending;
    }

    private async Task ApplyAsync(NpgsqlConnection connection, MigrationScript script, CancellationToken cancellationToken)
    {
        LogApplying(_logger, script.Version, script.Name, script.IsTransactional);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (script.IsTransactional)
            {
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, script.Sql, cancellationToken).ConfigureAwait(false);
                await LintAsync(connection, transaction, script, cancellationToken).ConfigureAwait(false);
                await RecordAsync(connection, transaction, script, stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Each statement is sent alone: a multi-statement command would run in an implicit transaction.
                foreach (var statement in script.SplitStatements())
                {
                    await ExecuteAsync(connection, null, statement, cancellationToken).ConfigureAwait(false);
                }

                await LintAsync(connection, null, script, cancellationToken).ConfigureAwait(false);
                await RecordAsync(connection, null, script, stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (PostgresException ex)
        {
            throw new MigrationException(
                $"Migration {script.Version} ('{script.Name}') failed: {ex.SqlState} {ex.MessageText}" +
                (script.IsTransactional ? " (rolled back)." : " (non-transactional: earlier statements may have been applied)."),
                ex);
        }
    }

    private async Task LintAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, MigrationScript script, CancellationToken cancellationToken)
    {
        if (!_options.EnforceTenantKeyLint)
        {
            return;
        }

        var violations = await TenantKeyLint.FindViolationsAsync(
            connection, transaction, _options.TenantSchemas, _options.TenantKeyColumn, cancellationToken).ConfigureAwait(false);
        if (violations.Count > 0)
        {
            throw new MigrationException(
                $"Migration {script.Version} ('{script.Name}') violates the tenant key rule:{Environment.NewLine}" +
                string.Join(Environment.NewLine, violations));
        }
    }

    private static async Task RecordAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, MigrationScript script, TimeSpan elapsed,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {SchemaHistory.Table} (version, description, script_name, checksum, transactional, execution_ms)
            VALUES (@version, @description, @name, @checksum, @transactional, @ms)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("version", script.Version);
        command.Parameters.AddWithValue("description", script.Description);
        command.Parameters.AddWithValue("name", script.Name);
        command.Parameters.AddWithValue("checksum", script.Checksum);
        command.Parameters.AddWithValue("transactional", script.IsTransactional);
        command.Parameters.AddWithValue("ms", (int)Math.Min(int.MaxValue, elapsed.TotalMilliseconds));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Dictionary<int, AppliedScript>> ReadHistoryAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT version, script_name, checksum FROM {SchemaHistory.Table}", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var applied = new Dictionary<int, AppliedScript>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            applied[reader.GetInt32(0)] = new AppliedScript(reader.GetString(1), reader.GetString(2));
        }

        return applied;
    }

    private async Task AcquireLockAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
        command.Parameters.AddWithValue("key", SchemaHistory.AdvisoryLockKey);
        var deadline = DateTimeOffset.UtcNow + _options.LockWaitTimeout;
        var logged = false;
        while (!(bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new MigrationException(
                    $"Another migrator held the migration lock for longer than {_options.LockWaitTimeout}.");
            }

            if (!logged)
            {
                LogWaitingForLock(_logger);
                logged = true;
            }

            await Task.Delay(_options.LockPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReleaseLockAsync(NpgsqlConnection connection)
    {
        // Closing the session releases the lock as well; this only matters for pooled connections.
        if (connection.State != System.Data.ConnectionState.Open)
        {
            return;
        }

        await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
        command.Parameters.AddWithValue("key", SchemaHistory.AdvisoryLockKey);
        await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction)
        {
            CommandTimeout = _options.CommandTimeoutSeconds,
        };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record AppliedScript(string ScriptName, string Checksum);

    [LoggerMessage(Level = LogLevel.Information, Message = "Waiting for another migrator to release the migration lock")]
    private static partial void LogWaitingForLock(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying migration {Version} ({Name}, transactional: {Transactional})")]
    private static partial void LogApplying(ILogger logger, int version, string name, bool transactional);

    [LoggerMessage(Level = LogLevel.Information, Message = "Migrations complete: {Count} applied, version {StartVersion} -> {EndVersion}")]
    private static partial void LogCompleted(ILogger logger, int count, int? startVersion, int? endVersion);
}
