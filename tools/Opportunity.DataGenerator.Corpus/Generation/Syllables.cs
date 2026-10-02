using System.Text;

using Opportunity.DataGenerator.Corpus.Randomness;

namespace Opportunity.DataGenerator.Corpus.Generation;

/// <summary>Builds invented words and names from syllables and script samples. Nothing is drawn from real data.</summary>
internal static class Syllables
{
    private static readonly string[] Onsets =
    [
        "b", "c", "d", "f", "g", "h", "j", "k", "l", "m", "n", "p", "r", "s", "t", "v", "w", "z",
        "br", "cr", "dr", "st", "tr", "pl", "gr", "sh", "ch", "th", "qu", "sl", "fr", "kl",
    ];

    private static readonly string[] Vowels = ["a", "e", "i", "o", "u", "ai", "ea", "ou", "io", "y"];

    private static readonly string[] Codas = ["", "", "", "n", "r", "s", "l", "m", "x", "nd", "st", "rk"];

    private static readonly char[] AccentedVowels = ['á', 'é', 'í', 'ó', 'ú', 'ä', 'ö', 'ü', 'å', 'ø', 'ê', 'è'];

    private static readonly string[] AccentedConsonants = ["ñ", "ç", "ł", "ß", "ž", "š"];

    // Common CJK ideographs used as an alphabet for invented names (combinations are random).
    private const string CjkCharacters = "王李张刘陈杨黄赵吴周徐孙马朱胡郭何高林罗郑梁谢宋唐许韩冯邓曹彭曾肖田董袁潘于蒋蔡余杜叶程苏魏吕丁任沈姚卢姜崔钟谭陆汪范金石廖贾夏韦付方白邹孟熊秦邱江尹薛闫段雷侯龙史陶黎贺顾毛郝龚邵万钱严覃武戴莫孔向汤明华文军平国建伟芳秀英敏静丽强磊洋艳勇杰娟涛超霞";

    private const string ArabicLetters = "ابتثجحخدذرزسشصضطظعغفقكلمنهوي";

    private const string HebrewLetters = "אבגדהוזחטיכלמנסעפצקרשת";

    public static string Word(Rng rng, int minSyllables, int maxSyllables)
    {
        int count = rng.NextInt(minSyllables, maxSyllables);
        var sb = new StringBuilder(count * 4);
        for (int i = 0; i < count; i++)
        {
            sb.Append(rng.Pick(Onsets)).Append(rng.Pick(Vowels));
            if (i == count - 1 || rng.Chance(0.3))
            {
                sb.Append(rng.Pick(Codas));
            }
        }

        return sb.ToString();
    }

    public static string Capitalize(string word) =>
        word.Length == 0 ? word : string.Concat(char.ToUpperInvariant(word[0]).ToString(), word.AsSpan(1));

    public static string Accent(Rng rng, string word)
    {
        var sb = new StringBuilder(word);
        int changes = 0;
        for (int i = 0; i < sb.Length && changes < 2; i++)
        {
            if ("aeiou".Contains(sb[i], StringComparison.Ordinal) && rng.Chance(0.35))
            {
                sb[i] = rng.Pick(AccentedVowels);
                changes++;
            }
        }

        if (changes == 0)
        {
            sb.Append(rng.Pick(AccentedConsonants));
        }

        return sb.ToString();
    }

    public static string Cjk(Rng rng, int length)
    {
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            sb.Append(CjkCharacters[rng.NextInt(CjkCharacters.Length)]);
        }

        return sb.ToString();
    }

    public static string Rtl(Rng rng, bool arabic, int length)
    {
        string alphabet = arabic ? ArabicLetters : HebrewLetters;
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            sb.Append(alphabet[rng.NextInt(alphabet.Length)]);
        }

        return sb.ToString();
    }

    /// <summary>A short non-Latin or accented word for Unicode-bearing values.</summary>
    public static string UnicodeWord(Rng rng) => rng.NextInt(4) switch
    {
        0 => Capitalize(Accent(rng, Word(rng, 2, 3))),
        1 => Cjk(rng, rng.NextInt(2, 4)),
        2 => Rtl(rng, arabic: true, rng.NextInt(3, 6)),
        _ => Rtl(rng, arabic: false, rng.NextInt(3, 6)),
    };
}
