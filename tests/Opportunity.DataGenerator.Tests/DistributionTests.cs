using AwesomeAssertions;

using Opportunity.DataGenerator.Corpus.Generation;
using Opportunity.DataGenerator.Corpus.Model;
using Opportunity.DataGenerator.Corpus.Output;
using Opportunity.DataGenerator.Corpus.Profiles;

namespace Opportunity.DataGenerator.Tests;

/// <summary>
/// Sanity checks on 20K-document corpora. Tolerances reflect sampling error at this size (heavy-tailed family and
/// text sizes); the ±2% acceptance check is made on 1M documents via the manifest's distribution report.
/// Every check is deterministic (fixed seeds), so these tests cannot flake.
/// </summary>
public class DistributionTests
{
    private static readonly Lazy<CorpusStatistics> Default = new(() => TestCorpus.Jsonl(TestCorpus.Profile(20_000), 42, 4).Stats);

    [Fact]
    public void Corpus_has_exactly_the_requested_document_count()
    {
        foreach (long n in new long[] { 0, 1, 2, 7, 1_000, 20_000 })
        {
            new CorpusGenerator(TestCorpus.Profile(n), 3).GenerateDocuments().LongCount().Should().Be(n);
        }
    }

    [Fact]
    public void Family_size_mean_is_close_to_target_without_containers()
    {
        CorpusStatistics s = TestCorpus.Jsonl(TestCorpus.NoContainers(20_000), 7, 4).Stats;
        s.MeanFamilySize.Should().BeApproximately(3.0, 0.09);
        s.MaxFamilySize.Should().BeLessThan(200);
    }

    [Fact]
    public void Family_size_has_a_long_tail_with_containers()
    {
        CorpusStatistics s = Default.Value;
        s.MeanFamilySize.Should().BeApproximately(3.0, 0.25);
        s.MaxFamilySize.Should().BeGreaterThan(200);
        s.FamiliesOver200.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Email_and_edoc_mix_matches_target()
    {
        Default.Value.EmailFamilyShare.Should().BeApproximately(0.60, 0.03);
    }

    [Fact]
    public void Duplicate_rate_and_types_match_target()
    {
        CorpusStatistics s = Default.Value;
        s.DuplicateRate.Should().BeApproximately(0.20, 0.03);
        s.DuplicatesOfType(DuplicateType.CrossCustodian).Should().BeGreaterThan(s.DuplicatesOfType(DuplicateType.ExactMd5));
        s.DuplicatesOfType(DuplicateType.ExactMd5).Should().BeGreaterThan(0);
        s.DuplicatesOfType(DuplicateType.WithinFamily).Should().BeGreaterThan(0);
        s.FamilyCopies.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Text_sizes_are_heavy_tailed_around_configured_percentiles()
    {
        CorpusStatistics s = Default.Value;
        var cal = new Calibration(new CorpusProfile());
        ((double)s.Text.Quantile(0.50)).Should().BeApproximately(5120, 5120 * 0.08);
        ((double)s.Text.Quantile(0.95)).Should().BeApproximately(cal.TextQuantile(1.6448536269514722), cal.TextQuantile(1.6448536269514722) * 0.15);
        ((double)s.Text.Quantile(0.99)).Should().BeApproximately(1024 * 1024, 1024 * 1024 * 0.25);
        s.Text.Max.Should().BeGreaterThan(5L * 1024 * 1024);
        s.NoTextDocuments.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Threads_custodians_and_recipients_are_realistic()
    {
        CorpusStatistics s = Default.Value;
        s.Threads.Should().BeGreaterThan(50);
        s.MaxThreadLength.Should().BeInRange(20, 50);
        s.ToRecipients.Max.Should().BeInRange(100, 500);
        s.ToRecipients.Quantile(0.5).Should().BeLessThanOrEqualTo(3);

        long[] perCustodian = [.. s.CustodianDocuments.Values.OrderDescending()];
        perCustodian.Length.Should().BeLessThanOrEqualTo(250).And.BeGreaterThan(150);
        // Zipf: the busiest custodian holds far more than the median custodian.
        perCustodian[0].Should().BeGreaterThan(perCustodian[perCustodian.Length / 2] * 20);
    }

    [Fact]
    public void Custom_fields_cover_varied_types_unicode_and_load_file_hazards()
    {
        List<GeneratedDocument> docs = TestCorpus.Documents(TestCorpus.Profile(5_000), 13, 2);
        var catalog = new FieldCatalog(new FieldProfile());
        catalog.Count.Should().BeInRange(28, 35);
        catalog.Fields.Select(f => f.Type).Distinct().Should().HaveCountGreaterThanOrEqualTo(9);

        string[] strings = [.. docs.SelectMany(d => d.Fields).SelectMany(v => v switch
        {
            string s => [s],
            string[] a => a,
            _ => Array.Empty<string>(),
        })];
        strings.Should().Contain(s => s.Contains('þ') || s.Contains('\u0014'));
        strings.Should().Contain(s => s.Contains('®'));
        strings.Should().Contain(s => s.Contains('\n'));
        strings.Any(s => s.Any(c => c is >= '\u4e00' and <= '\u9fff')).Should().BeTrue("CJK values expected");
        strings.Any(s => s.Any(c => c is >= '\u0590' and <= '\u06ff')).Should().BeTrue("RTL values expected");
        strings.Any(s => s.Any(c => c is >= '\u00c0' and <= '\u017f')).Should().BeTrue("accented values expected");

        var offsets = docs.Select(d => d.Fields[FieldCatalog.DateSent]).OfType<DateTimeOffset>().Select(d => d.Offset).Distinct().ToList();
        offsets.Count.Should().BeGreaterThan(5);
        docs.Any(d => d.Fields[FieldCatalog.Amount] is decimal).Should().BeTrue();
        docs.Any(d => d.Fields[FieldCatalog.RecordDate] is DateOnly).Should().BeTrue();
        docs.Any(d => d.Fields[FieldCatalog.Keywords] is string[]).Should().BeTrue();
    }

    [Fact]
    public void Extra_custom_fields_are_generated_when_configured()
    {
        CorpusProfile profile = ProfileSerializer.Parse("""{ "documentCount": 500, "fields": { "extraCustomFields": 20 } }""");
        List<GeneratedDocument> docs = TestCorpus.Documents(profile, 1);
        docs[0].Fields.Length.Should().Be(FieldCatalog.StandardCount + 20);
        docs.Should().Contain(d => d.Fields[FieldCatalog.StandardCount + 19] != null);
    }
}
