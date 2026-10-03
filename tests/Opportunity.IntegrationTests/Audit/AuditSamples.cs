using System.Security.Cryptography;

using AwesomeAssertions;

using Npgsql;

using Opportunity.Application.Audit;
using Opportunity.Data;
using Opportunity.Data.Audit;
using Opportunity.IntegrationTests.Documents;

namespace Opportunity.IntegrationTests.Audit;

internal static class AuditSamples
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A fully populated event (every optional envelope field set), truncated to the database's µs precision.</summary>
    public static AuditEvent Event(Guid? workspaceId) => new()
    {
        WorkspaceId = workspaceId,
        OccurredAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        Category = AuditTaxonomy.Auth.Category,
        Action = AuditTaxonomy.Auth.SignIn,
        ActorType = AuditActorType.User,
        ActorId = Guid.CreateVersion7().ToString(),
        ActorDisplay = "Rev Iewer",
        OnBehalfOf = Guid.CreateVersion7(),
        AccessPath = AuditAccessPath.Normal,
        ClientIp = "203.0.113.7",
        UserAgent = "Mozilla/5.0 (test)",
        SessionIdHash = SHA256.HashData("session"u8.ToArray()),
        ResourceType = "User",
        ResourceId = "u-1",
        Outcome = AuditOutcome.Success,
        CorrelationId = "corr-" + Guid.CreateVersion7().ToString("N"),
        CausationId = "cause-1",
        JobId = Guid.CreateVersion7(),
        ChunkSequence = 3,
        SnapshotId = Guid.CreateVersion7(),
        SearchGeneration = 42,
        Details = new Dictionary<string, string?> { ["Idp"] = "keycloak", ["Acr"] = null },
    };

    /// <summary>The events of one chain, read through the store as the app role.</summary>
    public static async Task<IReadOnlyList<StoredAuditEvent>> ReadAllAsync(CoreSchemaDatabase db, Guid? workspaceId, bool restricted = true)
    {
        await using var tx = workspaceId is { } ws
            ? await WorkspaceTransaction.BeginAsync(db.AppDataSource, ws, Ct)
            : await WorkspaceTransaction.BeginInstallationAsync(db.AppDataSource, Ct);
        var page = await AuditSql.QueryAsync(tx, new AuditQuery(workspaceId) { IncludeRestrictedDetails = restricted, Limit = 1000 }, Ct);
        await tx.CommitAsync(Ct);
        return page.Events;
    }

    /// <summary>Runs <paramref name="sql"/> as the app role in a workspace transaction and returns the error it must raise.</summary>
    public static async Task<PostgresException> FailsInWorkspaceAsync(NpgsqlDataSource dataSource, Guid workspaceId, string sql)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, Ct);
        await using var command = tx.Command(sql);
        var act = () => command.ExecuteNonQueryAsync(Ct);
        return (await act.Should().ThrowAsync<PostgresException>(sql)).Which;
    }
}
