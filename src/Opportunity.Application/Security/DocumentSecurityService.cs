using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Fields;
using Opportunity.Contracts.Api;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;

namespace Opportunity.Application.Security;

public enum SecurityOutcomeStatus
{
    Ok,
    Created,
    NotFound,
    Invalid,
    VersionConflict,
    Conflict,

    /// <summary>ADR-015 D6.5: the change applies to the caller; another administrator must make it.</summary>
    SelfProtection,
}

public sealed record SecurityOutcome<T>(SecurityOutcomeStatus Status, T? Value = default)
    where T : class
{
    public IReadOnlyDictionary<string, string[]> Errors { get; init; } = new Dictionary<string, string[]>();
}

public static class SecurityOutcome
{
    public static SecurityOutcome<T> Invalid<T>(Dictionary<string, string[]> errors)
        where T : class => new(SecurityOutcomeStatus.Invalid) { Errors = errors };
}

/// <summary>
/// Administration of document- and field-level security (E05-T06; ADR-015 D6; Q-11, Q-13, Q-45): restriction classes
/// with their role grants and coding rules, ethical walls, field restrictions and break-glass activations. Validates
/// against the field catalogue, applies the self-protection rule (D6.5: nobody changes a class grant or wall that
/// applies to themselves) and audits every change in its own transaction (the store writes the event).
/// </summary>
public sealed partial class DocumentSecurityService(
    IDocumentSecurityStore store,
    IFieldCatalogRepository fields,
    ISecurityStateReader securityState,
    IAuthorizationService authorization,
    TimeProvider time)
{
    public const int MaxRules = 100;
    public const int MaxWallUsers = 1_000;
    public const int MaxWallGroups = 1_000;
    public const int MaxWallDocuments = 10_000;
    public const int MaxWallCustodians = 1_000;
    public const int MaxWallChoices = 100;
    public static readonly TimeSpan DefaultBreakGlassDuration = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan MaxBreakGlassDuration = TimeSpan.FromHours(4);

    private const string CorrelationTag = "opportunity.correlation_id";
    private const string UnknownDocument = "The document does not exist.";

    // ----- Restriction classes -----------------------------------------------------------------------------------

    public Task<IReadOnlyList<RestrictionClassState>> ListClassesAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
        store.ListClassesAsync(workspaceId, cancellationToken);

    public async Task<SecurityOutcome<RestrictionClassState>> PutClassAsync(
        SecurityPrincipal principal, Guid workspaceId, string classKey, RestrictionClassRequest request, long? ifMatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        var current = (await store.ListClassesAsync(workspaceId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(c => string.Equals(c.ClassKey, classKey, StringComparison.Ordinal));
        if (current is null && !ClassKeyPattern().IsMatch(classKey ?? string.Empty))
        {
            return SecurityOutcome.Invalid<RestrictionClassState>(new() { ["classKey"] = ["A letter followed by up to 63 letters or digits."] });
        }

        if (current is not null && ifMatch is null)
        {
            return new(SecurityOutcomeStatus.VersionConflict);
        }

        var errors = new Dictionary<string, string[]>();
        var displayName = request.DisplayName?.Trim() ?? string.Empty;
        if (displayName.Length is < 1 or > 100)
        {
            errors["displayName"] = ["1–100 characters."];
        }

        var roles = ParseRoles(request.Roles, "roles", errors, allowEmpty: true);
        var catalog = await fields.GetCatalogAsync(workspaceId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var rules = ValidateChoices(catalog, request.Rules, "rules", MaxRules, errors,
            f => f.SecurityClass is SecurityClass.PrivilegeStatus or SecurityClass.ConfidentialityDesignation,
            "a security-affecting privilege or confidentiality coding field");
        if (errors.Count > 0)
        {
            return SecurityOutcome.Invalid<RestrictionClassState>(errors);
        }

        // D6.5: a grant change for a role the caller holds would change what the caller may see.
        var held = await HeldRolesAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false);
        var before = current?.Definition.Roles ?? [];
        if (before.Except(roles).Concat(roles.Except(before)).Any(held.Contains))
        {
            return new(SecurityOutcomeStatus.SelfProtection);
        }

        var definition = new RestrictionClassDefinition(displayName, roles, rules);
        var audit = Event(principal, workspaceId, AuditTaxonomy.Security.RestrictionChanged, "RestrictionClass", classKey!, new()
        {
            ["Change"] = current is null ? "Created" : "Modified",
            ["ClassKey"] = classKey,
            ["Roles"] = string.Join(',', roles.Select(r => r.Key()).Order(StringComparer.Ordinal)),
            ["RolesAdded"] = string.Join(',', roles.Except(before).Select(r => r.Key()).Order(StringComparer.Ordinal)),
            ["RolesRemoved"] = string.Join(',', before.Except(roles).Select(r => r.Key()).Order(StringComparer.Ordinal)),
            ["Rules"] = string.Join(',', rules.Select(r => Invariant(r.FieldId) + ":" + Invariant(r.ChoiceId))),
        });
        var write = await store.PutClassAsync(workspaceId, classKey!, definition, current is null ? null : ifMatch, principal.UserId, audit,
            cancellationToken).ConfigureAwait(false);
        return Map(write);
    }

    public async Task<SecurityOutcome<RestrictionClassState>> DeleteClassAsync(
        SecurityPrincipal principal, Guid workspaceId, string classKey, long ifMatch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var current = (await store.ListClassesAsync(workspaceId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(c => string.Equals(c.ClassKey, classKey, StringComparison.Ordinal));
        if (current is null)
        {
            return new(SecurityOutcomeStatus.NotFound);
        }

        if (current.IsBuiltIn)
        {
            return new(SecurityOutcomeStatus.Conflict);
        }

        var held = await HeldRolesAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false);
        if (current.Definition.Roles.Any(held.Contains))
        {
            return new(SecurityOutcomeStatus.SelfProtection);
        }

        var audit = Event(principal, workspaceId, AuditTaxonomy.Security.RestrictionChanged, "RestrictionClass", classKey, new()
        {
            ["Change"] = "Deleted",
            ["ClassKey"] = classKey,
        });
        return Map(await store.DeleteClassAsync(workspaceId, classKey, ifMatch, audit, cancellationToken).ConfigureAwait(false));
    }

    // ----- Ethical walls -----------------------------------------------------------------------------------------

    /// <summary>Every wall, with explicit documents the caller may not see left out (Q-52: no placeholder, no count).</summary>
    public async Task<IReadOnlyList<EthicalWallState>> ListWallsAsync(SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var walls = await store.ListWallsAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var hidden = await HiddenDocumentsAsync(principal, workspaceId, walls.SelectMany(w => w.Definition.DocumentIds), cancellationToken).ConfigureAwait(false);
        return [.. walls.Select(w => Redact(w, hidden))];
    }

    public async Task<EthicalWallState?> GetWallAsync(SecurityPrincipal principal, Guid workspaceId, Guid wallId, CancellationToken cancellationToken = default)
    {
        if (await store.GetWallAsync(workspaceId, wallId, cancellationToken).ConfigureAwait(false) is not { } wall)
        {
            return null;
        }

        var hidden = await HiddenDocumentsAsync(principal, workspaceId, wall.Definition.DocumentIds, cancellationToken).ConfigureAwait(false);
        return Redact(wall, hidden);
    }

    /// <summary>The wall as stored (nothing left out): for replacing it, never for a response.</summary>
    public Task<EthicalWallState?> GetStoredWallAsync(Guid workspaceId, Guid wallId, CancellationToken cancellationToken = default) =>
        store.GetWallAsync(workspaceId, wallId, cancellationToken);

    public async Task<SecurityOutcome<EthicalWallState>> CreateWallAsync(
        SecurityPrincipal principal, Guid workspaceId, EthicalWallRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var (definition, errors) = await ValidateWallAsync(principal, workspaceId, request, null, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            return SecurityOutcome.Invalid<EthicalWallState>(errors);
        }

        if (AppliesTo(definition, principal))
        {
            return new(SecurityOutcomeStatus.SelfProtection);
        }

        var wallId = Guid.CreateVersion7();
        var audit = WallEvent(principal, workspaceId, AuditTaxonomy.Security.WallCreated, wallId, definition, null);
        var write = await store.CreateWallAsync(workspaceId, wallId, definition, principal.UserId, audit, cancellationToken).ConfigureAwait(false);
        return await MapWallAsync(principal, workspaceId, write, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SecurityOutcome<EthicalWallState>> UpdateWallAsync(
        SecurityPrincipal principal, Guid workspaceId, EthicalWallState current, long ifMatch, EthicalWallRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(current);
        if (AppliesTo(current.Definition, principal))
        {
            return new(SecurityOutcomeStatus.SelfProtection);
        }

        var (definition, errors) = await ValidateWallAsync(principal, workspaceId, request, current, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            return SecurityOutcome.Invalid<EthicalWallState>(errors);
        }

        if (AppliesTo(definition, principal))
        {
            return new(SecurityOutcomeStatus.SelfProtection);
        }

        var audit = WallEvent(principal, workspaceId, AuditTaxonomy.Security.WallChanged, current.WallId, definition, current.Definition);
        var write = await store.UpdateWallAsync(workspaceId, current.WallId, definition, ifMatch, principal.UserId, audit, cancellationToken)
            .ConfigureAwait(false);
        return await MapWallAsync(principal, workspaceId, write, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SecurityOutcome<EthicalWallState>> DeleteWallAsync(
        SecurityPrincipal principal, Guid workspaceId, EthicalWallState current, long ifMatch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(current);
        if (AppliesTo(current.Definition, principal))
        {
            return new(SecurityOutcomeStatus.SelfProtection);
        }

        var audit = WallEvent(principal, workspaceId, AuditTaxonomy.Security.WallDeleted, current.WallId, current.Definition, null);
        return Map(await store.DeleteWallAsync(workspaceId, current.WallId, ifMatch, audit, cancellationToken).ConfigureAwait(false));
    }

    // ----- Field restrictions ------------------------------------------------------------------------------------

    public Task<IReadOnlyList<FieldRestrictionState>> ListFieldRestrictionsAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
        store.ListFieldRestrictionsAsync(workspaceId, cancellationToken);

    public async Task<SecurityOutcome<FieldRestrictionState>> PutFieldRestrictionAsync(
        SecurityPrincipal principal, Guid workspaceId, int fieldId, FieldRestrictionRequest request, long? ifMatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        var catalog = await fields.GetCatalogAsync(workspaceId, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (catalog.Find(fieldId) is not { IsDeleted: false } field)
        {
            return new(SecurityOutcomeStatus.NotFound);
        }

        var errors = new Dictionary<string, string[]>();
        if (field.IsSystem)
        {
            errors["fieldId"] = ["System fields cannot be restricted."];
        }

        var visible = ParseRoles(request.VisibleTo, "visibleTo", errors, allowEmpty: false);
        var editable = ParseRoles(request.EditableBy, "editableBy", errors, allowEmpty: true);
        if (editable.Except(visible).Any())
        {
            errors["editableBy"] = ["Every role that may edit the field must also see it."];
        }

        if (errors.Count > 0)
        {
            return SecurityOutcome.Invalid<FieldRestrictionState>(errors);
        }

        var current = (await store.ListFieldRestrictionsAsync(workspaceId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(r => r.FieldId == fieldId);
        if (current is not null && ifMatch is null)
        {
            return new(SecurityOutcomeStatus.VersionConflict);
        }

        var audit = Event(principal, workspaceId, AuditTaxonomy.Security.PermissionChanged, "FieldRestriction", Invariant(fieldId), new()
        {
            ["Change"] = current is null ? "Created" : "Modified",
            ["FieldId"] = Invariant(fieldId),
            ["VisibleTo"] = string.Join(',', visible.Select(r => r.Key())),
            ["EditableBy"] = string.Join(',', editable.Select(r => r.Key())),
        });
        return Map(await store.PutFieldRestrictionAsync(workspaceId, fieldId, visible, editable, current is null ? null : ifMatch, principal.UserId,
            audit, cancellationToken).ConfigureAwait(false));
    }

    public async Task<SecurityOutcome<FieldRestrictionState>> DeleteFieldRestrictionAsync(
        SecurityPrincipal principal, Guid workspaceId, int fieldId, long ifMatch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var audit = Event(principal, workspaceId, AuditTaxonomy.Security.PermissionChanged, "FieldRestriction", Invariant(fieldId), new()
        {
            ["Change"] = "Deleted",
            ["FieldId"] = Invariant(fieldId),
        });
        return Map(await store.DeleteFieldRestrictionAsync(workspaceId, fieldId, ifMatch, audit, cancellationToken).ConfigureAwait(false));
    }

    // ----- Break-glass -------------------------------------------------------------------------------------------

    /// <summary>
    /// Opens a break-glass activation for the caller (Q-45). The endpoint has already checked the BreakGlass role
    /// assignment and the MFA step-up; the activation and its audit event (access path BreakGlass) commit together.
    /// </summary>
    public async Task<SecurityOutcome<BreakGlassActivationState>> ActivateBreakGlassAsync(
        SecurityPrincipal principal, Guid workspaceId, BreakGlassActivationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>();
        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < 1 or > 2_000)
        {
            errors["reason"] = ["State the reason for emergency access (1–2,000 characters)."];
        }

        var duration = request.DurationMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : DefaultBreakGlassDuration;
        if (duration <= TimeSpan.Zero || duration > MaxBreakGlassDuration)
        {
            errors["durationMinutes"] = ["1–240 minutes."];
        }

        if (errors.Count > 0)
        {
            return SecurityOutcome.Invalid<BreakGlassActivationState>(errors);
        }

        var activationId = Guid.CreateVersion7();
        var audit = Event(principal, workspaceId, AuditTaxonomy.Security.BreakGlassActivated, "BreakGlassActivation", activationId.ToString(), new()
        {
            ["DurationMinutes"] = Invariant((int)duration.TotalMinutes),
            ["Reason"] = reason,
        }, breakGlass: true);
        return Map(await store.ActivateBreakGlassAsync(workspaceId, activationId, principal.UserId, reason, duration, audit, cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>Ends the caller's own activation, or revokes another user's with <c>Workspace.ManageSecurity</c>.</summary>
    public async Task<SecurityOutcome<BreakGlassActivationState>> EndBreakGlassAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid activationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (await store.GetBreakGlassAsync(workspaceId, activationId, cancellationToken).ConfigureAwait(false) is not { } activation)
        {
            return new(SecurityOutcomeStatus.NotFound);
        }

        var own = activation.UserId == principal.UserId;
        if (!own && !await CanManageAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false))
        {
            return new(SecurityOutcomeStatus.NotFound);
        }

        if (activation.EndedAt is not null || activation.ExpiresAt <= time.GetUtcNow())
        {
            return new(SecurityOutcomeStatus.Conflict);
        }

        var endedReason = own ? "Ended" : "Revoked";
        var audit = Event(principal, workspaceId, AuditTaxonomy.Security.BreakGlassEnded, "BreakGlassActivation", activationId.ToString(), new()
        {
            ["EndedReason"] = endedReason,
            ["HolderId"] = activation.UserId.ToString(),
        }, breakGlass: true);
        return Map(await store.EndBreakGlassAsync(workspaceId, activationId, endedReason, audit, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The break-glass report with <c>Audit.Read</c>; otherwise the caller's own activations.</summary>
    public async Task<IReadOnlyList<BreakGlassActivationState>> ListBreakGlassAsync(
        SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var all = await authorization.AuthorizeAsync(principal, workspaceId, Permission.AuditRead, cancellationToken).ConfigureAwait(false);
        return await store.ListBreakGlassAsync(workspaceId, all.IsAllowed ? null : principal.UserId, 500, cancellationToken).ConfigureAwait(false);
    }

    // ----- Helpers -----------------------------------------------------------------------------------------------

    private async Task<bool> CanManageAsync(SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken) =>
        (await authorization.AuthorizeAsync(principal, workspaceId, Permission.WorkspaceManageSecurity, cancellationToken).ConfigureAwait(false)).IsAllowed;

    private async Task<IReadOnlySet<WorkspaceRole>> HeldRolesAsync(SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken)
    {
        var read = await securityState.ReadAsync(workspaceId, principal, includePrincipal: true, null, cancellationToken).ConfigureAwait(false);
        return read.Principal?.Roles ?? new HashSet<WorkspaceRole>();
    }

    private static bool AppliesTo(EthicalWallDefinition wall, SecurityPrincipal principal) =>
        wall.UserIds.Contains(principal.UserId) || wall.Groups.Any(g => principal.Groups.Contains(g, StringComparer.Ordinal));

    /// <summary>Explicit wall documents the caller may not view (they stay in the wall but are never shown).</summary>
    private async Task<IReadOnlySet<Guid>> HiddenDocumentsAsync(
        SecurityPrincipal principal, Guid workspaceId, IEnumerable<Guid> documentIds, CancellationToken cancellationToken)
    {
        var ids = documentIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var decisions = await authorization.AuthorizeManyAsync(principal, workspaceId, Permission.DocumentView, ids, DenialAudit.Summary, cancellationToken)
            .ConfigureAwait(false);
        return decisions.Where(d => !d.Value.IsAllowed).Select(d => d.Key).ToHashSet();
    }

    private static EthicalWallState Redact(EthicalWallState wall, IReadOnlySet<Guid> hidden) =>
        hidden.Count == 0 || !wall.Definition.DocumentIds.Any(hidden.Contains)
            ? wall
            : wall with { Definition = wall.Definition with { DocumentIds = [.. wall.Definition.DocumentIds.Where(d => !hidden.Contains(d))] } };

    private async Task<(EthicalWallDefinition? Definition, Dictionary<string, string[]> Errors)> ValidateWallAsync(
        SecurityPrincipal principal, Guid workspaceId, EthicalWallRequest? request, EthicalWallState? current, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null)
        {
            errors["body"] = ["Send the wall."];
            return (null, errors);
        }

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 200)
        {
            errors["name"] = ["1–200 characters."];
        }

        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description is { Length: > 2_000 })
        {
            errors["description"] = ["At most 2,000 characters."];
        }

        var users = (request.Members?.UserIds ?? []).Distinct().ToList();
        if (users.Count > MaxWallUsers || users.Contains(Guid.Empty))
        {
            errors["members.userIds"] = [$"At most {Invariant(MaxWallUsers)} user ids."];
        }

        var groups = (request.Members?.Groups ?? []).Select(g => g?.Trim() ?? string.Empty).Distinct(StringComparer.Ordinal).ToList();
        if (groups.Count > MaxWallGroups || groups.Any(g => g.Length is < 1 or > 256))
        {
            errors["members.groups"] = [$"At most {Invariant(MaxWallGroups)} group names of 1–256 characters."];
        }

        var custodians = (request.Scope?.Custodians ?? []).Select(c => (c ?? string.Empty).Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        if (custodians.Count > MaxWallCustodians || custodians.Any(c => c.Length is < 1 or > 255))
        {
            errors["scope.custodians"] = [$"At most {Invariant(MaxWallCustodians)} custodians of 1–255 characters."];
        }

        var catalog = await fields.GetCatalogAsync(workspaceId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var choices = ValidateChoices(catalog, request.Scope?.Choices, "scope.choices", MaxWallChoices, errors,
            f => f.SecurityClass == SecurityClass.EthicalWall, "a security-affecting ethical-wall coding field");

        var requested = request.Scope?.DocumentIds ?? [];
        var documents = requested.Distinct().ToList();
        if (documents.Count > MaxWallDocuments)
        {
            errors["scope.documentIds"] = [$"At most {Invariant(MaxWallDocuments)} documents."];
        }
        else if (documents.Count > 0)
        {
            // A document the caller may not see answers exactly like one that does not exist (no oracle).
            var decisions = await authorization.AuthorizeManyAsync(principal, workspaceId, Permission.DocumentView, documents, DenialAudit.Summary,
                cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < requested.Count; i++)
            {
                if (!decisions[requested[i]].IsAllowed)
                {
                    errors[$"scope.documentIds[{Invariant(i)}]"] = [UnknownDocument];
                }
            }
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        // Explicit documents of the current scope the caller cannot see stay in the wall: they were never shown.
        if (current is not null && current.Definition.DocumentIds.Count > 0)
        {
            var hidden = await HiddenDocumentsAsync(principal, workspaceId, current.Definition.DocumentIds, cancellationToken).ConfigureAwait(false);
            documents.AddRange(hidden.Where(h => !documents.Contains(h)).Order());
        }

        return (new EthicalWallDefinition(name, description, users, groups, documents, custodians, choices), errors);
    }

    private static List<SecurityChoiceRef> ValidateChoices(
        FieldCatalog catalog, IReadOnlyList<SecurityChoiceRef>? requested, string key, int max, Dictionary<string, string[]> errors,
        Func<FieldDefinition, bool> fieldKind, string expected)
    {
        var result = new List<SecurityChoiceRef>();
        var list = requested ?? [];
        if (list.Count > max)
        {
            errors[key] = [$"At most {Invariant(max)} choices."];
            return result;
        }

        for (var i = 0; i < list.Count; i++)
        {
            var choice = list[i];
            if (choice is null || catalog.Find(choice.FieldId) is not { IsDeleted: false, IsSecurityAffecting: true, Storage: FieldStorage.Coding } field
                || !field.IsChoice || !fieldKind(field))
            {
                errors[$"{key}[{Invariant(i)}].fieldId"] = [$"Choose {expected}."];
            }
            else if (!catalog.ChoicesOf(field.FieldId).Any(c => c.ChoiceId == choice.ChoiceId))
            {
                errors[$"{key}[{Invariant(i)}].choiceId"] = [$"{field.Name} has no such choice."];
            }
            else if (!result.Contains(choice))
            {
                result.Add(choice);
            }
        }

        return result;
    }

    private static List<WorkspaceRole> ParseRoles(IReadOnlyList<string>? keys, string key, Dictionary<string, string[]> errors, bool allowEmpty)
    {
        var roles = new List<WorkspaceRole>();
        foreach (var name in keys ?? [])
        {
            if (!RoleCatalog.TryParse(name, out var role) || role == WorkspaceRole.BreakGlass)
            {
                errors[key] = [$"Unknown role “{name}”. Roles: {string.Join(", ", RoleCatalog.All.Where(r => r.Role != WorkspaceRole.BreakGlass).Select(r => r.Key))}."];
                return [];
            }

            if (!roles.Contains(role))
            {
                roles.Add(role);
            }
        }

        if (!allowEmpty && roles.Count == 0)
        {
            errors[key] = ["Name at least one role."];
        }

        roles.Sort();
        return roles;
    }

    private async Task<SecurityOutcome<EthicalWallState>> MapWallAsync(
        SecurityPrincipal principal, Guid workspaceId, SecurityWrite<EthicalWallState> write, CancellationToken cancellationToken)
    {
        if (write.Status == SecurityWriteStatus.UnknownDocuments)
        {
            return SecurityOutcome.Invalid<EthicalWallState>(new() { ["scope.documentIds"] = [UnknownDocument] });
        }

        var outcome = Map(write);
        if (outcome.Value is { } wall)
        {
            var hidden = await HiddenDocumentsAsync(principal, workspaceId, wall.Definition.DocumentIds, cancellationToken).ConfigureAwait(false);
            outcome = outcome with { Value = Redact(wall, hidden) };
        }

        return outcome;
    }

    private static SecurityOutcome<T> Map<T>(SecurityWrite<T> write)
        where T : class => write.Status switch
        {
            SecurityWriteStatus.Ok => new(SecurityOutcomeStatus.Ok, write.Value),
            SecurityWriteStatus.Created => new(SecurityOutcomeStatus.Created, write.Value),
            SecurityWriteStatus.VersionConflict => new(SecurityOutcomeStatus.VersionConflict),
            SecurityWriteStatus.NameTaken or SecurityWriteStatus.AlreadyActive => new(SecurityOutcomeStatus.Conflict),
            SecurityWriteStatus.UnknownDocuments => SecurityOutcome.Invalid<T>(new() { ["scope.documentIds"] = [UnknownDocument] }),
            _ => new(SecurityOutcomeStatus.NotFound),
        };

    private AuditEvent WallEvent(
        SecurityPrincipal principal, Guid workspaceId, string action, Guid wallId, EthicalWallDefinition definition, EthicalWallDefinition? before)
    {
        var details = new Dictionary<string, string?>
        {
            ["Name"] = definition.Name,
            ["Users"] = Invariant(definition.UserIds.Count),
            ["Groups"] = Invariant(definition.Groups.Count),
            ["ScopeDocuments"] = Invariant(definition.DocumentIds.Count),
            ["ScopeCustodians"] = Invariant(definition.Custodians.Count),
            ["ScopeChoices"] = string.Join(',', definition.Choices.Select(c => Invariant(c.FieldId) + ":" + Invariant(c.ChoiceId))),
        };
        if (before is not null)
        {
            details["UsersAdded"] = Truncated(definition.UserIds.Except(before.UserIds).Select(u => u.ToString()));
            details["UsersRemoved"] = Truncated(before.UserIds.Except(definition.UserIds).Select(u => u.ToString()));
            details["GroupsAdded"] = Truncated(definition.Groups.Except(before.Groups, StringComparer.Ordinal));
            details["GroupsRemoved"] = Truncated(before.Groups.Except(definition.Groups, StringComparer.Ordinal));
        }
        else
        {
            details["UserIds"] = Truncated(definition.UserIds.Select(u => u.ToString()));
            details["GroupNames"] = Truncated(definition.Groups);
        }

        return Event(principal, workspaceId, action, "EthicalWall", wallId.ToString(), details);
    }

    private static string Truncated(IEnumerable<string> values)
    {
        var joined = string.Join(',', values);
        return joined.Length <= 1_000 ? joined : joined[..1_000] + "…";
    }

    private AuditEvent Event(
        SecurityPrincipal actor, Guid workspaceId, string action, string resourceType, string resourceId, Dictionary<string, string?> details,
        bool breakGlass = false) => new()
        {
            WorkspaceId = workspaceId,
            OccurredAt = time.GetUtcNow(),
            Category = AuditTaxonomy.Security.Category,
            Action = action,
            ActorType = AuditActorType.User,
            ActorId = actor.UserId.ToString(),
            ActorDisplay = actor.DisplayName.Length > AuditEventRules.MaxActorDisplayLength
                ? actor.DisplayName[..AuditEventRules.MaxActorDisplayLength]
                : actor.DisplayName,
            AccessPath = breakGlass ? AuditAccessPath.BreakGlass : AuditAccessPath.Normal,
            ClientIp = actor.ClientIp,
            UserAgent = actor.UserAgent,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Outcome = AuditOutcome.Success,
            CorrelationId = actor.CorrelationId ?? Activity.Current?.GetTagItem(CorrelationTag) as string,
            Details = details,
        };

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{0,63}$")]
    private static partial Regex ClassKeyPattern();
}
