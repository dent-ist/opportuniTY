using System.Diagnostics;
using System.Globalization;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;

namespace Opportunity.Application.Workspaces;

public enum AcknowledgmentStatus
{
    Ok,
    Invalid,

    /// <summary>Publishing: the text was based on a version that is no longer current (412).</summary>
    VersionConflict,

    /// <summary>Accepting: the version is not current or its hash differs from the one shown (409).</summary>
    Outdated,

    /// <summary>Accepting: the workspace publishes no acknowledgment text (409).</summary>
    NotRequired,
}

public sealed record AcknowledgmentOutcome(AcknowledgmentStatus Status)
{
    public AcknowledgmentVersion? Version { get; init; }

    public AcknowledgmentAcceptance? Acceptance { get; init; }

    /// <summary>Accepting: true when this call recorded the acceptance, false when it already existed.</summary>
    public bool Created { get; init; }

    public int CurrentVersion { get; init; }

    public IReadOnlyDictionary<string, string[]> Errors { get; init; } = new Dictionary<string, string[]>();
}

/// <summary>What a member sees on the acknowledgment page: the current version (if any) and their acceptance of it.</summary>
public sealed record AcknowledgmentState(AcknowledgmentVersion? Current, AcknowledgmentAcceptance? Acceptance)
{
    public bool Pending => Current is not null && Acceptance is null;
}

/// <summary>
/// Reviewer attestation and protective-order acknowledgment (E20-T03, legal finding 14). Administrators with
/// <c>Workspace.ManageAcknowledgments</c> publish numbered text versions; every member must accept the current version
/// before PEP-1 lets them reach workspace content, and a new version requires accepting again. An acceptance stores the
/// version, the SHA-256 of the text and the time, and is audited as <c>Security.AcknowledgmentAccepted</c> in the same
/// transaction; publishing is <c>Security.AcknowledgmentPublished</c>. The text itself never goes to audit (ADR-013 §7).
/// </summary>
public sealed class AcknowledgmentService(IAcknowledgmentStore store, TimeProvider time)
{
    private const string CorrelationTag = "opportunity.correlation_id";

    public async Task<AcknowledgmentState> GetStateAsync(SecurityPrincipal caller, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var current = await store.GetCurrentAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var acceptance = current is null
            ? null
            : await store.GetAcceptanceAsync(workspaceId, caller.UserId, current.Version, cancellationToken).ConfigureAwait(false);
        return new AcknowledgmentState(current, acceptance);
    }

    public Task<IReadOnlyList<AcknowledgmentVersion>> ListVersionsAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
        store.ListVersionsAsync(workspaceId, cancellationToken);

    public Task<AcknowledgmentVersion?> GetVersionAsync(Guid workspaceId, int version, CancellationToken cancellationToken = default) =>
        version < 1 ? Task.FromResult<AcknowledgmentVersion?>(null) : store.GetVersionAsync(workspaceId, version, cancellationToken);

    public Task<AcknowledgmentRosterPage> ListRosterAsync(
        Guid workspaceId, AcknowledgmentRosterPosition? after, int limit, CancellationToken cancellationToken = default) =>
        store.ListRosterAsync(workspaceId, after, limit, cancellationToken);

    public Task<IReadOnlyList<AcknowledgmentRosterEntry>> ExportRosterAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
        store.ExportRosterAsync(workspaceId, cancellationToken);

    /// <summary>Publishes a new version based on <paramref name="expectedCurrentVersion"/> (0 when none exists yet).</summary>
    public async Task<AcknowledgmentOutcome> PublishAsync(
        SecurityPrincipal caller, Guid workspaceId, int expectedCurrentVersion, string? title, string? body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var errors = new Dictionary<string, string[]>();
        var cleanTitle = title?.Trim();
        if (string.IsNullOrEmpty(cleanTitle))
        {
            errors["title"] = ["title is required."];
        }
        else if (cleanTitle.Length > AcknowledgmentText.MaxTitleLength || cleanTitle.Any(char.IsControl))
        {
            errors["title"] = [$"title: at most {AcknowledgmentText.MaxTitleLength} characters on one line."];
        }

        var cleanBody = body is null ? null : AcknowledgmentText.NormalizeBody(body);
        if (string.IsNullOrEmpty(cleanBody))
        {
            errors["text"] = ["text is required."];
        }
        else if (cleanBody.Length > AcknowledgmentText.MaxBodyLength || cleanBody.Any(c => char.IsControl(c) && c is not '\n' and not '\t'))
        {
            errors["text"] = [$"text: at most {AcknowledgmentText.MaxBodyLength} characters, without control characters other than line breaks and tabs."];
        }

        if (errors.Count > 0)
        {
            return new AcknowledgmentOutcome(AcknowledgmentStatus.Invalid) { Errors = errors };
        }

        var hash = AcknowledgmentText.Sha256Hex(cleanTitle!, cleanBody!);
        var now = time.GetUtcNow();
        var audit = Audit(caller, workspaceId, AuditTaxonomy.Security.AcknowledgmentPublished, new Dictionary<string, string?>
        {
            ["version"] = (expectedCurrentVersion + 1).ToString(CultureInfo.InvariantCulture),
            ["previousVersion"] = expectedCurrentVersion == 0 ? null : expectedCurrentVersion.ToString(CultureInfo.InvariantCulture),
            ["textSha256"] = hash,
        });
        var result = await store.PublishAsync(workspaceId, expectedCurrentVersion, cleanTitle!, cleanBody!, hash, caller.UserId, now, audit, cancellationToken)
            .ConfigureAwait(false);
        return result.Outcome == AcknowledgmentPublishOutcome.Ok
            ? new AcknowledgmentOutcome(AcknowledgmentStatus.Ok) { Version = result.Version, CurrentVersion = result.CurrentVersion }
            : new AcknowledgmentOutcome(AcknowledgmentStatus.VersionConflict) { CurrentVersion = result.CurrentVersion };
    }

