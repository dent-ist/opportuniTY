using System.Text;

using Opportunity.DataGenerator.Corpus.Randomness;

namespace Opportunity.DataGenerator.Corpus.KnownAnswer;

/// <summary>
/// Filler words for known-answer texts: two or three consonant-vowel syllables of lower-case ASCII letters, never
/// containing <c>j q x y z</c> or an apostrophe. Every planted term contains one of those, so filler can never hit a
/// planted term, a stem variant or a wildcard prefix by accident.
/// </summary>
public static class FillerWords
{
    private const string Consonants = "bcdfghklmnprstvw";
    private const string Vowels = "aeiou";

    public static string Next(Rng rng)
    {
        ArgumentNullException.ThrowIfNull(rng);
        var syllables = rng.NextInt(2, 3);
        var word = new StringBuilder(syllables * 2);
        for (var i = 0; i < syllables; i++)
        {
            word.Append(Consonants[rng.NextInt(Consonants.Length)]).Append(Vowels[rng.NextInt(Vowels.Length)]);
        }

        return word.ToString();
    }

    /// <summary><paramref name="count"/> filler words separated by single spaces (no punctuation).</summary>
    public static string Run(Rng rng, int count) => string.Join(' ', Enumerable.Range(0, count).Select(_ => Next(rng)));

    /// <summary>True when <paramref name="word"/> could be filler (used by tests to prove fillers never collide with terms).</summary>
    public static bool IsFiller(string word) =>
        word.Length is >= 4 and <= 6 && word.All(c => Consonants.Contains(c, StringComparison.Ordinal) || Vowels.Contains(c, StringComparison.Ordinal));
}
