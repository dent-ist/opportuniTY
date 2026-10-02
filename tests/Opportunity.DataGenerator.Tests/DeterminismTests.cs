using AwesomeAssertions;

using Opportunity.DataGenerator.Corpus;
using Opportunity.DataGenerator.Corpus.Output;
using Opportunity.DataGenerator.Corpus.Profiles;

namespace Opportunity.DataGenerator.Tests;

public class DeterminismTests
{
    [Fact]
    public void Same_seed_and_profile_give_byte_identical_output_for_any_thread_count()
    {
        CorpusProfile profile = TestCorpus.Profile(20_000);

        byte[] single = TestCorpus.Jsonl(profile, 42, threads: 1).Jsonl;
        byte[] parallel = TestCorpus.Jsonl(profile, 42, threads: 4).Jsonl;
        byte[] again = TestCorpus.Jsonl(profile, 42, threads: 3).Jsonl;

        single.Length.Should().BeGreaterThan(1_000_000);
        TestCorpus.Sha256(parallel).Should().Be(TestCorpus.Sha256(single));
        TestCorpus.Sha256(again).Should().Be(TestCorpus.Sha256(single));
    }

    [Fact]
    public void Inline_text_output_is_byte_identical_across_thread_counts()
    {
        CorpusProfile profile = TestCorpus.SmallText(1_500);

        byte[] single = TestCorpus.Jsonl(profile, 9, threads: 1, inlineText: true).Jsonl;
        byte[] parallel = TestCorpus.Jsonl(profile, 9, threads: 4, inlineText: true).Jsonl;

        TestCorpus.Sha256(parallel).Should().Be(TestCorpus.Sha256(single));
    }

    [Fact]
    public void Different_seeds_or_profiles_give_different_output()
    {
        CorpusProfile profile = TestCorpus.Profile(2_000);
        string a = TestCorpus.Sha256(TestCorpus.Jsonl(profile, 1, 1).Jsonl);
        string b = TestCorpus.Sha256(TestCorpus.Jsonl(profile, 2, 1).Jsonl);
        string c = TestCorpus.Sha256(TestCorpus.Jsonl(TestCorpus.NoContainers(2_000), 1, 1).Jsonl);

        a.Should().NotBe(b);
        a.Should().NotBe(c);
    }

    [Fact]
    public void Smaller_corpus_is_not_required_to_be_a_prefix_but_is_itself_stable()
    {
        // The planner fits units into the document budget, so corpora of different sizes may diverge near the end;
        // each size must still be reproducible on its own.
        CorpusProfile profile = TestCorpus.Profile(777);
        TestCorpus.Sha256(TestCorpus.Jsonl(profile, 5, 2).Jsonl).Should().Be(TestCorpus.Sha256(TestCorpus.Jsonl(profile, 5, 1).Jsonl));
    }

    [Fact]
    public void Manifest_and_outputs_are_byte_identical_across_runs_and_thread_counts()
    {
        string root = Path.Combine(Path.GetTempPath(), "opp-datagen-" + Guid.NewGuid().ToString("N"));
        try
        {
            CorpusProfile profile = TestCorpus.Profile(5_000);
            CorpusRunner.Run(profile, 11, Path.Combine(root, "a"), new CorpusRunOptions { Threads = 1 });
            CorpusRunner.Run(profile, 11, Path.Combine(root, "b"), new CorpusRunOptions { Threads = 4 });

            foreach (string file in new[] { CorpusRunner.ManifestFile, CorpusRunner.DocumentsFile, CorpusRunner.GroundTruthFile })
            {
                File.ReadAllBytes(Path.Combine(root, "b", file)).Should().Equal(File.ReadAllBytes(Path.Combine(root, "a", file)), file);
            }

            string manifest = File.ReadAllText(Path.Combine(root, "a", CorpusRunner.ManifestFile));
            manifest.Should().Contain("\"seed\": 11")
                .And.Contain(ProfileSerializer.ComputeHash(profile))
                .And.Contain($"\"version\": \"{GeneratorInfo.Version}\"")
                .And.Contain("\"documents\": 5000");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// Pins the output of a small corpus. If this fails after an intentional change to generation, bump
    /// <see cref="GeneratorInfo.Version"/> (corpus caches key on it) and update the expected hash.
    /// </summary>
    [Fact]
    public void Golden_output_is_stable_for_this_generator_version()
    {
        GeneratorInfo.Version.Should().Be("1.0.0");
        string hash = TestCorpus.Sha256(TestCorpus.Jsonl(TestCorpus.SmallText(300), 2026, 1, inlineText: true).Jsonl);
        hash.Should().Be(GoldenHash);
    }

    private const string GoldenHash = "e50d71880c3888d1e3b5eefac599cbc9d57320c4050b8356190ab0e0b82fe06c";
}