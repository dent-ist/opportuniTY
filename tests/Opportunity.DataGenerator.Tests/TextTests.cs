using System.Text;

using AwesomeAssertions;

using Opportunity.DataGenerator.Corpus.Generation;
using Opportunity.DataGenerator.Corpus.Model;
using Opportunity.DataGenerator.Corpus.Profiles;
using Opportunity.DataGenerator.Corpus.Text;

namespace Opportunity.DataGenerator.Tests;

public class TextTests
{
    private static readonly char[] Separators = [' ', '.', '\n'];

    private static readonly Lazy<(GenerationContext Context, List<GeneratedDocument> Docs)> Corpus = new(() =>
    {
        var context = new GenerationContext(TestCorpus.SmallText(4_000), 21);
        return (context, [.. new CorpusGenerator(context, 2).GenerateDocuments().Where(d => d.IsDuplicatePrimary)]);
    });

    [Fact]
    public void Rendered_text_has_exactly_the_declared_utf8_size()
    {
        var synthesizer = new TextSynthesizer(Corpus.Value.Context.Vocabulary);
        foreach (GeneratedDocument doc in Corpus.Value.Docs.Take(1500))
        {
            ((long)Encoding.UTF8.GetByteCount(synthesizer.Render(doc.Content.Text))).Should().Be(doc.TextBytes);
        }
    }

    [Fact]
    public void Planted_needles_match_ground_truth_exactly()
    {
        (GenerationContext context, List<GeneratedDocument> docs) = Corpus.Value;
        var synthesizer = new TextSynthesizer(context.Vocabulary);
        int withHits = 0;
        int withPairs = 0;
        foreach (GeneratedDocument doc in docs)
        {
            if (doc.TextBytes == 0)
            {
                continue;
            }

            string[] tokens = synthesizer.Render(doc.Content.Text).Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            foreach (NeedleProfile needle in context.Profile.Needles)
            {
                int expected = doc.Content.Needles.FirstOrDefault(n => n.Term == needle.Term)?.Occurrences ?? 0;
                tokens.Count(t => t == needle.Term).Should().Be(expected, "{0} in {1}", needle.Term, doc.ControlNumber);
                if (needle.Scope == NeedleScope.AttachmentsOnly && expected > 0)
                {
                    doc.AttachmentDepth.Should().BeGreaterThan(0);
                }
            }

            withHits += doc.Content.Needles.Count > 0 ? 1 : 0;
            foreach (ProximityHit hit in doc.Content.ProximityHits)
            {
                int a = Array.IndexOf(tokens, hit.First);
                int b = Array.IndexOf(tokens, hit.Second);
                (b - a).Should().Be(hit.Distance);
                tokens.Count(t => t == hit.First).Should().Be(1);
                withPairs++;
            }

            if (doc.Content.ProximityHits.Count == 0)
            {
                tokens.Should().NotContain("kelvatron");
            }
        }

        withHits.Should().BeGreaterThan(50);
        withPairs.Should().BeGreaterThan(20);
    }

    [Fact]
    public void Vocabulary_excludes_needle_terms_and_their_parts()
    {
        Vocabulary vocabulary = Corpus.Value.Context.Vocabulary;
        foreach (string term in new[] { "zyqvoltran", "plimsorbex", "vex-tomarin", "vex", "tomarin", "kelvatron", "brisomund" })
        {
            vocabulary.Contains(term).Should().BeFalse(term);
        }
    }

    [Fact]
    public void Replies_quote_prior_text_and_near_duplicates_share_most_words()
    {
        (GenerationContext context, List<GeneratedDocument> docs) = Corpus.Value;
        var synthesizer = new TextSynthesizer(context.Vocabulary);
        docs.Where(d => d.Content.Text.QuotedBytes > 0).Take(20)
            .Should().OnlyContain(d => synthesizer.Render(d.Content.Text).Contains("-----Original Message-----", StringComparison.Ordinal));

        IGrouping<string?, GeneratedDocument>? cluster = docs.Where(d => d.Content.NearDuplicateClusterId != null)
            .GroupBy(d => d.Content.NearDuplicateClusterId).FirstOrDefault(g => g.Count() >= 2);
        cluster.Should().NotBeNull();
        GeneratedDocument[] pair = [.. cluster!.Take(2)];
        pair[0].Md5.Should().NotBe(pair[1].Md5);
        string[] x = synthesizer.Render(pair[0].Content.Text).Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        string[] y = synthesizer.Render(pair[1].Content.Text).Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        int n = Math.Min(Math.Min(x.Length, y.Length), 500);
        int same = Enumerable.Range(0, n).Count(i => string.Equals(x[i], y[i], StringComparison.OrdinalIgnoreCase));
        ((double)same / n).Should().BeGreaterThan(0.85);
    }

    [Fact]
    public void Large_text_streams_without_materialising_it()
    {
        var vocabulary = Corpus.Value.Context.Vocabulary;
        var spec = new TextSpec { TotalBytes = 50L * 1024 * 1024, BodySeed = 1, BodyBytes = 50L * 1024 * 1024 };
        var sink = new CountingSink();
        new TextSynthesizer(vocabulary).Write(spec, sink);
        sink.Chars.Should().Be(spec.TotalBytes);
        sink.MaxChunk.Should().BeLessThanOrEqualTo(16 * 1024);
    }

    private sealed class CountingSink : ITextSink
    {
        public long Chars { get; private set; }

        public int MaxChunk { get; private set; }

        public void Write(ReadOnlySpan<char> chars)
        {
            Chars += chars.Length;
            MaxChunk = Math.Max(MaxChunk, chars.Length);
        }
    }
}