using System.Security.Cryptography;
using System.Text;

using Opportunity.Application.Audit;

namespace Opportunity.Application.Workspaces;

/// <summary>
/// One published version of a workspace's acknowledgment text (E20-T03): the reviewer attestation or protective-order
/// undertaking members must accept before they reach workspace content. Versions are numbered from 1 and never change;
/// the highest one is current.
/// </summary>
public sealed record AcknowledgmentVersion
{
    public required Guid WorkspaceId { get; init; }

    public required int Version { get; init; }

    public required string Title { get; init; }

    /// <summary>The text; null in version lists, which carry only the metadata.</summary>
    public string? Body { get; init; }

    /// <summary>SHA-256 (lower-case hex) of <see cref="AcknowledgmentText.Canonical"/>; recomputed by the database.</summary>
    public required string TextSha256 { get; init; }

    public required Guid PublishedBy { get; init; }

    public string? PublishedByName { get; init; }

    public required DateTimeOffset PublishedAt { get; init; }

    /// <summary>How many users accepted this version.</summary>
    public int AcceptedCount { get; init; }
}

/// <summary>A user's acceptance of one version, with the hash of the text accepted and the audit event that records it.</summary>
public sealed record AcknowledgmentAcceptance(
    Guid UserId, int Version, string TextSha256, DateTimeOffset AcceptedAt, Guid AuditEventId);

/// <summary>
/// One person on the roster: a user with a direct role assignment in the workspace, or anyone who accepted a version
/// (members through IdP groups appear once they accept). <see cref="Acceptances"/> is newest version first.
/// </summary>
public sealed record AcknowledgmentRosterEntry(
    Guid UserId, string? DisplayName, string? Email, bool DirectMember, IReadOnlyList<AcknowledgmentAcceptance> Acceptances);

/// <summary>Keyset position of the roster: the sort name (lower case) and the user id.</summary>
public sealed record AcknowledgmentRosterPosition(string SortName, Guid UserId);

public sealed record AcknowledgmentRosterPage(
    IReadOnlyList<AcknowledgmentRosterEntry> Items, AcknowledgmentRosterPosition? Next, int Total);

/// <summary>The canonical text and its hash, shared with the database check (V0058) so either can verify the other.</summary>
public static class AcknowledgmentText
{
    public const int MaxTitleLength = 200;
    public const int MaxBodyLength = 65536;

    /// <summary><c>title</c> LF LF <c>body</c>, with LF line endings, encoded as UTF-8 for hashing.</summary>
    public static string Canonical(string title, string body)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);
        return title + "\n\n" + body;
    }

    public static string Sha256Hex(string title, string body) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(title, body))));

    /// <summary>CRLF and CR become LF; leading and trailing white space is removed.</summary>
    public static string NormalizeBody(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
    }
}

public enum AcknowledgmentPublishOutcome
{
    Ok,

    /// <summary>The current version is not the one the caller based the new text on.</summary>
    VersionConflict,
}

public sealed record AcknowledgmentPublishResult(AcknowledgmentPublishOutcome Outcome, AcknowledgmentVersion? Version, int CurrentVersion);

public enum AcknowledgmentAcceptOutcome
{
    /// <summary>The acceptance was recorded (and audited) now.</summary>
    Created,

    /// <summary>The user had already accepted this version; nothing was written.</summary>
    AlreadyAccepted,

    /// <summary>The version is not the current one, or its text hash differs from the one the user was shown.</summary>
    Outdated,

    /// <summary>The workspace publishes no acknowledgment text.</summary>
    NotRequired,
}

public sealed record AcknowledgmentAcceptResult(AcknowledgmentAcceptOutcome Outcome, AcknowledgmentAcceptance? Acceptance);

/// <summary>
/// PostgreSQL store of acknowledgment versions and acceptances (V0058). Writes lock the workspace row, check the current
/// version and insert their audit event in the same transaction (ADR-013 §2.1). Rows are append-only.
/// </summary>
public interface IAcknowledgmentStore
{
    /// <summary>The current (highest) version with its text, or null when none was published.</summary>
    Task<AcknowledgmentVersion?> GetCurrentAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    Task<AcknowledgmentVersion?> GetVersionAsync(Guid workspaceId, int version, CancellationToken cancellationToken = default);

    /// <summary>Every version without its text, newest first, with acceptance counts.</summary>
    Task<IReadOnlyList<AcknowledgmentVersion>> ListVersionsAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>Publishes version <paramref name="expectedCurrentVersion"/> + 1 (0: the first) unless another version came first.</summary>
    Task<AcknowledgmentPublishResult> PublishAsync(
        Guid workspaceId, int expectedCurrentVersion, string title, string body, string textSha256, Guid publishedBy,
        DateTimeOffset publishedAt, AuditEvent audit, CancellationToken cancellationToken = default);

    Task<AcknowledgmentAcceptance?> GetAcceptanceAsync(Guid workspaceId, Guid userId, int version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the user's acceptance of <paramref name="version"/> when it is still the current version and its hash is
    /// <paramref name="textSha256"/>; <paramref name="audit"/> is written only when a row is inserted.
    /// </summary>
    Task<AcknowledgmentAcceptResult> AcceptAsync(
        Guid workspaceId, Guid userId, int version, string textSha256, DateTimeOffset acceptedAt, AuditEvent audit,
        CancellationToken cancellationToken = default);

    /// <summary>The roster by name, after <paramref name="after"/>.</summary>
    Task<AcknowledgmentRosterPage> ListRosterAsync(
        Guid workspaceId, AcknowledgmentRosterPosition? after, int limit, CancellationToken cancellationToken = default);

    /// <summary>The whole roster by name, for the CSV export.</summary>
    Task<IReadOnlyList<AcknowledgmentRosterEntry>> ExportRosterAsync(Guid workspaceId, CancellationToken cancellationToken = default);
}
