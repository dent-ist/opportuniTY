namespace Opportunity.DataGenerator.Corpus.Randomness;

/// <summary>
/// Platform-independent 64-bit mixing (SplitMix64 finaliser). Every random stream is keyed by a hash of
/// (seed, stream tag, indices), so any entity can be regenerated in isolation, independent of thread count.
/// </summary>
public static class StableHash
{
    public static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public static ulong Combine(ulong a, ulong b) => Mix(a ^ Mix(b ^ 0x632BE59BD9B4E019UL));

    public static ulong Of(ulong seed, StreamTag tag, ulong a = 0, ulong b = 0, ulong c = 0)
    {
        ulong h = Combine(Mix(seed), (ulong)tag);
        h = Combine(h, a);
        h = Combine(h, b);
        return Combine(h, c);
    }
}

/// <summary>Stream identifiers. Values are part of the output contract: never renumber.</summary>
public enum StreamTag : ulong
{
    UnitPlan = 1,
    UnitContent = 2,
    FamilyContent = 3,
    DocumentContent = 4,
    Copies = 5,
    ChunkShuffle = 6,
    People = 7,
    Vocabulary = 8,
    TextBody = 9,
    TextVariant = 10,
    ContentKey = 11,
    Md5 = 12,
    Sha256 = 13,
    Thread = 14,
    MessageId = 15,
    NearDuplicateCluster = 16,
    Needles = 17,
    Filler = 18,
    ConversationIndex = 19,
    FamilyKey = 20,
    VolumeDefect = 21,
    VolumeChoice = 22,
}
