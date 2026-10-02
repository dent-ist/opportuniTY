using AwesomeAssertions;

using Opportunity.DataGenerator.Corpus.Profiles;

namespace Opportunity.DataGenerator.Tests;

public class ProfileTests
{
    [Fact]
    public void Checked_in_default_profile_matches_built_in_defaults()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "profiles", "enterprise-reference.json");
        CorpusProfile fromFile = ProfileSerializer.Load(path);

        ProfileSerializer.ComputeHash(fromFile).Should().Be(ProfileSerializer.ComputeHash(new CorpusProfile()));
        File.ReadAllText(path).ReplaceLineEndings("\n").TrimEnd().Should().Be(ProfileSerializer.ToJson(new CorpusProfile()).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Defaults_reproduce_the_section_29_enterprise_reference_shape()
    {
        var p = new CorpusProfile();
        p.Families.MeanFamilySize.Should().Be(3.0);
        p.Duplicates.Rate.Should().Be(0.20);
        p.Mix.EmailShare.Should().Be(0.60);
        p.Text.MedianBytes.Should().Be(5 * 1024);
        p.Text.P99Bytes.Should().Be(1024 * 1024);
        p.Text.MaxBytes.Should().BeGreaterThan(100L * 1024 * 1024);
        p.Families.MaxAttachmentDepth.Should().Be(3);
        p.Threads.MinLength.Should().Be(2);
        p.Threads.MaxLength.Should().Be(50);
        p.Recipients.MaxRecipients.Should().Be(500);
        p.Custodians.Count.Should().BeInRange(50, 5000);
    }

    [Fact]
    public void Hash_ignores_formatting_comments_and_explicit_defaults()
    {
        CorpusProfile a = ProfileSerializer.Parse("{}");
        CorpusProfile b = ProfileSerializer.Parse("""
            {
              // explicit default, trailing comma
              "duplicates": { "rate": 0.20, },
            }
            """);

        ProfileSerializer.ComputeHash(a).Should().Be(ProfileSerializer.ComputeHash(b));
        ProfileSerializer.ComputeHash(ProfileSerializer.WithDocumentCount(a, 5)).Should().NotBe(ProfileSerializer.ComputeHash(a));
    }

    [Fact]
    public void Unknown_properties_are_rejected()
    {
        Action act = () => ProfileSerializer.Parse("""{ "duplicates": { "rat": 0.2 } }""");
        act.Should().Throw<Exception>();
    }

    [Theory]
    [InlineData("""{ "duplicates": { "rate": 1.5 } }""")]
    [InlineData("""{ "families": { "meanFamilySize": 0.5 } }""")]
    [InlineData("""{ "families": { "meanFamilySize": 1.0 } }""")]
    [InlineData("""{ "text": { "medianBytes": 5000, "p99Bytes": 4000 } }""")]
    [InlineData("""{ "dates": { "timeZones": [ { "offset": "EST", "weight": 1 } ] } }""")]
    [InlineData("""{ "needles": [ { "term": "the", "docRate": 0.1 } ] }""")]
    [InlineData("""{ "profileVersion": 99 }""")]
    [InlineData("""{ "documentCount": 100000000000, "controlNumberDigits": 6 }""")]
    public void Invalid_profiles_are_rejected_with_a_reason(string json)
    {
        Action act = () => _ = new Corpus.Generation.GenerationContext(ProfileSerializer.Parse(json), 1);
        act.Should().Throw<ProfileValidationException>().Which.Errors.Should().NotBeEmpty();
    }
}