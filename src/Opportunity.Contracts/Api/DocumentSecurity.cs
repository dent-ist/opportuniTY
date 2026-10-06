namespace Opportunity.Contracts.Api;

/// <summary>A choice of a security-affecting coding field (by id, like coding requests).</summary>
public sealed record SecurityChoiceRef(int FieldId, int ChoiceId);

/// <summary>
/// A restriction class of the workspace (Q-11, ADR-015 D6.1): documents carrying it are visible only to the roles in
/// <see cref="Roles"/>. A document carries the class while it is coded with any choice in <see cref="Rules"/>.
/// </summary>
/// <param name="ClassKey">Stable key, e.g. <c>AttorneysEyesOnly</c>.</param>
/// <param name="Roles">Role keys (e.g. <c>Reviewer</c>) that may see documents of the class.</param>
/// <param name="Version">Optimistic-concurrency version; also the ETag.</param>
public sealed record RestrictionClassResource(
    string ClassKey,
    string DisplayName,
    bool IsBuiltIn,
    IReadOnlyList<string> Roles,
    IReadOnlyList<SecurityChoiceRef> Rules,
    DateTimeOffset UpdatedAt,
    long Version);

/// <summary><c>GET …/security/restriction-classes</c>.</summary>
public sealed record RestrictionClassList(IReadOnlyList<RestrictionClassResource> Items);

/// <summary>Body of <c>PUT …/security/restriction-classes/{classKey}</c> (create or replace).</summary>
/// <param name="DisplayName">1–100 characters.</param>
/// <param name="Roles">Role keys that may see the class (Break-glass is never granted a class).</param>
/// <param name="Rules">At most 100 choices of security-affecting privilege or confidentiality coding fields.</param>
public sealed record RestrictionClassRequest(string? DisplayName, IReadOnlyList<string>? Roles, IReadOnlyList<SecurityChoiceRef>? Rules);

/// <summary>Who an ethical wall keeps out: users by id and IdP groups by name.</summary>
public sealed record EthicalWallMembers(IReadOnlyList<Guid> UserIds, IReadOnlyList<string> Groups);

/// <summary>
/// What an ethical wall covers: explicit documents, custodians (matched case-insensitively against the custodian
/// fields, All Custodians included) and choices of security-affecting wall fields. A covered document is invisible
/// to every member on every path, whatever their roles (Q-13).
/// </summary>
public sealed record EthicalWallScope(IReadOnlyList<Guid> DocumentIds, IReadOnlyList<string> Custodians, IReadOnlyList<SecurityChoiceRef> Choices);

/// <summary>An ethical wall (Q-13, ADR-015 D6.2).</summary>
/// <param name="Version">Optimistic-concurrency version; also the ETag (<c>If-Match</c> on replace and delete).</param>
public sealed record EthicalWallResource(
    Guid WallId,
    string Name,
    string? Description,
    EthicalWallMembers Members,
    EthicalWallScope Scope,
    DateTimeOffset UpdatedAt,
    long Version);

/// <summary><c>GET …/security/walls</c>.</summary>
public sealed record EthicalWallList(IReadOnlyList<EthicalWallResource> Items);

/// <summary>Body of <c>POST</c> (create) and <c>PUT</c> (replace) <c>…/security/walls</c>.</summary>
/// <param name="Name">1–200 characters, unique in the workspace.</param>
/// <param name="Members">Up to 1,000 users and 1,000 groups.</param>
/// <param name="Scope">Up to 10,000 documents, 1,000 custodians and 100 choices.</param>
public sealed record EthicalWallRequest(string? Name, string? Description, EthicalWallMembers? Members, EthicalWallScope? Scope);

/// <summary>
/// A field-level restriction (E05-T06): the custom field is hidden from principals holding none of
/// <see cref="VisibleTo"/> and read-only for those holding none of <see cref="EditableBy"/>.
/// </summary>
public sealed record FieldRestrictionResource(int FieldId, string FieldName, IReadOnlyList<string> VisibleTo, IReadOnlyList<string> EditableBy,
    DateTimeOffset UpdatedAt, long Version);

/// <summary><c>GET …/security/field-restrictions</c>.</summary>
public sealed record FieldRestrictionList(IReadOnlyList<FieldRestrictionResource> Items);

/// <summary>Body of <c>PUT …/security/field-restrictions/{fieldId}</c>.</summary>
/// <param name="VisibleTo">At least one role key.</param>
/// <param name="EditableBy">Role keys, each also in <see cref="VisibleTo"/>.</param>
public sealed record FieldRestrictionRequest(IReadOnlyList<string>? VisibleTo, IReadOnlyList<string>? EditableBy);

/// <summary>Body of <c>POST …/security/break-glass/activations</c> (Q-45).</summary>
/// <param name="Reason">Required justification, 1–2,000 characters.</param>
/// <param name="DurationMinutes">1–240; default 60.</param>
public sealed record BreakGlassActivationRequest(string? Reason, int? DurationMinutes = null);

/// <summary>A break-glass activation (Q-45): read-only emergency access, separately audited.</summary>
/// <param name="EndedReason"><c>Ended</c> by the holder or <c>Revoked</c> by an administrator; null while open.</param>
public sealed record BreakGlassActivationResource(
    Guid ActivationId,
    Guid UserId,
    string? UserDisplayName,
    string Reason,
    DateTimeOffset ActivatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? EndedAt,
    string? EndedReason,
    bool Active);

/// <summary><c>GET …/security/break-glass/activations</c>: the break-glass report (Audit.Read) or the caller's own.</summary>
public sealed record BreakGlassActivationList(IReadOnlyList<BreakGlassActivationResource> Items);
