using System.Text;

using Opportunity.Application.Keys;

namespace Opportunity.Security.Keys;

/// <summary>Settings of the built-in secret sources (configuration section <c>Secrets</c>).</summary>
public sealed class SecretOptions
{
    public const string SectionName = "Secrets";

    /// <summary>Docker/Compose secrets directory; one file per secret, named like the secret.</summary>
    public string Directory { get; set; } = "/run/secrets";

    /// <summary>
    /// Also read <c>OPPORTUNITY_SECRET_{NAME}</c> and <c>OPPORTUNITY_SECRET_{NAME}_FILE</c>. Plain variables are for
    /// development; ADR-015 D10.1 forbids them in Full, where operators set this to false.
    /// </summary>
    public bool AllowEnvironment { get; set; } = true;
}

/// <summary>Docker/Compose secrets: <c>{directory}/{name}</c>, one trailing line break removed.</summary>
public sealed class FileSecretProvider(string directory) : ISecretProvider
{
    public async ValueTask<SecretValue?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(directory, SecretNames.Validate(name));
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            return new SecretValue(TrimLineBreak(bytes));
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    internal static ReadOnlySpan<byte> TrimLineBreak(ReadOnlySpan<byte> bytes) =>
        bytes.EndsWith("\r\n"u8) ? bytes[..^2] : bytes.EndsWith("\n"u8) ? bytes[..^1] : bytes;
}

/// <summary>
/// <c>OPPORTUNITY_SECRET_{NAME}_FILE</c> (a path, the <c>*_FILE</c> convention) or <c>OPPORTUNITY_SECRET_{NAME}</c>
/// (the value), where <c>{NAME}</c> is the secret name upper-cased with every other character than A–Z and 0–9 as
/// <c>_</c>.
/// </summary>
public sealed class EnvironmentSecretProvider(Func<string, string?>? read = null) : ISecretProvider
{
    public const string Prefix = "OPPORTUNITY_SECRET_";

    private readonly Func<string, string?> _read = read ?? Environment.GetEnvironmentVariable;

    public static string VariableName(string name)
    {
        var builder = new StringBuilder(Prefix);
        foreach (var c in SecretNames.Validate(name))
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        }

        return builder.ToString();
    }

    public async ValueTask<SecretValue?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        var variable = VariableName(name);
        if (_read(variable + SecretFileConvention.Suffix) is { Length: > 0 } path)
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            try
            {
                return new SecretValue(FileSecretProvider.TrimLineBreak(bytes));
            }
            finally
            {
                Array.Clear(bytes);
            }
        }

        return _read(variable) is { Length: > 0 } value ? new SecretValue(Encoding.UTF8.GetBytes(value)) : null;
    }
}

/// <summary>The first source that defines the secret wins.</summary>
public sealed class CompositeSecretProvider(IReadOnlyList<ISecretProvider> sources) : ISecretProvider
{
    public async ValueTask<SecretValue?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        SecretNames.Validate(name);
        foreach (var source in sources)
        {
            if (await source.GetAsync(name, cancellationToken).ConfigureAwait(false) is { } value)
            {
                return value;
            }
        }

        return null;
    }
}
