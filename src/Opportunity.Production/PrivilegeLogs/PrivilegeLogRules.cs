using System.Globalization;
using System.Text;

using Opportunity.Application.PrivilegeLogs;
using Opportunity.Core.Fields;
using Opportunity.Core.Productions;
using Opportunity.Core.Redactions;

namespace Opportunity.Production.PrivilegeLogs;

/// <summary>
/// Which documents a privilege log lists and how (E13-T03 AC 1: every document withheld or redacted for privilege appears
/// exactly once; no document produced in full appears). Pure functions over what the store read.
/// </summary>
public static class PrivilegeLogRules
{
    /// <summary>
    /// The treatment of a candidate, or null when it does not belong on the log.
    /// <list type="bullet">
    /// <item>Production member produced as a placeholder: withheld when coded Withhold (its placeholder's Bates is its log id).</item>
    /// <item>Production member produced as images or natively: only with produced redactions (else it was produced in
    /// full); redacted for privilege when a reason is in the Privilege category or it is coded Redact; otherwise privacy
    /// only, listed as such when <paramref name="includePrivacyRedactions"/>.</item>
    /// <item>Not produced (a production's family member or review-set document): withheld when coded Withhold.</item>
    /// <item>Frozen-set log: Withhold → withheld, Redact → redacted.</item>
    /// </list>
    /// </summary>
    public static PrivilegeLogEntryTreatment? Classify(PrivilegeLogCandidate candidate, PrivilegeLogSource source, bool includePrivacyRedactions)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var withhold = candidate.StatusKey == PrivilegeFields.Keys.Withhold;
        var redact = candidate.StatusKey == PrivilegeFields.Keys.Redact;
        if (source == PrivilegeLogSource.Snapshot)
        {
            return withhold ? PrivilegeLogEntryTreatment.Withheld : redact ? PrivilegeLogEntryTreatment.Redacted : null;
        }

        if (candidate.Member is not { } member)
        {
            return withhold ? PrivilegeLogEntryTreatment.Withheld : null;
        }

        if (member.Output == ProductionOutputKind.Placeholder)
        {
            return withhold ? PrivilegeLogEntryTreatment.Withheld : null;
        }

        if (member.RedactionCount == 0)
        {
            return null;
        }

        if (redact || member.RedactionReasons.Any(r => r.Category == RedactionReasonCategory.Privilege))
        {
            return PrivilegeLogEntryTreatment.Redacted;
        }

        return includePrivacyRedactions && member.RedactionReasons.Any(r => r.Category == RedactionReasonCategory.Privacy)
            ? PrivilegeLogEntryTreatment.RedactedPrivacy
            : null;
    }

    public static string TreatmentText(PrivilegeLogEntryTreatment treatment) => treatment switch
    {
        PrivilegeLogEntryTreatment.Withheld => "Withheld",
        PrivilegeLogEntryTreatment.Redacted => "Redacted",
        _ => "Redacted (Privacy)",
    };

    public static string PrivId(string prefix, long number, int padding) =>
        prefix + number.ToString(CultureInfo.InvariantCulture).PadLeft(padding, '0');

    /// <summary>
    /// The first and last log identifier among a family's members (in family order) that are produced or listed: the
    /// first member's begin and the last member's end identifier; empty when fewer than two such members exist.
    /// </summary>
    public static string FamilyRange(IReadOnlyList<(string Begin, string End)> identifiedMembers)
    {
        ArgumentNullException.ThrowIfNull(identifiedMembers);
        return identifiedMembers.Count < 2 ? string.Empty : identifiedMembers[0].Begin + " - " + identifiedMembers[^1].End;
    }

    /// <summary>A cell value without control characters other than tab, line feed and carriage return.</summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (!value.Any(c => char.IsControl(c) && c is not ('\t' or '\n' or '\r')))
        {
            return value;
        }

        var text = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (!char.IsControl(c) || c is '\t' or '\n' or '\r')
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }
}
