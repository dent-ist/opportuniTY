using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Opportunity.Application.Keys;

namespace Opportunity.Security.Keys;

/// <summary>Settings of the key store (configuration section <c>KeyManagement</c>).</summary>
public sealed class KeyManagementOptions
{
    public const string SectionName = "KeyManagement";

    public const string LocalProvider = "Local";

    /// <summary>
    /// <c>Local</c> (built in: a key directory, for Lite/Compose and single-host installations). Vault Transit, AWS KMS
    /// and Azure Key Vault adapters implement <see cref="IKeyEncryptionKeyProvider"/> and <see cref="ISigningKeyProvider"/>
    /// and register under their own name; none ships in this build.
    /// </summary>
    public string Provider { get; set; } = LocalProvider;

    /// <summary>How long a process reuses an unwrapped workspace data key.</summary>
    public TimeSpan DataKeyCacheTtl { get; set; } = TimeSpan.FromMinutes(1);

    public LocalKeyStoreOptions Local { get; set; } = new();
}

public sealed class LocalKeyStoreOptions
{
    /// <summary>Directory holding the keys (mode 0700, files 0600). Back it up separately from the data (docs/operations).</summary>
    public string? KeyDirectory { get; set; }

    /// <summary>
    /// Optional name of a secret (<see cref="ISecretProvider"/>, e.g. a Docker secret) holding 32 random bytes in base64.
    /// When set, every key file is sealed with it, so a copy of the key directory alone is useless.
    /// </summary>
    public string? MasterKeySecret { get; set; }

    /// <summary>Create the installation KEK and signing keys on first use (Lite generates its keys on first run, D10.1).</summary>
    public bool CreateMissingKeys { get; set; } = true;
}

/// <summary>
/// Versioned key files under one directory: <c>{root}/{kind}/{id}/v000001.key</c>. Files are created exclusively
/// (concurrent processes never overwrite each other's keys) with mode 0600, optionally sealed with a master key from the
/// secret provider, and overwritten with zeros before deletion when destroyed. Local disks and backups can keep
/// remnants; a key store with real destruction guarantees (HSM, KMS) is the Full-profile choice.
/// </summary>
internal sealed partial class LocalKeyStore(LocalKeyStoreOptions options, ISecretProvider secrets)
{
    private const int MasterKeySize = 32;
    private static readonly byte[] PlainMagic = "OPPK0"u8.ToArray();
    private static readonly byte[] SealedMagic = "OPPK1"u8.ToArray();

    public bool CreateMissingKeys => options.CreateMissingKeys;

    public IReadOnlyList<int> Versions(string kind, string id)
    {
        var dir = Directory(kind, id);
        if (!System.IO.Directory.Exists(dir))
        {
            return [];
        }

        var versions = new List<int>();
        foreach (var file in System.IO.Directory.EnumerateFiles(dir, "v*.key"))
        {
            var match = VersionFile().Match(Path.GetFileName(file));
            if (match.Success)
            {
                versions.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
            }
        }

        versions.Sort();
        return versions;
    }