    /// <summary>
    /// Records that the caller accepts <paramref name="version"/>, whose text they were shown with hash
    /// <paramref name="textSha256"/>. Accepting the same version again changes nothing.
    /// </summary>
    public async Task<AcknowledgmentOutcome> AcceptAsync(
        SecurityPrincipal caller, Guid workspaceId, int? version, string? textSha256, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var errors = new Dictionary<string, string[]>();
        if (version is not >= 1)
        {
            errors["version"] = ["version is required: the version of the text that was shown."];
        }

        var hash = textSha256?.Trim().ToLowerInvariant();
        if (hash is not { Length: 64 } || !hash.All(char.IsAsciiHexDigitLower))
        {
            errors["textSha256"] = ["textSha256 is required: the SHA-256 (64 hex digits) of the text that was shown."];
        }

        if (errors.Count > 0)
        {
            return new AcknowledgmentOutcome(AcknowledgmentStatus.Invalid) { Errors = errors };
        }

        var audit = Audit(caller, workspaceId, AuditTaxonomy.Security.AcknowledgmentAccepted, new Dictionary<string, string?>
        {
            ["version"] = version!.Value.ToString(CultureInfo.InvariantCulture),
            ["textSha256"] = hash,
        });
        var result = await store.AcceptAsync(workspaceId, caller.UserId, version.Value, hash!, time.GetUtcNow(), audit, cancellationToken)
            .ConfigureAwait(false);
        return result.Outcome switch
        {
            AcknowledgmentAcceptOutcome.Created => new AcknowledgmentOutcome(AcknowledgmentStatus.Ok) { Acceptance = result.Acceptance, Created = true },
            AcknowledgmentAcceptOutcome.AlreadyAccepted => new AcknowledgmentOutcome(AcknowledgmentStatus.Ok) { Acceptance = result.Acceptance },
            AcknowledgmentAcceptOutcome.NotRequired => new AcknowledgmentOutcome(AcknowledgmentStatus.NotRequired),
            _ => new AcknowledgmentOutcome(AcknowledgmentStatus.Outdated),
        };
    }

    /// <summary>The <c>Security.AcknowledgmentRosterExported</c> event the CSV export writes before its first byte.</summary>
    public AuditEvent RosterExportedEvent(SecurityPrincipal caller, Guid workspaceId, int rows, int members) =>
        Audit(caller, workspaceId, AuditTaxonomy.Security.AcknowledgmentRosterExported, new Dictionary<string, string?>
        {
            ["rows"] = rows.ToString(CultureInfo.InvariantCulture),
            ["members"] = members.ToString(CultureInfo.InvariantCulture),
        });

    private AuditEvent Audit(SecurityPrincipal caller, Guid workspaceId, string action, IReadOnlyDictionary<string, string?> details)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return new AuditEvent
        {
            WorkspaceId = workspaceId,
            OccurredAt = time.GetUtcNow(),
            Category = AuditTaxonomy.Security.Category,
            Action = action,
            ActorType = AuditActorType.User,
            ActorId = caller.UserId.ToString(),
            ActorDisplay = caller.DisplayName.Length > AuditEventRules.MaxActorDisplayLength
                ? caller.DisplayName[..AuditEventRules.MaxActorDisplayLength]
                : caller.DisplayName,
            ClientIp = caller.ClientIp,
            UserAgent = caller.UserAgent,
            ResourceType = AuditTaxonomy.Security.AcknowledgmentResourceType,
            ResourceId = workspaceId.ToString(),
            Outcome = AuditOutcome.Success,
            CorrelationId = caller.CorrelationId ?? Activity.Current?.GetTagItem(CorrelationTag) as string,
            Details = details,
        };
    }
}
