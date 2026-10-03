using System.Text.Json;

using Microsoft.AspNetCore.DataProtection;

namespace Opportunity.Security.Authentication;

/// <summary>The IdP tokens of a session. They stay on the server, encrypted at rest (ADR-015 D4.1).</summary>
internal sealed record SessionTokenSet(string? AccessToken, string? RefreshToken, string? IdToken, DateTimeOffset? AccessTokenExpiresAt)
{
    public static SessionTokenSet Empty { get; } = new(null, null, null, null);
}

/// <summary>Encrypts token sets with ASP.NET Core Data Protection (key ring shared through PostgreSQL, D10.4).</summary>
internal sealed class SessionTokenProtector(IDataProtectionProvider provider)
{
    private const string Purpose = "Opportunity.Security.SessionTokens.v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public byte[] Protect(SessionTokenSet tokens) =>
        _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(tokens, Json));

    /// <summary>Returns <see cref="SessionTokenSet.Empty"/> when the payload is missing or cannot be decrypted.</summary>
    public SessionTokenSet Unprotect(ReadOnlyMemory<byte> protectedTokens)
    {
        if (protectedTokens.IsEmpty)
        {
            return SessionTokenSet.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<SessionTokenSet>(_protector.Unprotect(protectedTokens.ToArray()), Json) ?? SessionTokenSet.Empty;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return SessionTokenSet.Empty;
        }
    }
}
