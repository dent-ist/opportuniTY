using System.Text.Json.Nodes;

namespace Opportunity.Core.Fields;

/// <summary>
/// Privilege designations as system coding fields (E13-T01, §14/§15, Q-20): provisioned in every workspace by
/// <c>opportunity.provision_privilege_fields</c> (V0045), security-affecting (class
/// <see cref="SecurityClass.PrivilegeStatus"/>), renameable and hideable but never retyped or deleted. The built-in
/// choices carry a <see cref="Choice.SystemKey"/>; their ids come from the workspace counter like any other choice, so
/// code finds them by key, never by id or name.
/// </summary>
public static class PrivilegeFields
{
    public const int Status = 37;
    public const int Basis = 38;
    public const int Description = 39;
    public const int AttorneysInvolved = 40;
    public const int LogCategory = 41;

    /// <summary>Built-in choice keys (<c>choice.system_key</c>).</summary>
    public static class Keys
    {
        public const string NotPrivileged = "privilege-status.not-privileged";
        public const string Withhold = "privilege-status.withhold";
        public const string Redact = "privilege-status.redact";
        public const string NeedsSecondLevelReview = "privilege-status.needs-2l-review";
        public const string AttorneyClient = "privilege-basis.attorney-client";
        public const string WorkProduct = "privilege-basis.work-product";
        public const string CommonInterest = "privilege-basis.common-interest";
        public const string Other = "privilege-basis.other";
    }

    /// <summary>The provisioned definitions (without workspace-specific slots): id, default name, type, multi-value.</summary>
    public static IReadOnlyList<(int FieldId, string Name, FieldType Type, bool IsMultiValue)> Definitions { get; } =
    [
        (Status, "Privilege Status", FieldType.SingleChoice, false),
        (Basis, "Privilege Basis", FieldType.MultiChoice, true),
        (Description, "Privilege Description", FieldType.Text, false),
        (AttorneysInvolved, "Attorneys Involved", FieldType.Keyword, true),
        (LogCategory, "Log Category", FieldType.SingleChoice, false),
    ];

    /// <summary>
    /// Coding search slots reserved at the top of each kind's budget (ADR-007 R4), so lowest-free allocation of custom
    /// coding fields is unaffected. A workspace whose custom fields already held one gets the lowest free slot instead.
    /// </summary>
    public static IReadOnlyDictionary<int, string> ReservedSlots { get; } = new Dictionary<int, string>
    {
        [Status] = "ch.s100",
        [Basis] = "ch.s099",
        [Description] = "txt.s020",
        [AttorneysInvolved] = "kw.s020",
        [LogCategory] = "ch.s098",
    };

    /// <summary>Built-in choices per field in display order: (field, name, key).</summary>
    public static IReadOnlyList<(int FieldId, string Name, string Key)> BuiltInChoices { get; } =
    [
        (Status, "Not Privileged", Keys.NotPrivileged),
        (Status, "Withhold", Keys.Withhold),
        (Status, "Redact", Keys.Redact),
        (Status, "Needs 2L Review", Keys.NeedsSecondLevelReview),
        (Basis, "Attorney-Client", Keys.AttorneyClient),
        (Basis, "Work Product", Keys.WorkProduct),
        (Basis, "Common Interest", Keys.CommonInterest),
        (Basis, "Other", Keys.Other),
    ];

    /// <summary>Status choices the built-in <c>Privileged</c> restriction class is bound to when provisioned (ADR-015 D6.1).</summary>
    public static IReadOnlyList<string> PrivilegedClassKeys { get; } = [Keys.Withhold, Keys.Redact, Keys.NeedsSecondLevelReview];

    public static bool IsPrivilegeField(int fieldId) => fieldId is >= Status and <= LogCategory;

    /// <summary>The id of the built-in choice <paramref name="key"/> in <paramref name="catalog"/>, if loaded.</summary>
    public static int? ChoiceId(FieldCatalog catalog, int fieldId, string key)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.ChoicesOf(fieldId).FirstOrDefault(c => string.Equals(c.SystemKey, key, StringComparison.Ordinal))?.ChoiceId;
    }

    /// <summary>Status choices that need a Privilege Basis: Withhold and Redact.</summary>
    public static IReadOnlySet<int> BasisRequiredStatuses(FieldCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.Find(Status) is not { IsSystem: true, IsDeleted: false } || catalog.Find(Basis) is not { IsSystem: true, IsDeleted: false })
        {
            return new HashSet<int>();
        }

        return catalog.ChoicesOf(Status)
            .Where(c => c.SystemKey is Keys.Withhold or Keys.Redact)
            .Select(c => c.ChoiceId)
            .ToHashSet();
    }

    /// <summary>
    /// AC 1: a document whose Privilege Status is Withhold or Redact must have at least one Privilege Basis. Returns
    /// the error for the resulting values, or null when they are consistent.
    /// </summary>
    public static FieldError? ValidateBasis(FieldCatalog catalog, JsonNode? status, JsonNode? basis)
    {
        var required = BasisRequiredStatuses(catalog);
        if (required.Count == 0 || !FieldValues.ChoiceIds(status).Any(required.Contains) || FieldValues.ChoiceIds(basis).Count > 0)
        {
            return null;
        }

        var statusName = catalog.Find(Status)!.Name;
        var basisName = catalog.Find(Basis)!.Name;
        return new FieldError(FieldKey.For(Basis), BasisRequiredCode,
            $"{basisName} is required when {statusName} is Withhold or Redact.");
    }

    public const string BasisRequiredCode = "privilege-basis-required";
}
