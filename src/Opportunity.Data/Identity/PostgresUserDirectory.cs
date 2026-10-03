using Npgsql;

using Opportunity.Application.Identity;

namespace Opportunity.Data.Identity;

/// <summary><see cref="IUserDirectory"/> over <c>opportunity.app_user</c> (V0005).</summary>
public sealed class PostgresUserDirectory(NpgsqlDataSource dataSource) : IUserDirectory
{
    public async Task<Guid> ProvisionAsync(ExternalIdentity identity, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        // Matching is on (issuer, subject) only (ADR-015 D3.3); display attributes follow the IdP.
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO opportunity.app_user
                (user_id, issuer, subject, display_name, email, groups, groups_refreshed_at, last_sign_in_at)
            VALUES (@id, @issuer, @subject, @display_name, @email, @groups, @now, @now)
            ON CONFLICT (issuer, subject) DO UPDATE SET
                display_name = EXCLUDED.display_name,
                email = EXCLUDED.email,
                groups = EXCLUDED.groups,
                groups_refreshed_at = EXCLUDED.groups_refreshed_at,
                last_sign_in_at = EXCLUDED.last_sign_in_at
            RETURNING user_id
            """);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("issuer", identity.Issuer);
        command.Parameters.AddWithValue("subject", identity.Subject);
        command.Parameters.AddWithValue("display_name", (object?)identity.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("email", (object?)identity.Email ?? DBNull.Value);
        command.Parameters.AddWithValue("groups", identity.Groups.ToArray());
        command.Parameters.AddWithValue("now", now);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task UpdateGroupsAsync(Guid userId, IReadOnlyList<string> groups, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groups);

        await using var command = dataSource.CreateCommand(
            "UPDATE opportunity.app_user SET groups = @groups, groups_refreshed_at = @now WHERE user_id = @id");
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("groups", groups.ToArray());
        command.Parameters.AddWithValue("now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
