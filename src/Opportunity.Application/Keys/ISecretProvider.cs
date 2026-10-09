using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Opportunity.Application.Keys;

/// <summary>
/// Reads named secrets (ADR-015 D10.1): Docker/Compose secret files (the default, <c>/run/secrets/{name}</c>), the
/// <c>*_FILE</c> environment convention, or an external store (Vault, Azure Key Vault, AWS Secrets Manager) through an
/// adapter registered in place of the built-in providers. Callers never log, trace or audit the value.
/// </summary>
public interface ISecretProvider
{
    /// <summary>The secret, or null when no source defines it.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid secret name.</exception>
    ValueTask<SecretValue?> GetAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>Secret names: <c>[A-Za-z0-9][A-Za-z0-9._-]{0,127}</c>, so a name can never escape the secrets directory.</summary>
public static partial class SecretNames
{
    public static bool IsValid(string? name) => name is not null && Pattern().IsMatch(name);

    public static string Validate(string? name) =>
        IsValid(name) ? name! : throw new ArgumentException("A secret name is [A-Za-z0-9][A-Za-z0-9._-]{0,127}.", nameof(name));

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>
/// Secret bytes. <see cref="ToString"/> never reveals them, so a secret that reaches a log message, an exception or a
/// configuration dump by accident prints as redacted. <see cref="Dispose"/> zeroes the buffer.
/// </summary>
public sealed class SecretValue : IDisposable
{
    private readonly byte[] _bytes;

    public SecretValue(ReadOnlySpan<byte> bytes)
    {
        _bytes = bytes.ToArray();
    }

    public ReadOnlySpan<byte> Bytes => _bytes;

    public int Length => _bytes.Length;

    /// <summary>The value as UTF-8 text, for credentials that are strings (passwords, connection strings).</summary>
    public string Reveal() => Encoding.UTF8.GetString(_bytes);

    public override string ToString() => "SecretValue(redacted)";

    public void Dispose() => CryptographicOperations.ZeroMemory(_bytes);
}
