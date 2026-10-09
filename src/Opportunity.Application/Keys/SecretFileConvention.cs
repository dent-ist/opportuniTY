using System.Collections;

namespace Opportunity.Application.Keys;

/// <summary>
/// The Docker <c>*_FILE</c> convention for configuration (ADR-015 D10.1): an environment variable
/// <c>ConnectionStrings__App_FILE=/run/secrets/db-app</c> supplies configuration key <c>ConnectionStrings:App</c> from
/// the file's content (one trailing line break removed). Only names with a <c>__</c> section separator take part, so
/// unrelated variables such as <c>SSL_CERT_FILE</c> are left alone. Hosts add the result after the environment
/// variables, so a file wins over a plain variable of the same key. File contents are never logged.
/// </summary>
public static class SecretFileConvention
{
    public const string Suffix = "_FILE";

    /// <summary>Maps every <c>{Key}_FILE</c> variable to <c>{Key}</c> (with <c>__</c> as the section separator).</summary>
    /// <exception cref="InvalidOperationException">A referenced file is missing or unreadable; the message names the
    /// variable, never the content.</exception>
    public static IReadOnlyDictionary<string, string?> Resolve(IDictionary environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in environment)
        {
            if (entry.Key is not string name
                || name.Length <= Suffix.Length
                || !name.EndsWith(Suffix, StringComparison.Ordinal)
                || !name.Contains("__", StringComparison.Ordinal)
                || entry.Value is not string path
                || string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var key = name[..^Suffix.Length].Replace("__", ":", StringComparison.Ordinal);
            try
            {
                values[key] = TrimLineBreak(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Environment variable {name} names a secret file that cannot be read.", ex);
            }
        }

        return values;
    }

    /// <summary>Removes one trailing <c>\n</c> or <c>\r\n</c> (what <c>echo</c> appends when a secret file is created).</summary>
    public static string TrimLineBreak(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.EndsWith("\r\n", StringComparison.Ordinal) ? value[..^2]
            : value.EndsWith('\n') ? value[..^1]
            : value;
    }
}