    /// <summary>Writes <paramref name="material"/> as a new version; false when that version already exists.</summary>
    public async Task<bool> TryCreateAsync(string kind, string id, int version, byte[] material, CancellationToken cancellationToken)
    {
        var dir = Directory(kind, id);
        CreatePrivateDirectory(dir);
        var sealedBytes = await SealAsync(material, cancellationToken).ConfigureAwait(false);
        var temp = Path.Combine(dir, $".v{version:D6}.{Guid.NewGuid():N}.tmp");
        try
        {
            await WritePrivateFileAsync(temp, sealedBytes, cancellationToken).ConfigureAwait(false);
            try
            {
                // Atomic and exclusive: a racing creator of the same version loses here, never half-writes.
                File.Move(temp, FilePath(kind, id, version), overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(FilePath(kind, id, version)))
            {
                return false;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sealedBytes);
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>The key material, or null when the version does not exist.</summary>
    public async Task<byte[]?> ReadAsync(string kind, string id, int version, CancellationToken cancellationToken)
    {
        var path = FilePath(kind, id, version);
        byte[] stored;
        try
        {
            stored = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        try
        {
            return await UnsealAsync(stored, id, version, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(stored);
        }
    }

    public void Destroy(string kind, string id, int version)
    {
        var path = FilePath(kind, id, version);
        if (!File.Exists(path))
        {
            return;
        }

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Write(new byte[stream.Length]);
            stream.Flush(flushToDisk: true);
        }

        File.Delete(path);
    }

    public void DestroyAll(string kind, string id)
    {
        foreach (var version in Versions(kind, id))
        {
            Destroy(kind, id, version);
        }

        var dir = Directory(kind, id);
        if (System.IO.Directory.Exists(dir))
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    private string Root =>
        string.IsNullOrWhiteSpace(options.KeyDirectory)
            ? throw new InvalidOperationException(
                $"{KeyManagementOptions.SectionName}:Local:KeyDirectory is required to use the local key store (see docs/operations/keys-and-secrets.md).")
            : options.KeyDirectory;

    private string Directory(string kind, string id) => Path.Combine(Root, kind, KeyEncryptionKeyIds.Validate(id));

    private string FilePath(string kind, string id, int version)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        return Path.Combine(Directory(kind, id), $"v{version:D6}.key");
    }

    private async Task<byte[]> SealAsync(byte[] material, CancellationToken cancellationToken)
    {
        using var master = await MasterKeyAsync(cancellationToken).ConfigureAwait(false);
        if (master is null)
        {
            return [.. PlainMagic, .. material];
        }

        var output = new byte[SealedMagic.Length + 12 + material.Length + 16];
        SealedMagic.CopyTo(output, 0);
        var nonce = output.AsSpan(SealedMagic.Length, 12);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(master.Bytes, 16);
        aes.Encrypt(nonce, material, output.AsSpan(SealedMagic.Length + 12, material.Length), output.AsSpan(output.Length - 16), "opportunity/key-file/v1"u8);
        return output;
    }

    private async Task<byte[]> UnsealAsync(byte[] stored, string id, int version, CancellationToken cancellationToken)
    {
        if (stored.AsSpan().StartsWith(PlainMagic))
        {
            return stored[PlainMagic.Length..];
        }

        if (!stored.AsSpan().StartsWith(SealedMagic) || stored.Length < SealedMagic.Length + 12 + 16)
        {
            throw new KeyUnavailableException($"Key file of {id} v{version} is not a key file.");
        }

        using var master = await MasterKeyAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new KeyUnavailableException($"Key {id} v{version} is sealed with a master key, but no master key secret is configured.");
        var body = stored.AsSpan(SealedMagic.Length + 12, stored.Length - SealedMagic.Length - 12 - 16);
        var material = new byte[body.Length];
        try
        {
            using var aes = new AesGcm(master.Bytes, 16);
            aes.Decrypt(stored.AsSpan(SealedMagic.Length, 12), body, stored.AsSpan(stored.Length - 16), material, "opportunity/key-file/v1"u8);
            return material;
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new KeyUnavailableException($"Key {id} v{version} does not open with the configured master key.", ex);
        }
    }

    private async Task<SecretValue?> MasterKeyAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.MasterKeySecret))
        {
            return null;
        }

        using var secret = await secrets.GetAsync(options.MasterKeySecret, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyUnavailableException($"Master key secret '{options.MasterKeySecret}' is not defined.");
        var decoded = new byte[MasterKeySize];
        if (!Convert.TryFromBase64String(Encoding.ASCII.GetString(secret.Bytes).Trim(), decoded, out var written) || written != MasterKeySize)
        {
            throw new KeyUnavailableException($"Master key secret '{options.MasterKeySecret}' must be 32 random bytes in base64.");
        }

        try
        {
            return new SecretValue(decoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            System.IO.Directory.CreateDirectory(path);
        }
        else
        {
            System.IO.Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static async Task WritePrivateFileAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var stream = new FileStream(path, fileOptions);
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
    }

    [GeneratedRegex("^v([0-9]{6})\\.key$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionFile();
}
