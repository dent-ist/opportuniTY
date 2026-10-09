using System.Globalization;

using Opportunity.Application.Productions;
using Opportunity.Contracts.Api;
using Opportunity.Core.Productions;

namespace Opportunity.Production.Productions;

/// <summary>One text stamped on a produced page.</summary>
public sealed record PageStamp(EndorsementPosition Position, string Text);

/// <summary>
/// The endorsements of produced pages (E12-T04), from the frozen specification: each template with <c>{bates}</c> (the
/// page's Bates label), <c>{confidentiality}</c> (the member's frozen designation legend) and <c>{production}</c> (the
/// production name) filled in; a stamp whose text comes out empty is left off. Pure: the volume writer (E12-T05) asks
/// for each page's stamps and hands them to the page endorser in the render sandbox.
/// </summary>
public static class EndorsementPlanner
{
    public static EndorsementPosition Position(EndorsementPositionResource position) => position switch
    {
        EndorsementPositionResource.TopLeft => EndorsementPosition.TopLeft,
        EndorsementPositionResource.TopCenter => EndorsementPosition.TopCenter,
        EndorsementPositionResource.TopRight => EndorsementPosition.TopRight,
        EndorsementPositionResource.BottomLeft => EndorsementPosition.BottomLeft,
        EndorsementPositionResource.BottomCenter => EndorsementPosition.BottomCenter,
        EndorsementPositionResource.BottomRight => EndorsementPosition.BottomRight,
        _ => throw new ArgumentOutOfRangeException(nameof(position), position, "Unknown endorsement position."),
    };

    /// <summary>The stamps of one page.</summary>
    public static IReadOnlyList<PageStamp> ForPage(ProductionSpecification specification, string productionName, string batesLabel, string? designation)
    {
        ArgumentNullException.ThrowIfNull(specification);
        var stamps = new List<PageStamp>();
        foreach (var item in specification.Endorsements?.Items ?? [])
        {
            var text = item.Template
                .Replace("{bates}", batesLabel, StringComparison.Ordinal)
                .Replace("{confidentiality}", designation ?? string.Empty, StringComparison.Ordinal)
                .Replace("{production}", productionName, StringComparison.Ordinal)
                .Trim();
            if (text.Length > 0)
            {
                stamps.Add(new PageStamp(Position(item.Position), text));
            }
        }

        return stamps;
    }

    /// <summary>
    /// The Bates label of every produced page of a member: its numbers at page level; at document level the document's
    /// number with a page suffix per image page. A native slip sheet or placeholder is one page with the first label.
    /// </summary>
    public static IReadOnlyList<string> PageLabels(BatesFormat format, ProductionDocumentRow member)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(member);
        if (member.BegNumber is not { } begin || member.EndNumber is not { } end)
        {
            return [];
        }

        if (member.Output != ProductionOutputKind.Image)
        {
            return [format.Format(begin)];
        }

        if (format.Level == BatesNumberingLevel.Document)
        {
            var pages = Math.Max(1, member.PageCount);
            return [.. Enumerable.Range(1, pages).Select(p => format.PageLabel(begin, p))];
        }

        var labels = new List<string>(checked((int)(end - begin + 1)));
        for (var n = begin; n <= end; n++)
        {
            labels.Add(format.Format(n));
        }

        return labels;
    }

    /// <summary>Every produced page of a member with its stamps, in page order.</summary>
    public static IReadOnlyList<(string BatesLabel, IReadOnlyList<PageStamp> Stamps)> ForMember(
        ProductionSpecification specification, string productionName, ProductionDocumentRow member)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(member);
        var format = ProductionSpecificationRules.FormatOf(specification);
        return [.. PageLabels(format, member).Select(label => (label, ForPage(specification, productionName, label, member.Designation)))];
    }
}

/// <summary>
/// The designation QC check (E12-T04 AC 1): every page of a designated member carries its legend, and the load file's
/// designation value is the same legend, for 100 % of the members. The volume writer (E12-T05) runs it over what it
/// actually stamped and wrote; finalization runs the specification-level part (a designated member needs an
/// endorsement that stamps <c>{confidentiality}</c>).
/// </summary>
public static class DesignationQc
{
    /// <summary>Problems of one member (empty when it passes).</summary>
    /// <param name="stampedPages">The stamps drawn on each produced page of the member, in page order.</param>
    /// <param name="loadFileValue">The member's designation value as written to the load file.</param>
    public static IReadOnlyList<string> CheckMember(
        ProductionSpecification specification, ProductionDocumentRow member, IReadOnlyList<IReadOnlyList<PageStamp>> stampedPages, string? loadFileValue)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(stampedPages);
        var problems = new List<string>();
        var name = member.ProdBegBates ?? member.DocumentId.ToString();
        if (member.DesignationSource is null)
        {
            problems.Add($"{name}: the designation was not frozen with the production.");
            return problems;
        }

        var legend = member.Designation ?? string.Empty;
        var expectedPages = ProductionSpecificationRules.FormatOf(specification) is var format ? EndorsementPlanner.PageLabels(format, member).Count : 0;
        if (stampedPages.Count != expectedPages)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{name}: {stampedPages.Count} pages stamped, {expectedPages} produced."));
        }

        if (legend.Length > 0)
        {
            for (var i = 0; i < stampedPages.Count; i++)
            {
                if (!stampedPages[i].Any(s => s.Text.Contains(legend, StringComparison.Ordinal)))
                {
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"{name}: page {i + 1} does not carry the designation '{legend}'."));
                }
            }
        }

        if (!string.Equals(loadFileValue ?? string.Empty, legend, StringComparison.Ordinal))
        {
            problems.Add($"{name}: the load file says '{loadFileValue}', the pages say '{legend}'.");
        }

        return problems;
    }
}
