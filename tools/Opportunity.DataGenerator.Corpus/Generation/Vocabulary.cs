using Opportunity.DataGenerator.Corpus.Profiles;
using Opportunity.DataGenerator.Corpus.Randomness;

namespace Opportunity.DataGenerator.Corpus.Generation;

/// <summary>
/// Zipf-ranked vocabulary: common English function words first, then invented pseudo-words (including a few
/// hyphenated and apostrophe forms). Keyed by <see cref="TextProfile.VocabularySeed"/> so it is stable across corpus
/// seeds. Needle terms (and their hyphen/apostrophe parts) are excluded so planted counts are exact.
/// </summary>
public sealed class Vocabulary
{
    private static readonly string[] FunctionWords =
    [
        "the", "of", "and", "to", "a", "in", "is", "that", "for", "it", "as", "was", "with", "be", "by", "on", "not",
        "he", "this", "are", "or", "his", "from", "at", "which", "but", "have", "an", "had", "they", "you", "were",
        "their", "one", "all", "we", "can", "her", "has", "there", "been", "if", "more", "when", "will", "would",
        "who", "so", "no", "she", "other", "its", "may", "these", "about", "into", "than", "them", "only", "some",
        "could", "time", "out", "my", "do", "also", "our", "what", "up", "your", "new", "two", "any", "over", "per",
        "should", "please", "thanks", "regards", "meeting", "report", "review", "draft", "agreement", "contract",
        "invoice", "schedule", "update", "account", "budget", "project", "team", "call", "today", "tomorrow",
        "attached", "following", "question", "information", "approval", "payment", "deadline", "client",
    ];

    private readonly string[] _words;
    private readonly AliasTable _table;

    public Vocabulary(TextProfile text, IEnumerable<string> reservedTerms)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(reservedTerms);
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        foreach (string term in reservedTerms)
        {
            reserved.Add(term);
            foreach (string part in term.Split(['-', '\''], StringSplitOptions.RemoveEmptyEntries))
            {
                reserved.Add(part);
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var words = new List<string>(text.VocabularySize);
        foreach (string w in FunctionWords)
        {
            if (words.Count < text.VocabularySize && !reserved.Contains(w) && seen.Add(w))
            {
                words.Add(w);
            }
        }

        var rng = Rng.For((ulong)text.VocabularySeed, StreamTag.Vocabulary);
        while (words.Count < text.VocabularySize)
        {
            string w = rng.NextInt(100) switch
            {
                0 => Syllables.Word(rng, 1, 2) + "-" + Syllables.Word(rng, 1, 2),
                1 => Syllables.Word(rng, 1, 2) + "'s",
                _ => Syllables.Word(rng, 1, 4),
            };

            if (w.Length is >= 2 and <= 24 && !reserved.Contains(w) && seen.Add(w))
            {
                words.Add(w);
            }
        }

        _words = [.. words];
        _table = new AliasTable(AliasTable.ZipfWeights(_words.Length, text.VocabularyZipfExponent, offset: 1.7));
    }

    public int Count => _words.Length;

    public string this[int rank] => _words[rank];

    public string Sample(Rng rng) => _words[_table.Sample(rng)];

    public bool Contains(string word) => Array.IndexOf(_words, word) >= 0;
}