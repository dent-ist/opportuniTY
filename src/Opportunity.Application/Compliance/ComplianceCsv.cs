using System.Globalization;
using System.Text;

using Opportunity.Core.Security;
using Opportunity.Core.Text;

namespace Opportunity.Application.Compliance;

/// <summary>
/// The wall proof and the access review as CSV (E14-T06): RFC 4180, UTF-8 with a byte-order mark, UTC times; text a
/// spreadsheet would run as a formula gets a leading apostrophe. A short header block (report, workspace, period,
/// verdict) precedes the rows, so a printed copy stands on its own.
/// </summary>
public static class ComplianceCsv
{
    public const string ContentType = "text/csv; charset=utf-8";

    public static string WallProofFileName(DateTimeOffset generatedAt) => "ethical-wall-proof-" + Stamp(generatedAt) + ".csv";

    public static string AccessReviewFileName(DateTimeOffset generatedAt) => "access-review-" + Stamp(generatedAt) + ".csv";

    public static byte[] WallProof(WallProofReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new StringBuilder();
        var counts = report.Data.Counts;
        var names = report.Data.Walls.ToDictionary(w => w.WallId, w => w.Name);
        Append(text, ["Report", "Ethical wall proof"]);
        Append(text, ["Workspace", report.WorkspaceId.ToString("D")]);
        Append(text, ["Period (UTC)", Time(report.From) + " to " + Time(report.To)]);
        Append(text, ["Generated (UTC)", Time(report.GeneratedAt)]);
        Append(text, ["Result", report.Held ? "Held: no walled user reached walled content" : "Not held: walled content was served to a walled user"]);
        Append(text, ["Attempts", N(counts.Attempts), "Denied", N(counts.Denied), "Break-glass", N(counts.BreakGlass), "Allowed", N(counts.Allowed), "Failed", N(counts.Failed)]);
        foreach (var wall in report.Data.Walls)
        {
            Append(text,
            [
                "Wall", wall.Name, wall.WallId.ToString("D"), "Documents covered", N(wall.DocumentsCovered), "Members",
                string.Join("; ", wall.Members.Select(m => m.GroupName is { } g ? "group " + g : m.DisplayName ?? m.UserId?.ToString("D"))),
            ]);
        }

        foreach (var change in report.Data.Changes)
        {
            Append(text, ["Wall change", Time(change.OccurredAt), change.Action, change.WallId, change.ActorDisplay]);
        }

        if (report.Data.Truncated)
        {
            Append(text, ["Note", $"Only the first {N(report.Data.Attempts.Count)} attempts are listed; the counts above include all."]);
        }

        text.Append("\r\n");
        Append(text, ["Time (UTC)", "Result", "User ID", "User", "Document ID", "Walls", "Action", "Outcome", "Reason", "Access Path", "Event ID", "Correlation ID"]);
        foreach (var a in report.Data.Attempts)
        {
            Append(text,
            [
                Time(a.OccurredAt), a.Result.ToString(), a.ActorId, a.ActorDisplay, a.DocumentId.ToString("D"),
                string.Join("; ", a.WallIds.Select(id => names.TryGetValue(id, out var name) ? name : id.ToString("D"))),
                a.Category + "." + a.Action, a.Outcome, a.ReasonCode, a.AccessPath, a.EventId.ToString("D"), a.CorrelationId,
            ]);
        }

        return Bytes(text);
    }

    public static byte[] AccessReview(AccessReviewReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new StringBuilder();
        Append(text, ["Report", "Workspace access review"]);
        Append(text, ["Workspace", report.WorkspaceId.ToString("D")]);
        Append(text, ["Generated (UTC)", Time(report.GeneratedAt)]);
        Append(text, ["Users", N(report.Users), "Groups", N(report.Groups)]);
        text.Append("\r\n");
        Append(text,
        [
            "Principal", "User ID", "Group", "Name", "Email", "Roles", "Role Sources", "Ethical Walls", "Last Workspace Access (UTC)",
            "Last Sign-in (UTC)", "Known Group Members",
        ]);
        foreach (var e in report.Entries)
        {
            Append(text,
            [
                e.Principal.ToString(), e.UserId?.ToString("D"), e.GroupName, e.DisplayName, e.Email,
                string.Join("; ", e.Roles.Select(r => RoleCatalog.Get(r.Role).DisplayName).Distinct()),
                string.Join("; ", e.Roles.Select(r => RoleCatalog.Get(r.Role).DisplayName + (r.ViaGroup is { } g ? " via group " + g : " (direct)")
                    + " since " + Time(r.AssignedAt))),
                string.Join("; ", e.Walls.Select(w => w.Name + (w.ViaGroup is { } g ? " via group " + g : string.Empty))),
                e.LastAccessAt is { } access ? Time(access) : null,
                e.LastSignInAt is { } signIn ? Time(signIn) : null,
                e.KnownMembers is { } known ? N(known) : null,
            ]);
        }

        return Bytes(text);
    }

    private static byte[] Bytes(StringBuilder text) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    private static string Time(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static void Append(StringBuilder text, IEnumerable<string?> cells) =>
        text.AppendJoin(',', cells.Select(Cell)).Append("\r\n");

    private static string Cell(string? value)
    {
        var v = value ?? string.Empty;
        v = FormulaNeutralizer.Neutralize(v);

        return v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }
}
