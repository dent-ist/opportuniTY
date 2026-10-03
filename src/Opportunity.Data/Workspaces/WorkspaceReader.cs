using Npgsql;

using Opportunity.Application.Workspaces;
using Opportunity.Core.Workspaces;

namespace Opportunity.Data.Workspaces;

public sealed class WorkspaceReader(NpgsqlDataSource dataSource) : IWorkspaceReader
{
    public async Task<Workspace?> GetAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command("""
            SELECT name, matter_number, display_time_zone, status, closed_at, epoch, control_number_case_sensitive, created_at, updated_at
            FROM opportunity.workspace WHERE workspace_id = @ws
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        Workspace? workspace = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                workspace = new Workspace
                {
                    WorkspaceId = workspaceId,
                    Name = reader.GetString(0),
                    MatterNumber = reader.IsDBNull(1) ? null : reader.GetString(1),
                    DisplayTimeZone = reader.GetString(2),
                    Status = Enum.Parse<WorkspaceStatus>(reader.GetString(3)),
                    ClosedAt = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                    Epoch = reader.GetInt64(5),
                    ControlNumberCaseSensitive = reader.GetBoolean(6),
                    CreatedAt = reader.GetFieldValue<DateTimeOffset>(7),
                    UpdatedAt = reader.GetFieldValue<DateTimeOffset>(8),
                };
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return workspace;
    }
}
