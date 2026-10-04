using System.Security.Cryptography;
using System.Text;

namespace Opportunity.Core.Documents;

/// <summary>Who formed a duplicate group (ADR-009 R15). Stored as smallint.</summary>
public enum DuplicateGroupSource : short
{
    /// <summary>A mapped upstream duplicate group value (R13); never rewritten by computed grouping.</summary>
    Upstream = 1,

    /// <summary>The optional family-level grouping job (R14, E09-T04).</summary>
    Computed = 2,
}

/// <summary>Which value drove grouping (ADR-009 R15); also which upstream hash a document holds. Stored as smallint.</summary>
public enum DuplicateHashKind : short
{
    UpstreamGroup = 1,
    UpstreamDedupeHash = 2,
    UpstreamEmailHash = 3,
    Sha256Native = 4,
}

/// <summary>A duplicate group as the writer records it: its deterministic id and the value that formed it.</summary>
public sealed record DuplicateGroupKey(Guid DuplicateGroupId, DuplicateGroupSource Source, DuplicateHashKind HashKind, string HashValue);

/// <summary>An email thread as the writer records it: its deterministic id and the value that formed it.</summary>
public sealed record EmailThreadKey(Guid EmailThreadId, EmailThreadSource Source, string ThreadKey);

/// <summary>
/// Deterministic relationship ids (ADR-009 R13, R14, R18): UUIDv5 (RFC 9562 §5.5) of
/// <c>"{workspaceId:D}:{label}:{value}"</c> in <see cref="Namespace"/>. Only the value is free-form and it comes last,
/// so the name is unambiguous. A re-import or a rebuild from PostgreSQL gives identical ids. Never change the namespace
/// or the labels: stored ids would no longer match.
/// </summary>
public static class RelationshipIds
{
    /// <summary>The opportuniTY relationship namespace.</summary>
    public static readonly Guid Namespace = new("8f0b6a52-3c1e-4d7a-9b25-61e4c0d3a7f9");

    /// <summary>Longest upstream group or thread value accepted (the stored key's limit).</summary>
    public const int MaxUpstreamValueLength = 255;

    public static Guid DuplicateGroup(Guid workspaceId, DuplicateHashKind kind, string value) => Create(workspaceId, kind switch
    {
        DuplicateHashKind.UpstreamGroup => "upstream",
        DuplicateHashKind.UpstreamDedupeHash => "dedupe-hash",
        DuplicateHashKind.UpstreamEmailHash => "email-hash",
        DuplicateHashKind.Sha256Native => "sha256",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    }, value);

    public static Guid EmailThread(Guid workspaceId, EmailThreadSource source, string value) => Create(workspaceId, source switch
    {
        EmailThreadSource.Upstream => "thread",
        EmailThreadSource.ConversationIndex => "conversation-index",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    }, value);

    /// <summary>RFC 9562 version-5 (SHA-1, name-based) UUID.</summary>
    public static Guid NameBased(Guid namespaceId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[16 + nameBytes.Length];
        namespaceId.TryWriteBytes(input, bigEndian: true, out _);
        nameBytes.CopyTo(input, 16);
        Span<byte> hash = stackalloc byte[20];
#pragma warning disable CA5350 // RFC 9562 defines version 5 with SHA-1; it names, it does not protect.
        SHA1.HashData(input, hash);
#pragma warning restore CA5350
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    private static Guid Create(Guid workspaceId, string label, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("A relationship id needs a workspace.", nameof(workspaceId));
        }

        return NameBased(Namespace, $"{workspaceId:D}:{label}:{value}");
    }
}

/// <summary>
/// The MAPI <c>PR_CONVERSATION_INDEX</c> (ADR-009 R19): a 22-byte header (reserved byte, FILETIME, thread GUID) followed by
/// 5-byte child blocks, one per reply/forward. Stored upper-case hex; exports that write it as base64 are converted.
/// </summary>
public static class ConversationIndex
{
    public const int HeaderBytes = 22;
    public const int ChildBlockBytes = 5;

    /// <summary>Longest index accepted (header plus 1,000 child blocks).</summary>
    public const int MaxBytes = HeaderBytes + (1000 * ChildBlockBytes);

    /// <summary>Normalizes hex (optional <c>0x</c>, spaces or dashes) or base64 input to upper-case hex.</summary>
    /// <param name="fromBase64">The input was base64, not hex.</param>
    public static bool TryNormalize(string? input, out string hex, out bool fromBase64)
    {
        hex = string.Empty;
        fromBase64 = false;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();
        var compact = new StringBuilder(trimmed.Length);
        var start = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
        var allHex = true;
        for (var i = start; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c is ' ' or '-')
            {
                continue;
            }

            allHex &= char.IsAsciiHexDigit(c);
            compact.Append(c);
        }

        byte[]? bytes = null;
        if (allHex && compact.Length % 2 == 0)
        {
            bytes = Convert.FromHexString(compact.ToString());
        }
        else
        {
            var buffer = new byte[(trimmed.Length * 3 / 4) + 3];
            if (Convert.TryFromBase64String(trimmed, buffer, out var written))
            {
                bytes = buffer[..written];
                fromBase64 = true;
            }
        }

        if (bytes is null || bytes.Length < HeaderBytes || bytes.Length > MaxBytes)
        {
            fromBase64 = false;
            return false;
        }

        hex = Convert.ToHexString(bytes);
        return true;
    }

    /// <summary>The 22-byte header shared by every message of a conversation, as upper-case hex.</summary>
    public static string ThreadRoot(string normalizedHex)
    {
        ArgumentNullException.ThrowIfNull(normalizedHex);
        if (normalizedHex.Length < HeaderBytes * 2)
        {
            throw new ArgumentException("A conversation index has at least a 22-byte header.", nameof(normalizedHex));
        }

        return normalizedHex[..(HeaderBytes * 2)];
    }
}
