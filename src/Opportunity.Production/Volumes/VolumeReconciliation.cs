using System.Globalization;

using Opportunity.Core.Productions;
using Opportunity.Import.LoadFiles;
using Opportunity.Production.Exports;

namespace Opportunity.Production.Volumes;

/// <summary>What a volume run registered, to reconcile its load files against.</summary>
/// <param name="Members">The production's members (one DAT row each).</param>
/// <param name="ImageFiles">Image files registered by the run.</param>
/// <param name="NativeFiles">Native files registered by the run.</param>
/// <param name="TextFiles">Text files registered by the run.</param>
/// <param name="BatesNumbers">The production's allocated numbers (its Bates span), for runs whose DAT has no ProdBeg/EndBates.</param>
public sealed record VolumeRegistration(long Members, long ImageFiles, long NativeFiles, long TextFiles, long BatesNumbers);

/// <summary>
/// The reconciliation of a volume (E12-T07): read back from the stored DAT and OPT, it holds when images = OPT rows =
/// Σ(ProdEnd − ProdBeg + 1) (page-level numbering), natives = DAT NativeLink values, text files = DAT TextLink values and
/// DAT rows = members. A link column the load file does not carry is not reconciled (null).
/// </summary>
public sealed record VolumeReconciliationResult(
    long DatRows,
    long OptRows,
    long ImageFiles,
    long BatesSpan,
    long NativeFiles,
    long? NativeLinks,
    long TextFiles,
    long? TextLinks,
    IReadOnlyList<string> Problems)
{
    public bool Passed => Problems.Count == 0;
}

public static class VolumeReconciliation
{
    public static async Task<VolumeReconciliationResult> ReconcileAsync(
        Stream dat, Stream opt, ExportSettings settings, BatesFormat format, VolumeRegistration registered, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dat);
        ArgumentNullException.ThrowIfNull(opt);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(registered);
        string? HeaderOf(ExportColumnKind kind) => settings.Columns.FirstOrDefault(c => c.Kind == kind)?.Header;
        long rows = 0, span = 0, natives = 0, texts = 0, unparsed = 0;
        var problems = new List<string>();
        await using (var reader = await DatReader.OpenAsync(dat, new DatReaderOptions
        {
            Profile = settings.Profile,
            EncodingOverride = settings.DatEncoding,
            MaxRejectedRows = null,
        }, leaveOpen: true, cancellationToken).ConfigureAwait(false))
        {
            int Index(string? header) => header is null ? -1 : reader.Header.IndexOf(header);
            var beg = Index(HeaderOf(ExportColumnKind.ProdBegBates));
            var end = Index(HeaderOf(ExportColumnKind.ProdEndBates));
            var native = Index(HeaderOf(ExportColumnKind.NativePath));
            var text = Index(HeaderOf(ExportColumnKind.TextPath));
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) is { } record)
            {
                rows++;
                if (beg >= 0 && end >= 0)
                {
                    if (format.TryParse(record.Values[beg], out var first) && format.TryParse(record.Values[end], out var last) && last >= first)
                    {
                        span += last - first + 1;
                    }
                    else
                    {
                        unparsed++;
                    }
                }

                natives += native >= 0 && record.Values[native].Length > 0 ? 1 : 0;
                texts += text >= 0 && record.Values[text].Length > 0 ? 1 : 0;
            }

            if (beg < 0 || end < 0)
            {
                span = registered.BatesNumbers;
            }

            var result = await CountOptAsync(opt, cancellationToken).ConfigureAwait(false);
            void Check(bool holds, string problem)
            {
                if (!holds)
                {
                    problems.Add(problem);
                }
            }

            Check(reader.Statistics.RejectedRows == 0, Invariant($"{reader.Statistics.RejectedRows} DAT row(s) cannot be read back."));
            Check(unparsed == 0, Invariant($"{unparsed} DAT row(s) have ProdBegBates/ProdEndBates that are not Bates numbers of the production."));
            Check(rows == registered.Members, Invariant($"The DAT has {rows} row(s) for {registered.Members} document(s)."));
            Check(result == registered.ImageFiles, Invariant($"The OPT has {result} row(s) for {registered.ImageFiles} image file(s)."));
            Check(format.Level != BatesNumberingLevel.Page || registered.ImageFiles == span,
                Invariant($"{registered.ImageFiles} image file(s) for a Bates span of {span}."));
            Check(native < 0 || natives == registered.NativeFiles, Invariant($"{registered.NativeFiles} native file(s) for {natives} DAT NativeLink value(s)."));
            Check(text < 0 || texts == registered.TextFiles, Invariant($"{registered.TextFiles} text file(s) for {texts} DAT TextLink value(s)."));
            return new VolumeReconciliationResult(rows, result, registered.ImageFiles, span, registered.NativeFiles, native < 0 ? null : natives,
                registered.TextFiles, text < 0 ? null : texts, problems);
        }
    }

    private static async Task<long> CountOptAsync(Stream opt, CancellationToken cancellationToken)
    {
        long rows = 0;
        await using var reader = OptReader.Open(opt, leaveOpen: true);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) is { } record)
        {
            rows += record.Problem is null && record.ImageKey.Length > 0 ? 1 : 0;
        }

        return rows;
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
