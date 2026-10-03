using System.Text;
using System.Text.Json;

using Opportunity.DataGenerator.Corpus.Output;
using Opportunity.DataGenerator.Corpus.Profiles;
using Opportunity.DataGenerator.Corpus.Volumes;

namespace Opportunity.DataGenerator.Tests;

/// <summary>A generated corpus with load-file volumes in a temporary directory.</summary>
internal sealed class VolumeRun : IDisposable
{
    private VolumeRun(string root, VolumeOptions options)
    {
        Root = root;
        Options = options;
    }

    public string Root { get; }

    public VolumeOptions Options { get; }

    public string Volume(string name = "VOL001") => Path.Combine(Root, name);

    public static VolumeRun Create(CorpusProfile profile, ulong seed, VolumeOptions options, int threads = 1)
    {
        string root = Path.Combine(Path.GetTempPath(), "opp-volumes-" + Guid.NewGuid().ToString("N"));
        CorpusRunner.Run(profile, seed, root, new CorpusRunOptions
        {
            Threads = threads,
            WriteGroundTruth = false,
            SinkFactories = [context => new VolumeWriter(context, root, options)],
        });
        return new VolumeRun(root, options);
    }

    /// <summary>Resolves a volume-relative load-file path (backslashes) to a local path.</summary>
    public string Resolve(string relativePath, string volume = "VOL001") =>
        Path.Combine(Volume(volume), relativePath.Replace('\\', Path.DirectorySeparatorChar));

    public byte[] Dat(string volume = "VOL001") => File.ReadAllBytes(Path.Combine(Volume(volume), "DATA", volume + ".dat"));

    public List<List<string>> ParseDat(string volume = "VOL001") =>
        DatTestReader.ParseAll(LoadFileEncodings.GetString(Options.DatEncoding, Dat(volume)), Options.Delimiters);

    public List<string[]> Opt(string volume = "VOL001") =>
        [.. File.ReadAllLines(Path.Combine(Volume(volume), "DATA", volume + ".opt"), Encoding.Latin1).Select(l => l.Split(','))];

    public List<JsonElement> Defects() =>
        [.. File.ReadAllLines(Path.Combine(Root, VolumeWriter.DefectsFile)).Select(l => JsonDocument.Parse(l).RootElement.Clone())];

    public JsonElement Manifest() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, VolumeWriter.ManifestFile))).RootElement.Clone();

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

/// <summary>
/// Independent, lenient DAT reader used to verify writer output: qualified values, doubled-qualifier escapes,
/// newline-character conversion, CRLF row breaks outside qualifiers; a lone qualifier inside a value is literal.
/// </summary>
internal static class DatTestReader
{
    public static List<List<string>> ParseAll(string text, DelimiterProfile d)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var value = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            value.Clear();
            if (text[i] == d.Quote)
            {
                i++;
                while (i < text.Length)
                {
                    char c = text[i];
                    if (c == d.Quote)
                    {
                        if (i + 1 < text.Length && text[i + 1] == d.Quote)
                        {
                            value.Append(c);
                            i += 2;
                            continue;
                        }

                        if (i + 1 == text.Length || text[i + 1] == d.Column || text[i + 1] == '\r')
                        {
                            i++;
                            break;
                        }
                    }

                    value.Append(c);
                    i++;
                }
            }
            else
            {
                while (i < text.Length && text[i] != d.Column && text[i] != '\r')
                {
                    value.Append(text[i++]);
                }
            }

            string v = value.ToString();
            row.Add(d.Newline is { } nl ? v.Replace(nl, '\n') : v);
            if (i < text.Length && text[i] == d.Column)
            {
                i++;
                continue;
            }

            if (i + 1 < text.Length && text[i] == '\r' && text[i + 1] == '\n')
            {
                i += 2;
            }

            rows.Add(row);
            row = [];
        }

        return rows;
    }

    /// <summary>What a reader should get back for a source value under the given profile and encoding.</summary>
    public static string Expected(string value, DelimiterProfile d, LoadFileEncoding encoding)
    {
        if (encoding == LoadFileEncoding.Windows1252)
        {
            value = LoadFileEncodings.GetString(encoding, LoadFileEncodings.GetBytes(encoding, value));
        }

        return d.Newline is { } nl
            ? value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace(nl, '\n')
            : value;
    }
}
