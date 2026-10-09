using System.Text.RegularExpressions;

namespace Opportunity.Application.Keys;

/// <summary>
/// Removes credentials from free text before it leaves the process (ADR-015 D10.5): connection-string and
/// <c>key=value</c> secrets (<c>Password=</c>, <c>AccountKey=</c>, <c>client_secret=</c>, …), credentials in URLs
/// (<c>amqp://user:pass@host</c>), bearer tokens and JWTs, presigned-URL signatures (<c>X-Amz-Signature</c>,
/// <c>sig=</c>) and PEM private keys. Used by the telemetry log processor; it is a safety net behind the rule that code
/// never logs a secret, not a licence to do so.
/// </summary>
public static partial class SecretRedactor
{
    public const string Mask = "***";

    /// <summary>The text with every recognized credential replaced by <see cref="Mask"/>; the input when nothing matched.</summary>
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return text;
        }

        var result = PemPrivateKey().Replace(text, "-----PRIVATE KEY " + Mask + "-----");
        result = UrlCredentials().Replace(result, m => m.Groups["scheme"].Value + m.Groups["user"].Value + ":" + Mask + "@");
        result = Bearer().Replace(result, m => m.Groups["scheme"].Value + " " + Mask);
        result = Jwt().Replace(result, Mask);
        result = QueryCredential().Replace(result, m => m.Groups["name"].Value + Mask);
        result = KeyValueCredential().Replace(result, m => m.Groups["name"].Value + Mask);
        return result;
    }

    /// <summary>True when <see cref="Redact"/> would change <paramref name="text"/>.</summary>
    public static bool ContainsCredential(string text) => !string.Equals(Redact(text), text, StringComparison.Ordinal);

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(-----END [A-Z ]*PRIVATE KEY-----|$)", RegexOptions.CultureInvariant)]
    private static partial Regex PemPrivateKey();

    [GeneratedRegex(@"(?<scheme>\b[a-zA-Z][a-zA-Z0-9+.-]*://)(?<user>[^/\s:@]+):(?<pass>[^@\s/]+)@", RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"(?<scheme>\b(?i:bearer|basic))\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    [GeneratedRegex(
        @"(?<name>[?&](?i:x-amz-signature|x-amz-credential|x-amz-security-token|sig|se|skoid|sktid|code|access_token|id_token|refresh_token)=)[^&\s""'#]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex QueryCredential();

    [GeneratedRegex(
        @"(?<name>""?\b(?i:password|pwd|passwd|secret|secret[_-]?key|secretaccesskey|client[_-]?secret|account[_-]?key|shared[_-]?access[_-]?key|sharedaccesssignature|access[_-]?key|api[_-]?key|token|access[_-]?token|refresh[_-]?token|id[_-]?token|private[_-]?key)""?\s*[=:]\s*""?)[^;\s,&""'}]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueCredential();
}
