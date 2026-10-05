using System.Diagnostics;
using System.Text;

using AwesomeAssertions;

using Opportunity.Core.Highlighting;
using Opportunity.Core.QueryLanguage;

namespace Opportunity.UnitTests.Highlighting;

/// <summary>E16-T12: server-side term hits over chunked text: whole phrase and proximity spans, folding, chunk seams.</summary>
public class TermHitScannerTests
{
    [Fact]
    public void Tokenizer_follows_the_word_break_rules_search_uses()
    {
        Words("Don't e-mail john.smith@example.com about 3.14 or 1,000 items_v2 (Résumé) ÆON straße")
            .Should().Equal("don't", "e", "mail", "john.smith", "example.com", "about", "3.14", "or", "1,000", "items_v2", "resume", "aeon", "strasse");
        Words("合同终止 カタカナ ひらがな").Should().Equal("合", "同", "终", "止", "カタカナ", "ひ", "ら", "が", "な");
        Words("end. Next").Should().Equal("end", "next");
    }

    [Fact]
    public void Phrase_highlights_as_one_span_and_never_marks_its_words_alone()
    {
        var text = "The price increase was approved. Later price adjustments followed; an increase too.";
        var hits = Scan("\"price increase\"", text);

        hits.Should().ContainSingle();
        text[(int)hits[0].Start..(int)hits[0].End].Should().Be("price increase");
    }

    [Fact]
    public void Proximity_highlights_from_the_first_to_the_last_word_in_either_order()
    {
        var text = "termination of the agreement. Later: the agreement shall survive termination. termination alone";
        var hits = Scan("termination W/3 agreement", text);

        hits.Select(h => text[(int)h.Start..(int)h.End]).Should().Equal(
            "termination of the agreement", "agreement shall survive termination");
    }

    [Fact]
    public void Proximity_respects_the_distance()
    {
        Scan("apple W/2 iphone", "apple one iphone").Should().ContainSingle();
        Scan("apple W/1 iphone", "apple one iphone").Should().BeEmpty();
        Scan("apple W/1 iphone", "iphone apple").Should().ContainSingle();
    }

    [Fact]
    public void Proximity_operands_may_be_phrases_wildcards_and_alternatives()
    {
        var text = "The board approved the price increase today; pears near the iPhone.";
        Spans("\"price increase\" W/3 approv*", text).Should().Equal("approved the price increase");
        Spans("(apple OR pear*) W/3 iphone", text).Should().Equal("pears near the iPhone");
    }

    [Fact]
    public void Terms_wildcards_and_case_or_accent_variants_match_like_search()
    {
        var text = "Terminate, TERMINATION, résumé and Resume; terms.";
        Spans("terminat*", text).Should().Equal("Terminate", "TERMINATION");
        Spans("resume", text).Should().Equal("résumé", "Resume");
        Spans("term", text).Should().BeEmpty();
    }

    [Fact]
    public void Negated_and_other_field_clauses_are_not_highlighted()
    {
        var units = HitUnits.FromQuery(QueryParser.Parse("contract AND NOT draft AND custodian:smith AND text:\"trade secret\"").Ast!);

        units.Select(u => (u.Kind, u.Label)).Should().Equal((HitUnitKind.Term, "contract"), (HitUnitKind.Phrase, "trade secret"));
    }

    [Theory]
    [InlineData("termination", HitUnitKind.Term)]
    [InlineData("price increase", HitUnitKind.Phrase)]
    [InlineData("\"price increase\"", HitUnitKind.Phrase)]
    [InlineData("terminat*", HitUnitKind.Wildcard)]
    [InlineData("price W/3 increase", HitUnitKind.Proximity)]
    public void Highlight_set_terms_are_one_unit_each(string expression, HitUnitKind kind)
    {
        var (units, problems) = HitUnits.FromHighlightTerm(expression);

        problems.Should().BeEmpty();
        units.Should().ContainSingle().Which.Kind.Should().Be(kind);
    }

    [Theory]
    [InlineData("te*", HitUnits.LeadingWildcardCode)]
    [InlineData("NOT draft", HitUnits.UnsupportedCode)]
    [InlineData("custodian:smith", HitUnits.UnsupportedCode)]
    [InlineData("\"unterminated", QueryErrorCodes.UnterminatedPhrase)]
    [InlineData("!!!", QueryErrorCodes.UnsupportedSyntax)]
    public void Invalid_highlight_set_terms_are_reported(string expression, string code)
    {
        var (units, problems) = HitUnits.FromHighlightTerm(expression);

        units.Should().BeEmpty();
        problems.Should().NotBeEmpty().And.Subject.First().Code.Should().Be(code);
    }

    [Fact]
    public void Hits_are_the_same_whatever_the_text_is_cut_into()
    {
        var text = string.Concat(Enumerable.Repeat("the price increase of termination, then the agreement terminated. ", 200));
        var whole = Scan("\"price increase\" OR terminat* OR agreement W/4 termination", text);
        foreach (var size in new[] { 1, 7, 64, 1000 })
        {
            var scanner = new TermHitScanner(HitUnits.FromQuery(QueryParser.Parse("\"price increase\" OR terminat* OR agreement W/4 termination").Ast!));
            var parts = new List<TermHit>();
            for (var i = 0; i < text.Length; i += size)
            {
                parts.AddRange(scanner.Append(text.Substring(i, Math.Min(size, text.Length - i))));
            }

            parts.AddRange(scanner.Complete());
            parts.OrderBy(h => h.Start).ThenBy(h => h.Unit).Should().Equal(whole, "parts of {0} characters", size);
        }
    }

    [Fact]
    public void A_text_of_more_than_a_million_characters_scans_in_bounded_time_and_memory()
    {
        var line = "Lorem ipsum dolor sit amet, the termination clause of the agreement applies; price increase noted.\n";
        var units = HitUnits.FromQuery(QueryParser.Parse("termination OR \"price increase\" OR agreement W/5 termination OR claus*").Ast!);
        var scanner = new TermHitScanner(units);
        var builder = new StringBuilder(256 * 1024);
        long hits = 0;
        var watch = Stopwatch.StartNew();
        for (var chunk = 0; chunk < 16; chunk++)
        {
            builder.Clear();
            while (builder.Length < 128 * 1024)
            {
                builder.Append(line);
            }

            hits += scanner.Append(builder.ToString()).Count;
        }

        hits += scanner.Complete().Count;
        watch.Stop();

        scanner.Length.Should().BeGreaterThan(2_000_000);
        hits.Should().Be(4 * (scanner.Length / line.Length));
        // Generous: tens of milliseconds per MB on a laptop; the gate is "no timeout", not a benchmark (Q-70).
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    private static List<string> Words(string text) => [.. HitTextAnalyzer.Tokenize(text).Select(t => t.Folded)];

    private static List<TermHit> Scan(string query, string text)
    {
        var scanner = new TermHitScanner(HitUnits.FromQuery(QueryParser.Parse(query).Ast!));
        return [.. scanner.Append(text).Concat(scanner.Complete()).OrderBy(h => h.Start).ThenBy(h => h.Unit)];
    }

    private static List<string> Spans(string query, string text) => [.. Scan(query, text).Select(h => text[(int)h.Start..(int)h.End])];
}
