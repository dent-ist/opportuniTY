namespace Opportunity.Contracts.Api;

/// <summary>
/// A workspace deletion (E20-T02, ADR-014 §3-§4): the request, its approval by a second person and the fenced run
/// that purges the workspace and certifies the destruction. Served at installation level
/// (<c>/api/v1/workspace-deletions/{deletionId}</c>) so the requester and approvers can follow it after the workspace
/// itself is gone. The version is the ETag and the If-Match of approve and cancel.
/// </summary>
/// <param name="Status">requested, approved (waiting for <paramref name="RunNotBefore"/>), running, halted (a legal hold
/// stopped the run; it resumes once every hold is released), completed, completedWithResiduals, cancelled or expired.</param>
/// <param name="CurrentStep">The step a running or halted run is at.</param>
/// <param name="Error">Why the run is waiting or retrying (a legal hold, a store that failed), when it is.</param>
/// <param name="CanApprove">The caller may approve it now (approval permission, not the requester, still requested).</param>
/// <param name="CanCancel">The caller may still cancel it (requested or approved, not started).</param>
public sealed record WorkspaceDeletionResource(
    Guid DeletionId,
    Guid WorkspaceId,
    string WorkspaceName,
    string? MatterNumber,
    DeletionRetentionProfileResource RetentionProfile,
    string Reason,
    string? ExternalReference,
    WorkspaceDeletionStatusResource Status,
    DeletionStepResource? CurrentStep,
    DeletionActor RequestedBy,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt,
    DeletionActor? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    string? ApprovalNote,
    DateTimeOffset? RunNotBefore,
    DeletionActor? CancelledBy,
    DateTimeOffset? CancelledAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? HaltedAt,
    string? Error,
    IReadOnlyList<WorkspaceDeletionStepProgress> Steps,
    bool CertificateAvailable,
    bool CanApprove,
    bool CanCancel,
    long Version);

/// <summary>A person who acted on a deletion: the user id and the name they had at their last sign-in.</summary>
public sealed record DeletionActor(Guid UserId, string? DisplayName);

/// <summary>One step of the run with its totals (counts only; the certificate has the per-store detail).</summary>
/// <param name="Outcome">success, residuals (verification found data left), halted or failed; null while running.</param>
/// <param name="Totals">Headline counts of the step, e.g. rows, documents, objects, bytes, jobsCancelled, keysDestroyed.</param>
public sealed record WorkspaceDeletionStepProgress(
    DeletionStepResource Step,
    int Attempt,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? Outcome,
    IReadOnlyDictionary<string, long> Totals);

/// <summary>What a deletion keeps besides the audit trail and the certificate (ADR-014 §5, Q-23).</summary>
public enum DeletionRetentionProfileResource
{
    /// <summary>Default: keep productions with their members, Bates ranges, volume outputs and source snapshots.</summary>
    RetainRecords,

    /// <summary>Remove everything of the workspace and destroy its data keys (crypto-shredding).</summary>
    PurgeAll,
}

public enum WorkspaceDeletionStatusResource
{
    Requested,
    Approved,
    Running,
    Halted,
    Completed,
    CompletedWithResiduals,
    Cancelled,
    Expired,
}

/// <summary>The run's steps in order (ADR-014 §4).</summary>
public enum DeletionStepResource
{
    Fence,
    Drain,
    Inventory,
    SearchPurge,
    DatabasePurge,
    StoragePurge,
    KeyDestruction,
    Verification,
    Certification,
}

/// <summary>A list of deletions, newest first.</summary>
public sealed record WorkspaceDeletionList(IReadOnlyList<WorkspaceDeletionResource> Items);

/// <summary>Body of <c>POST /api/v1/workspaces/{workspaceId}/deletions</c>. Text is trimmed.</summary>
/// <param name="RetentionProfile">Default retainRecords (Q-23).</param>
/// <param name="Reason">Required, 1 to 2,000 characters: why the workspace is deleted (end of matter, protective order).</param>
/// <param name="ExternalReference">Optional, at most 200 characters on one line: order or protective-order paragraph.</param>
/// <param name="ConfirmName">Required: the workspace name exactly, as typed by the requester.</param>
public sealed record WorkspaceDeletionWrite(
    DeletionRetentionProfileResource? RetentionProfile, string? Reason, string? ExternalReference = null, string? ConfirmName = null);

/// <summary>Body of <c>POST /api/v1/workspace-deletions/{deletionId}/approve</c>.</summary>
/// <param name="Note">Optional, at most 2,000 characters; goes on the certificate.</param>
public sealed record WorkspaceDeletionApprovalWrite(string? Note = null);

/// <summary>
/// <c>GET /api/v1/workspace-deletions/{deletionId}/certificate</c>: the destruction certificate (ADR-014 §8) to read
/// (<paramref name="Certificate"/>), its canonical text exactly as stored in object storage
/// (<paramref name="Canonical"/>; <paramref name="Sha256"/> is the SHA-256 of its UTF-8 bytes), and its signature with the
/// audit checkpoint key when the installation has one.
/// </summary>
public sealed record DestructionCertificateDocument(
    System.Text.Json.Nodes.JsonObject Certificate,
    string Canonical,
    string Sha256,
    DateTimeOffset IssuedAt,
    DestructionCertificateSignature? Signature);

/// <param name="KeyId">The signing key version, e.g. <c>audit-checkpoint-v1</c>.</param>
/// <param name="Algorithm">ES256 (ECDSA P-256, IEEE P1363 signature) over the UTF-8 bytes of the canonical text.</param>
/// <param name="Value">Base64 signature.</param>
public sealed record DestructionCertificateSignature(string KeyId, string Algorithm, string Value);
