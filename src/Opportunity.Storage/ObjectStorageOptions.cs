using Opportunity.Application.Storage;

namespace Opportunity.Storage;

public enum ObjectStorageProvider
{
    /// <summary>Lite only (Q-01, Q-38): local directory, always streamed through the API.</summary>
    FileSystem,

    /// <summary>Any S3-compatible store; the Full default is a permissively licensed one (Q-38).</summary>
    S3,

    AzureBlob,
}

/// <summary>Object storage settings (configuration section <c>ObjectStorage</c>). Switching provider is configuration only.</summary>
public sealed class ObjectStorageOptions
{
    public const string SectionName = "ObjectStorage";

    /// <summary>MVP key identifier recorded with every object (ADR-011 §6.1).</summary>
    public const string InstallationDefaultKeyId = "installation-default";

    public ObjectStorageProvider Provider { get; set; } = ObjectStorageProvider.FileSystem;

    public string KeyId { get; set; } = InstallationDefaultKeyId;

    /// <summary>Default presigned GET lifetime; ADR-015 D12.3: 60 s, configurable 30–300 s.</summary>
    public TimeSpan PresignGetDefaultTtl { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan PresignGetMaxTtl { get; set; } = PresignPolicy.HardMaxGetTtl;

    public TimeSpan PresignPutDefaultTtl { get; set; } = TimeSpan.FromSeconds(300);

    public FileSystemObjectStoreOptions FileSystem { get; set; } = new();

    public S3ObjectStoreOptions S3 { get; set; } = new();

    public AzureBlobObjectStoreOptions AzureBlob { get; set; } = new();

    /// <summary>Envelope encryption of workspace objects (E05-T09); off by default (provider SSE only).</summary>
    public ObjectEncryptionOptions Encryption { get; set; } = new();

    public PresignPolicy CreatePresignPolicy() => new(PresignGetDefaultTtl, PresignGetMaxTtl, PresignPutDefaultTtl);

    /// <summary>Start-up check of the settings the selected provider needs; throws <see cref="InvalidOperationException"/>.</summary>
    public void Validate()
    {
        var missing = Provider switch
        {
            ObjectStorageProvider.FileSystem => string.IsNullOrWhiteSpace(FileSystem.RootPath) ? "FileSystem:RootPath" : null,
            ObjectStorageProvider.S3 => S3.ServiceUrl is null ? "S3:ServiceUrl" : string.IsNullOrWhiteSpace(S3.Bucket) ? "S3:Bucket" : null,
            ObjectStorageProvider.AzureBlob => string.IsNullOrWhiteSpace(AzureBlob.ConnectionString) ? "AzureBlob:ConnectionString"
                : string.IsNullOrWhiteSpace(AzureBlob.Container) ? "AzureBlob:Container" : null,
            _ => "Provider",
        };
        if (missing is not null)
        {
            throw new InvalidOperationException($"{SectionName}:{missing} is required for provider {Provider}.");
        }

        if (Encryption.ChunkSizeLog2 is < Opportunity.Storage.Encryption.EnvelopeFormat.MinChunkSizeLog2 or > Opportunity.Storage.Encryption.EnvelopeFormat.MaxChunkSizeLog2)
        {
            throw new InvalidOperationException($"{SectionName}:Encryption:ChunkSizeLog2 must be 12 (4 KiB) to 24 (16 MiB).");
        }
    }
}

public enum ObjectEncryptionMode
{
    /// <summary>The provider's server-side encryption (S3 SSE, Azure SSE) or none (Lite filesystem); KeyId installation-default.</summary>
    ProviderSse,

    /// <summary>Client-side envelope encryption with per-workspace data keys (ADR-011 §6, E05-T09); objects are streamed, never presigned.</summary>
    Envelope,
}

public sealed class ObjectEncryptionOptions
{
    public ObjectEncryptionMode Mode { get; set; } = ObjectEncryptionMode.ProviderSse;

    /// <summary>log2 of the plaintext chunk size for new objects (default 16 = 64 KiB); recorded per object.</summary>
    public int ChunkSizeLog2 { get; set; } = 16;
}

public sealed class FileSystemObjectStoreOptions
{
    /// <summary>Store root; must be outside any web root (ADR-011 §3.2, ADR-015 D12.2).</summary>
    public string RootPath { get; set; } = string.Empty;
}

public sealed class S3ObjectStoreOptions
{
    public const long MinPartSizeBytes = 5L * 1024 * 1024;

    /// <summary>Endpoint the services use, e.g. <c>http://objectstore:8333</c>.</summary>
    public Uri? ServiceUrl { get; set; }

    /// <summary>Browser-reachable endpoint used only for signing URLs; defaults to <see cref="ServiceUrl"/>.</summary>
    public Uri? PublicServiceUrl { get; set; }

    public string Region { get; set; } = "us-east-1";

    /// <summary>One bucket per installation (ADR-011 §3.2).</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>Physical name prefix, <c>{InstallationPrefix}/{logicalKey}</c>; empty for none.</summary>
    public string InstallationPrefix { get; set; } = string.Empty;

    /// <summary>Supplied through the secret provider in Full (ADR-015 D10); never logged.</summary>
    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    public bool ForcePathStyle { get; set; } = true;

    /// <summary>Multipart part size and single-PUT threshold. ADR-011 §3.1: at least 64 MiB in production.</summary>
    public long PartSizeBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Send <c>If-None-Match: *</c> so the store enforces write-once atomically.</summary>
    public bool UseConditionalWrites { get; set; } = true;

    /// <summary>Send <c>x-amz-checksum-sha256</c> on single PUTs so the store verifies the bytes it receives.</summary>
    public bool SendChecksums { get; set; } = true;

    /// <summary>Optional SSE header, e.g. <c>AES256</c>; null relies on the bucket's default encryption.</summary>
    public string? ServerSideEncryption { get; set; }
}

public sealed class AzureBlobObjectStoreOptions
{
    /// <summary>Connection string with an account key (needed for service SAS). Never logged.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>One container per installation (ADR-011 §3.2).</summary>
    public string Container { get; set; } = string.Empty;

    public string InstallationPrefix { get; set; } = string.Empty;

    /// <summary>Block size for staged uploads; also the most bytes held in memory per put.</summary>
    public long BlockSizeBytes { get; set; } = 64L * 1024 * 1024;
}
