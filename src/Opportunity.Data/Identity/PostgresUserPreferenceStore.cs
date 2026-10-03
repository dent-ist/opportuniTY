using System.Text.Json;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Identity;

namespace Opportunity.Data.Identity;

/// <summary>PostgreSQL implementation of <see cref="IUserPreferenceStore"/> over <c>user_preference</c> (V0013, installation-level).</summary>
public sealed class PostgresUserPreferenceStore(NpgsqlDataSource dataSource) : IUserPreferenceStore
{
    public async Task<IReadOnlyDictionary<string, JsonElement>> GetAllAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            "SELECT pref_key, value::text FROM opportunity.user_preference WHERE user_id = @user ORDER BY pref_key");
        command.Parameters.AddWithValue("user", userId);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                using var json = JsonDocument.Parse(reader.GetString(1));
                values[reader.GetString(0)] = json.RootElement.Clone();
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return values;
    }

    public async Task<bool> SetAsync(Guid userId, string key, JsonElement value, int maxKeys, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);

        // The key limit is checked in the same statement; two concurrent first writes of different keys may overshoot
        // it by one, which is harmless for a per-user convenience store.
        await using var command = tx.Command(
            """
            INSERT INTO opportunity.user_preference (user_id, pref_key, value)
            SELECT @user, @key, @value
             WHERE EXISTS (SELECT 1 FROM opportunity.user_preference WHERE user_id = @user AND pref_key = @key)
                OR (SELECT count(*) FROM opportunity.user_preference WHERE user_id = @user) < @max
            ON CONFLICT (user_id, pref_key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
            """);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.Add(new NpgsqlParameter("value", NpgsqlDbType.Jsonb) { Value = value.GetRawText() });
        command.Parameters.AddWithValue("max", (long)maxKeys);
        var stored = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return stored;
    }

    public async Task RemoveAsync(Guid userId, string key, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command("DELETE FROM opportunity.user_preference WHERE user_id = @user AND pref_key = @key");
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("key", key);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
