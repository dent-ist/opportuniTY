using Npgsql;

using Opportunity.Application.Identity;

namespace Opportunity.Data.Identity;

/// <summary>
/// <see cref="ISessionStore"/> over <c>opportunity.user_session</c> (V0005). One indexed read per request
/// (ADR-015 consequences: "session state in PG adds a read per request"); writes are throttled by the caller.
/// </summary>
public sealed class PostgresSessionStore(NpgsqlDataSource dataSource) : ISessionStore
{
    private const string Columns =
        """
        session_id, user_id, issuer, subject, idp_session_id, display_name, email, groups, acr, amr, created_at,
        last_seen_at, absolute_expires_at, principal_refreshed_at, tokens
        """;

    public async Task CreateAsync(ReadOnlyMemory<byte> keyHash, UserSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        await using var command = dataSource.CreateCommand(
            $"""
            INSERT INTO opportunity.user_session (key_hash, {Columns})
            VALUES (@key_hash, @session_id, @user_id, @issuer, @subject, @idp_session_id, @display_name, @email, @groups,
                    @acr, @amr, @created_at, @last_seen_at, @absolute_expires_at, @principal_refreshed_at, @tokens)
            """);
        command.Parameters.AddWithValue("key_hash", keyHash.ToArray());
        command.Parameters.AddWithValue("session_id", session.SessionId);
        command.Parameters.AddWithValue("user_id", session.UserId);
        command.Parameters.AddWithValue("issuer", session.Issuer);
        command.Parameters.AddWithValue("subject", session.Subject);
        command.Parameters.AddWithValue("idp_session_id", (object?)session.IdpSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("display_name", (object?)session.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("email", (object?)session.Email ?? DBNull.Value);
        command.Parameters.AddWithValue("groups", session.Groups.ToArray());
        command.Parameters.AddWithValue("acr", (object?)session.Acr ?? DBNull.Value);
        command.Parameters.AddWithValue("amr", session.Amr.ToArray());
        command.Parameters.AddWithValue("created_at", session.CreatedAt);
        command.Parameters.AddWithValue("last_seen_at", session.LastSeenAt);
        command.Parameters.AddWithValue("absolute_expires_at", session.AbsoluteExpiresAt);
        command.Parameters.AddWithValue("principal_refreshed_at", session.PrincipalRefreshedAt);
        command.Parameters.AddWithValue("tokens", session.ProtectedTokens.ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<UserSession?> FindActiveAsync(ReadOnlyMemory<byte> keyHash, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT {Columns} FROM opportunity.user_session WHERE key_hash = @key_hash AND revoked_at IS NULL");
        command.Parameters.AddWithValue("key_hash", keyHash.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task TouchAsync(Guid sessionId, DateTimeOffset lastSeenAt, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE opportunity.user_session SET last_seen_at = @at
            WHERE session_id = @id AND revoked_at IS NULL AND last_seen_at < @at
            """);
        command.Parameters.AddWithValue("id", sessionId);
        command.Parameters.AddWithValue("at", lastSeenAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdatePrincipalAsync(
        Guid sessionId,
        IReadOnlyList<string> groups,
        ReadOnlyMemory<byte> protectedTokens,
        DateTimeOffset refreshedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groups);

        await using var command = dataSource.CreateCommand(
            """
            UPDATE opportunity.user_session SET groups = @groups, tokens = @tokens, principal_refreshed_at = @at
            WHERE session_id = @id AND revoked_at IS NULL
            """);
        command.Parameters.AddWithValue("id", sessionId);
        command.Parameters.AddWithValue("groups", groups.ToArray());
        command.Parameters.AddWithValue("tokens", protectedTokens.ToArray());
        command.Parameters.AddWithValue("at", refreshedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RevokeAsync(Guid sessionId, SessionEndReason reason, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE opportunity.user_session SET revoked_at = @at, revoked_reason = @reason, tokens = ''::bytea
            WHERE session_id = @id AND revoked_at IS NULL
            """);
        command.Parameters.AddWithValue("id", sessionId);
        command.Parameters.AddWithValue("at", at);
        command.Parameters.AddWithValue("reason", reason.ToString());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public Task<IReadOnlyList<UserSession>> RevokeByIdpSessionAsync(
        string issuer, string idpSessionId, SessionEndReason reason, DateTimeOffset at, CancellationToken cancellationToken = default) =>
        RevokeWhereAsync("issuer = @issuer AND idp_session_id = @value", issuer, idpSessionId, reason, at, cancellationToken);

    public Task<IReadOnlyList<UserSession>> RevokeBySubjectAsync(
        string issuer, string subject, SessionEndReason reason, DateTimeOffset at, CancellationToken cancellationToken = default) =>
        RevokeWhereAsync("issuer = @issuer AND subject = @value", issuer, subject, reason, at, cancellationToken);

    public async Task<int> DeleteEndedAsync(DateTimeOffset endedBefore, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            "DELETE FROM opportunity.user_session WHERE revoked_at < @before OR absolute_expires_at < @before");
        command.Parameters.AddWithValue("before", endedBefore);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<UserSession>> RevokeWhereAsync(
        string predicate, string issuer, string value, SessionEndReason reason, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(issuer);
        ArgumentException.ThrowIfNullOrEmpty(value);

        // RETURNING sees the new row, so the token column is already cleared in the result.
#pragma warning disable CA2100 // predicate is one of two constants above.
        await using var command = dataSource.CreateCommand(
            $"""
            UPDATE opportunity.user_session SET revoked_at = @at, revoked_reason = @reason, tokens = ''::bytea
            WHERE {predicate} AND revoked_at IS NULL
            RETURNING {Columns}
            """);
#pragma warning restore CA2100
        command.Parameters.AddWithValue("issuer", issuer);
        command.Parameters.AddWithValue("value", value);
        command.Parameters.AddWithValue("at", at);
        command.Parameters.AddWithValue("reason", reason.ToString());

        var revoked = new List<UserSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            revoked.Add(Read(reader));
        }

        return revoked;
    }

    private static UserSession Read(NpgsqlDataReader reader) => new(
        SessionId: reader.GetGuid(0),
        UserId: reader.GetGuid(1),
        Issuer: reader.GetString(2),
        Subject: reader.GetString(3),
        IdpSessionId: reader.IsDBNull(4) ? null : reader.GetString(4),
        DisplayName: reader.IsDBNull(5) ? null : reader.GetString(5),
        Email: reader.IsDBNull(6) ? null : reader.GetString(6),
        Groups: reader.GetFieldValue<string[]>(7),
        Acr: reader.IsDBNull(8) ? null : reader.GetString(8),
        Amr: reader.GetFieldValue<string[]>(9),
        CreatedAt: reader.GetFieldValue<DateTimeOffset>(10),
        LastSeenAt: reader.GetFieldValue<DateTimeOffset>(11),
        AbsoluteExpiresAt: reader.GetFieldValue<DateTimeOffset>(12),
        PrincipalRefreshedAt: reader.GetFieldValue<DateTimeOffset>(13),
        ProtectedTokens: reader.GetFieldValue<byte[]>(14));
}
