using Opportunity.Core.Workspaces;

namespace Opportunity.Application.Workspaces;

/// <summary>Reads the workspace registry. Callers authorize first (PEP-1); this port does not check access.</summary>
public interface IWorkspaceReader
{
    Task<Workspace?> GetAsync(Guid workspaceId, CancellationToken cancellationToken = default);
}
