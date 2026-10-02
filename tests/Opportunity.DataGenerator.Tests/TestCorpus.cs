using System.Security.Cryptography;

using Opportunity.DataGenerator.Corpus.Generation;
using Opportunity.DataGenerator.Corpus.Model;
using Opportunity.DataGenerator.Corpus.Output;
using Opportunity.DataGenerator.Corpus.Profiles;
using Opportunity.DataGenerator.Corpus.Text;

namespace Opportunity.DataGenerator.Tests;

internal static class TestCorpus
{
    public static CorpusProfile Profile(long documents) => ProfileSerializer.WithDocumentCount(new CorpusProfile(), documents);

    /// <summary>Profile with containers disabled, so family-size statistics are tight at small sizes.</summary>
    public static CorpusProfile NoContainers(long documents) => ProfileSerializer.Parse($$"""
        { "documentCount": {{documents}}, "families": { "emailContainerShare": 0, "edocContainerShare": 0 } }
        """);

    /// <summary>Small text sizes so text can be rendered inline in tests.</summary>
    public static CorpusProfile SmallText(long documents) => ProfileSerializer.Parse($$"""
        {
          "documentCount": {{documents}},
          "text": { "medianBytes": 3000, "p99Bytes": 40000, "maxBytes": 200000 },
          "needles": [ { "term": "zyqvoltran", "docRate": 0.05, "maxOccurrences": 3 },
                       { "term": "plimsorbex", "docRate": 0.05, "scope": "attachmentsOnly" },
                       { "term": "vex-tomarin", "docRate": 0.05 } ],
          "proximityPairs": [ { "first": "kelvatron", "second": "brisomund", "nearDistance": 3, "farDistance": 12, "nearDocRate": 0.05, "farDocRate": 0.05 } ]
        }
        """);

    public static List<GeneratedDocument> Documents(CorpusProfile profile, ulong seed, int threads = 1) =>
        [.. new CorpusGenerator(profile, seed, threads).GenerateDocuments()];

    public static (byte[] Jsonl, CorpusStatistics Stats) Jsonl(CorpusProfile profile, ulong seed, int threads, bool inlineText = false)
    {
        var context = new GenerationContext(profile, seed);
        var stats = new CorpusStatistics(context);
        using var buffer = new MemoryStream();
        using (var writer = new JsonlCorpusWriter(new NonClosingStream(buffer), "documents.jsonl", context.Catalog,
                   inlineText ? new TextSynthesizer(context.Vocabulary) : null))
        {
            foreach (GeneratedChunk chunk in new CorpusGenerator(context, threads).GenerateChunks())
            {
                stats.Write(chunk);
                writer.Write(chunk);
            }

            writer.Complete();
        }

        return (buffer.ToArray(), stats);
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    }
}
