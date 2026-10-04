namespace Opportunity.Application.Identity;

/// <summary>Installation-level user records, keyed by (<c>iss</c>, <c>sub</c>) (ADR-015 D3.3, D3.4).</summary>
public interface IUserDirectory
{
    /// <summary>
    /// Creates the user on first sign-in (with no workspace access) or updates display attributes, and stores the
    /// group snapshot with its refresh time. Returns the opportuniTY user ID.
    /// </summary>
    Task<Guid> ProvisionAsync(ExternalIdentity identity, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Replaces the group snapshot after a principal refresh.</summary>
    Task UpdateGroupsAsync(Guid userId, IReadOnlyList<string> groups, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Display names of known users (absent or unnamed users are omitted), e.g. the last editor of a document.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);
}
