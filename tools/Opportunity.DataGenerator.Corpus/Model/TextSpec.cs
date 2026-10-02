namespace Opportunity.DataGenerator.Corpus.Model;

/// <summary>A word planted at a fixed word position of a document's own body (never in quoted text).</summary>
public readonly record struct PlantedWord(int Position, string Word);

/// <summary>
/// Recipe for a document's extracted text. Text is synthesised on demand (see <c>TextSynthesizer</c>) so that
/// documents of any size cost only this descriptor in memory. Layout: [Prefix] Body [Separator Quoted], padded
/// with spaces to exactly <see cref="TotalBytes"/> UTF-8 bytes.
/// </summary>
public sealed class TextSpec
{
    public static readonly TextSpec Empty = new() { TotalBytes = 0 };

    public required long TotalBytes { get; init; }

    /// <summary>Header block (e.g. email From/To/Subject lines), truncated if longer than the budget.</summary>
    public string? Prefix { get; init; }

    public ulong BodySeed { get; init; }

    public long BodyBytes { get; init; }

    /// <summary>Non-zero for near-duplicate variants: perturbs words of the shared base body.</summary>
    public ulong VariantSeed { get; init; }

    public double WordChangeRate { get; init; }

    public ulong QuotedSeed { get; init; }

    public long QuotedBytes { get; init; }

    public IReadOnlyList<PlantedWord> Planted { get; init; } = [];
}
