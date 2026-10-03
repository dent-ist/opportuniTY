using System.Security.Cryptography;

namespace Opportunity.Application.Storage;

/// <summary>A SHA-256 digest, the integrity hash of every stored object (ADR-011 §2.1). Formats as 64 lowercase hex.</summary>
public readonly struct Sha256Digest : IEquatable<Sha256Digest>
{
    public const int HexLength = 64;

    private readonly string? _hex;

    private Sha256Digest(string hex) => _hex = hex;

    public string Hex => _hex ?? throw new InvalidOperationException("Uninitialized digest.");

    public static Sha256Digest FromBytes(ReadOnlySpan<byte> hash)
    {
        if (hash.Length != SHA256.HashSizeInBytes)
        {
            throw new ArgumentException("A SHA-256 hash is 32 bytes.", nameof(hash));
        }

        return new Sha256Digest(Convert.ToHexStringLower(hash));
    }

    public static Sha256Digest Parse(string hex) =>
        TryParse(hex, out var digest) ? digest : throw new FormatException("Expected 64 lowercase hex characters.");

    public static bool TryParse(string? hex, out Sha256Digest digest)
    {
        digest = default;
        if (hex is null || hex.Length != HexLength || !hex.All(IsLowerHex))
        {
            return false;
        }

        digest = new Sha256Digest(hex);
        return true;
    }

    public static Sha256Digest Compute(ReadOnlySpan<byte> content) => FromBytes(SHA256.HashData(content));

    public static async Task<Sha256Digest> ComputeAsync(Stream content, CancellationToken cancellationToken = default) =>
        FromBytes(await SHA256.HashDataAsync(content, cancellationToken).ConfigureAwait(false));

    public byte[] ToBytes() => Convert.FromHexString(Hex);

    public string ToBase64() => Convert.ToBase64String(ToBytes());

    internal static bool IsLowerHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f');

    public bool Equals(Sha256Digest other) => string.Equals(_hex, other._hex, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Sha256Digest other && Equals(other);

    public override int GetHashCode() => _hex is null ? 0 : StringComparer.Ordinal.GetHashCode(_hex);

    public override string ToString() => _hex ?? string.Empty;

    public static bool operator ==(Sha256Digest left, Sha256Digest right) => left.Equals(right);

    public static bool operator !=(Sha256Digest left, Sha256Digest right) => !left.Equals(right);
}
