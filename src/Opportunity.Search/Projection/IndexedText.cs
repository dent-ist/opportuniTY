using System.Text;

using Microsoft.Extensions.Configuration;

namespace Opportunity.Search.Projection;

/// <summary>Extracted text as indexed: at most the cap, with the truncation flag (ADR-007 R9, R10).</summary>
public sealed record ProjectionText(string Text, bool Truncated);

/// <summary>Projection settings, section <c>Search</c>.</summary>
public sealed class ProjectionOptions
{
    public const string SectionName = "Search";
    public const int DefaultIndexedTextCap = 10_000_000;
    public const int MaxIndexedTextCap = 50_000_000;

    /// <summary>Characters of extracted text the projection indexes (Q-29, ADR-007 R9). Installation-configurable.</summary>
    public int IndexedTextCap { get; set; } = DefaultIndexedTextCap;

    public static ProjectionOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new ProjectionOptions();
        configuration.GetSection(SectionName).Bind(options);
        return options;
    }

    public void Validate()
    {
        if (IndexedTextCap is < IndexedText.CutWindow or > MaxIndexedTextCap)
        {
            throw new InvalidOperationException(
                $"{SectionName}:IndexedTextCap must be between {IndexedText.CutWindow} and {MaxIndexedTextCap} characters.");
        }
    }
}

/// <summary>The text cap of ADR-007 R9.</summary>
public static class IndexedText
{
    /// <summary>The cut is made at the last whitespace within this many characters before the cap.</summary>
    public const int CutWindow = 1_000;

    /// <summary>
    /// <paramref name="text"/> cut to at most <paramref name="cap"/> UTF-16 code units: at the last whitespace within
    /// the final <see cref="CutWindow"/> characters before the cap, otherwise at the cap, never inside a surrogate pair.
    /// </summary>
    public static ProjectionText Cap(string text, int cap)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, 1);
        if (text.Length <= cap)
        {
            return new ProjectionText(text, false);
        }

        var windowStart = Math.Max(0, cap - CutWindow);
        var cut = cap;
        for (var i = cap - 1; i >= windowStart; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                cut = i;
                break;
            }
        }

        if (cut == cap && char.IsHighSurrogate(text[cap - 1]))
        {
            cut--;
        }

        return new ProjectionText(text[..cut], true);
    }

    /// <summary>Reads at most <paramref name="cap"/> + 1 characters (enough to know whether to cut) and caps them.</summary>
    public static async Task<ProjectionText> ReadAsync(TextReader reader, int cap, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, 1);
        var limit = cap + 1;
        var builder = new StringBuilder(Math.Min(limit, 1 << 20));
        var buffer = new char[Math.Min(limit, 81_920)];
        while (builder.Length < limit)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit - builder.Length)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            builder.Append(buffer, 0, read);
        }

        return Cap(builder.ToString(), cap);
    }
}
