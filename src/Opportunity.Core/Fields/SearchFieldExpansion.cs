namespace Opportunity.Core.Fields;

/// <summary>Field-binding options of a search (E07-T07 planner input).</summary>
/// <param name="CustodianIncludesAllCustodians">
/// <c>custodian:</c> also matches documents whose <c>All Custodians</c> (the custodians of every de-duplicated copy,
/// ADR-009 R13) holds the value, so a globally de-duplicated document is found under each custodian who held it.
/// </param>
public sealed record SearchFieldOptions(bool CustodianIncludesAllCustodians = false);

/// <summary>
/// Which catalogue fields a query field addresses (E09-T02). Today one rule: with
/// <see cref="SearchFieldOptions.CustodianIncludesAllCustodians"/>, the custodian field also searches the
/// <see cref="SystemFields.AllCustodians"/> system field; the planner ORs the fields' clauses.
/// </summary>
public static class SearchFieldExpansion
{
    /// <summary>Query name of the custodian field (ADR-008 §7: <c>custodian:"John Smith"</c>).</summary>
    public const string CustodianQueryName = "custodian";

    public static IReadOnlyList<FieldDefinition> Expand(FieldCatalog catalog, FieldDefinition field, SearchFieldOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(field);
        if (options?.CustodianIncludesAllCustodians != true || !IsCustodian(field)
            || catalog.Find(SystemFields.AllCustodians) is not { IsDeleted: false, IsSearchable: true } all
            || all.FieldId == field.FieldId)
        {
            return [field];
        }

        return [field, all];
    }

    /// <summary>The workspace's custodian field: the live Keyword metadata field whose ADR-008 R11 query name is <c>custodian</c>.</summary>
    public static bool IsCustodian(FieldDefinition field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return !field.IsDeleted && field.Type == FieldType.Keyword && field.Storage == FieldStorage.Metadata
            && string.Equals(DefaultQueryName(field.Name), CustodianQueryName, StringComparison.Ordinal);
    }

    /// <summary>ADR-008 R11 default query name: lower-case, every run of non-alphanumerics becomes one <c>_</c>, trimmed.</summary>
    public static string DefaultQueryName(string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        var builder = new System.Text.StringBuilder(displayName.Length);
        foreach (var c in displayName.Trim())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        return builder.ToString().TrimEnd('_');
    }
}
