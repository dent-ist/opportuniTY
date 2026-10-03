using Npgsql;

using Opportunity.Application.Identity;

namespace Opportunity.Data.Identity;

/// <summary>Reads <c>workspace.require_mfa</c> (V0005) inside the workspace's transaction context (RLS-ready).</summary>
public sealed class PostgresWorkspaceAuthenticationPolicy(NpgsqlDataSource dataSource) : IWorkspaceAuthenticationPolicy
{
    public async Task<bool> RequiresMfaAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command("SELECT require_mfa FROM opportunity.workspace WHERE workspace_id = @ws");
        command.Parameters.AddWithValue("ws", workspaceId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result is true;
    }
}
