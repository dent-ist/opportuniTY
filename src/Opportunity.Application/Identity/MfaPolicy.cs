namespace Opportunity.Application.Identity;

/// <summary>
/// Which IdP <c>acr</c>/<c>amr</c> values count as multi-factor authentication for this installation (ADR-015 D3.6).
/// MFA is satisfied when the session's <c>acr</c> is one of <see cref="AcrValues"/> or any of its <c>amr</c> values
/// is one of <see cref="AmrValues"/>.
/// </summary>
public sealed record MfaPolicy(IReadOnlyList<string> AcrValues, IReadOnlyList<string> AmrValues)
{
    public bool IsConfigured => AcrValues.Count > 0 || AmrValues.Count > 0;

    public bool IsSatisfiedBy(string? acr, IEnumerable<string> amr)
    {
        ArgumentNullException.ThrowIfNull(amr);
        return (acr is not null && AcrValues.Contains(acr, StringComparer.Ordinal))
            || amr.Any(value => AmrValues.Contains(value, StringComparer.Ordinal));
    }
}

/// <summary>Per-workspace authentication requirements (ADR-015 D3.6: a workspace MAY require MFA).</summary>
public interface IWorkspaceAuthenticationPolicy
{
    /// <summary>True when the workspace requires MFA. Unknown workspaces return false (they 404 elsewhere).</summary>
    Task<bool> RequiresMfaAsync(Guid workspaceId, CancellationToken cancellationToken = default);
}
