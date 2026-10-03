namespace Opportunity.Core.Storage;

/// <summary>Object-storage area (ADR-011 §1.5). Stored as smallint.</summary>
public enum ObjectArea : short
{
    Native = 1,
    Text = 2,
    Image = 3,
    Rendition = 4,
    ImportSource = 5,
    ImportUpload = 6,
    SnapshotManifest = 7,
    Export = 8,
    Production = 9,
    Report = 10,
    Scratch = 11,
}

/// <summary>ADR-011 §2.3. Stored as smallint.</summary>
public enum EncryptionScheme : short
{
    ProviderSse = 1,
    Envelope = 2,
}

/// <summary>ADR-011 §2.3. Stored as smallint.</summary>
public enum StoredObjectState : short
{
    Committed = 1,
    Quarantined = 2,
}

/// <summary>Registry row for one stored object (ADR-011 §2.3). Domain rows reference <see cref="ObjectId"/>, never the key.</summary>
public sealed class StoredObject
{
    public Guid WorkspaceId { get; set; }

    public Guid ObjectId { get; set; }

    /// <summary>Logical key under <c>ws/{workspaceId:N}/</c>; never contains user-supplied strings.</summary>
    public string LogicalKey { get; set; } = string.Empty;

    public ObjectArea Area { get; set; }

    public Guid? DocumentId { get; set; }

    public byte[] Sha256 { get; set; } = [];

    public long SizeBytes { get; set; }

    public string? ContentType { get; set; }

    public string KeyId { get; set; } = string.Empty;

    public EncryptionScheme EncryptionScheme { get; set; }

    public byte[]? WrappedDek { get; set; }

    public StoredObjectState State { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedByJobId { get; set; }
}
