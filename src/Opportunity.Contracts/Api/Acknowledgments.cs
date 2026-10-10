namespace Opportunity.Contracts.Api;

/// <summary>
/// <c>GET …/acknowledgment</c>: the workspace's current acknowledgment text (reviewer attestation or protective-order
/// undertaking, E20-T03) and whether the caller accepted it. Reachable before accepting; every other workspace route
/// answers 403 <c>acknowledgment-required</c> while <see cref="Required"/> is true and <see cref="Acknowledged"/> false.
/// </summary>
/// <param name="Required">True when the workspace publishes acknowledgment text; the other text fields are then set.</param>
/// <param name="TextSha256">SHA-256 (lower-case hex) of the UTF-8 text <c>title + "\n\n" + text</c>; send it back when accepting.</param>
/// <param name="AcknowledgedAt">When the caller accepted <see cref="Version"/>; null while pending.</param>
public sealed record AcknowledgmentResource(
    bool Required,
    int? Version,
    string? Title,
    string? Text,
    string? TextSha256,
    DateTimeOffset? PublishedAt,
    bool Acknowledged,
    DateTimeOffset? AcknowledgedAt);

/// <summary>Body of <c>POST …/acknowledgment/acceptances</c>: the version and text hash the caller was shown.</summary>
public sealed record AcknowledgmentAcceptanceWrite(int? Version, string? TextSha256);

/// <summary>A recorded acceptance: the version, the hash of the text accepted and when.</summary>
public sealed record AcknowledgmentAcceptanceResource(int Version, string TextSha256, DateTimeOffset AcceptedAt);

/// <summary>A person who published a version: the user id and the name they had at their last sign-in.</summary>
public sealed record AcknowledgmentActor(Guid UserId, string? DisplayName);

/// <summary>One published acknowledgment version. <see cref="Text"/> is set only when one version is read.</summary>
public sealed record AcknowledgmentVersionResource(
    int Version,
    string Title,
    string? Text,
    string TextSha256,
    AcknowledgmentActor PublishedBy,
    DateTimeOffset PublishedAt,
    int AcceptedCount,
    bool IsCurrent);

/// <summary>
/// <c>GET …/acknowledgment-versions</c>: every published version, newest first, without the text. The ETag (and
/// <see cref="CurrentVersion"/>) is the current version number, <c>"0"</c> before the first; publishing sends it as
/// <c>If-Match</c>.
/// </summary>
public sealed record AcknowledgmentVersionList(IReadOnlyList<AcknowledgmentVersionResource> Items, int CurrentVersion);

/// <summary>
/// Body of <c>POST …/acknowledgment-versions</c>: publishes the next version, which every member must accept (again)
/// before using the workspace.
/// </summary>
/// <param name="Title">Required, at most 200 characters on one line, e.g. "Protective order acknowledgment (Exhibit A)".</param>
/// <param name="Text">Required, at most 65,536 characters; line breaks are kept (CRLF becomes LF), leading and trailing space is removed.</param>
public sealed record AcknowledgmentVersionWrite(string? Title, string? Text);

public enum AcknowledgmentRosterStatus
{
    /// <summary>Accepted the current version.</summary>
    Current,

    /// <summary>Accepted an earlier version only; must accept again.</summary>
    Outdated,

    /// <summary>Accepted no version.</summary>
    Pending,
}

/// <summary>
/// One person on the acknowledgment roster: users with a direct role assignment, and anyone who accepted a version
/// (members through IdP groups appear once they accept). <see cref="Acceptances"/> is newest version first.
/// </summary>
public sealed record AcknowledgmentRosterEntryResource(
    Guid UserId,
    string? DisplayName,
    string? Email,
    bool DirectMember,
    AcknowledgmentRosterStatus Status,
    IReadOnlyList<AcknowledgmentAcceptanceResource> Acceptances);
