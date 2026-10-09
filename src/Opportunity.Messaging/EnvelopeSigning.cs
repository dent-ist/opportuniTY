using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Opportunity.Application.Keys;

namespace Opportunity.Messaging;

/// <summary>
/// Optional HMAC envelope signing (E05-T07, ADR-015 D9.5), section <c>Messaging:RabbitMq:Signing</c>. When enabled the
/// publisher signs every message with <see cref="KeyId"/> and every consumer rejects a message whose signature is missing,
/// invalid or under a key id it does not accept (dead-lettered, <c>Integrity.MessageRejected</c>). Keys come from
/// <see cref="ISecretProvider"/> as the secret <c>envelope-hmac-{keyId}</c> (at least 32 bytes), e.g. the Docker secret
/// <c>/run/secrets/envelope-hmac-k1</c> or <c>OPPORTUNITY_SECRET_ENVELOPE_HMAC_K1_FILE</c>. Rotation: add the new key to
/// every consumer's <see cref="AcceptedKeyIds"/>, then switch <see cref="KeyId"/> on the publishers, then drop the old id
/// once its messages have drained (docs/operations/message-trust.md). Signing is tamper evidence, not authority: the
/// worker still re-derives workspace and actor from PostgreSQL (D9.2).
/// </summary>
public sealed partial class MessageSigningOptions
{
    public const string SecretNamePrefix = "envelope-hmac-";
    public const int MinimumKeyBytes = 32;

    public bool Enabled { get; set; }

    /// <summary>Key id new messages are signed with (and that consumers accept).</summary>
    public string KeyId { get; set; } = "k1";

    /// <summary>Further key ids consumers accept, comma-separated (the rotation overlap).</summary>
    public string? AcceptedKeyIds { get; set; }

    public static string SecretName(string keyId) => SecretNamePrefix + keyId;

    /// <summary><see cref="KeyId"/> followed by the <see cref="AcceptedKeyIds"/>.</summary>
    public IReadOnlyList<string> VerificationKeyIds() =>
        [.. new[] { KeyId }.Concat((AcceptedKeyIds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)];

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        foreach (var id in VerificationKeyIds())
        {
            if (!KeyIdPattern().IsMatch(id))
            {
                throw new InvalidOperationException(
                    $"{RabbitMqOptions.SectionName}:Signing key ids are 1-32 characters [A-Za-z0-9_-] (got '{id}').");
            }
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyIdPattern();
}

/// <summary>Outcome of <see cref="EnvelopeSigner.VerifyAsync"/>.</summary>
public enum SignatureCheck
{
    Valid,
    Missing,
    Invalid,
    UnknownKey,
}

/// <summary>
/// HMAC-SHA256 over the canonical envelope: <c>"opportunity-envelope-v1\n" + destination queue + "\n" + body</c>. The
/// body is the exact serialized envelope, which the transport never changes on retry, dead-letter or parking, and the
/// destination binds a signature to the queue it was published for, so a valid message replayed onto another queue is
/// rejected. Headers: <see cref="TransportHeaders.SignatureKeyId"/> and <see cref="TransportHeaders.Signature"/>
/// (<c>v1.</c> + base64). Keys are read once per key id and kept in memory.
/// </summary>
public sealed class EnvelopeSigner
{
    private const string SignaturePrefix = "v1.";
    private static readonly byte[] Domain = "opportunity-envelope-v1\n"u8.ToArray();

    private readonly MessageSigningOptions _options;
    private readonly ISecretProvider? _secrets;
    private readonly HashSet<string> _accepted;
    private readonly ConcurrentDictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

    public EnvelopeSigner(MessageSigningOptions options, ISecretProvider? secrets)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
        _secrets = secrets;
        _accepted = [.. options.VerificationKeyIds()];
        if (options.Enabled && secrets is null)
        {
            throw new InvalidOperationException("Envelope signing is enabled but no ISecretProvider is registered for its keys.");
        }
    }

    /// <summary>A signer that neither signs nor verifies.</summary>
    public static EnvelopeSigner Disabled { get; } = new(new MessageSigningOptions(), null);

    public bool Enabled => _options.Enabled;

    /// <summary>Adds the signature headers for <paramref name="body"/> published to <paramref name="destination"/>.</summary>
    public async ValueTask SignAsync(
        string destination, ReadOnlyMemory<byte> body, IDictionary<string, object?> headers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (!Enabled)
        {
            return;
        }

        var key = await KeyAsync(_options.KeyId, cancellationToken).ConfigureAwait(false);
        headers[TransportHeaders.SignatureKeyId] = _options.KeyId;
        headers[TransportHeaders.Signature] = SignaturePrefix + Convert.ToBase64String(Mac(key, destination, body.Span));
    }

    /// <summary>Checks the signature headers of a delivery consumed from <paramref name="destination"/>.</summary>
    public async ValueTask<SignatureCheck> VerifyAsync(
        string destination, ReadOnlyMemory<byte> body, IDictionary<string, object?>? headers, CancellationToken cancellationToken)
    {
        var keyId = TransportHeaders.ReadString(headers, TransportHeaders.SignatureKeyId);
        var signature = TransportHeaders.ReadString(headers, TransportHeaders.Signature);
        if (string.IsNullOrEmpty(keyId) || string.IsNullOrEmpty(signature))
        {
            return SignatureCheck.Missing;
        }

        if (!_accepted.Contains(keyId))
        {
            return SignatureCheck.UnknownKey;
        }

        if (!signature.StartsWith(SignaturePrefix, StringComparison.Ordinal))
        {
            return SignatureCheck.Invalid;
        }

        var presented = new byte[32];
        if (!Convert.TryFromBase64String(signature[SignaturePrefix.Length..], presented, out var written) || written != presented.Length)
        {
            return SignatureCheck.Invalid;
        }

        var key = await KeyAsync(keyId, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(Mac(key, destination, body.Span), presented)
            ? SignatureCheck.Valid
            : SignatureCheck.Invalid;
    }

    private static byte[] Mac(byte[] key, string destination, ReadOnlySpan<byte> body)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        hmac.AppendData(Domain);
        hmac.AppendData(Encoding.UTF8.GetBytes(destination));
        hmac.AppendData("\n"u8);
        hmac.AppendData(body);
        return hmac.GetHashAndReset();
    }

    private async ValueTask<byte[]> KeyAsync(string keyId, CancellationToken cancellationToken)
    {
        if (_keys.TryGetValue(keyId, out var cached))
        {
            return cached;
        }

        var name = MessageSigningOptions.SecretName(keyId);
        using var secret = await _secrets!.GetAsync(name, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Envelope signing key '{keyId}' is not configured (secret '{name}').");
        if (secret.Length < MessageSigningOptions.MinimumKeyBytes)
        {
            throw new InvalidOperationException(
                $"Envelope signing key '{keyId}' is shorter than {MessageSigningOptions.MinimumKeyBytes} bytes (secret '{name}').");
        }

        return _keys.GetOrAdd(keyId, secret.Bytes.ToArray());
    }
}
