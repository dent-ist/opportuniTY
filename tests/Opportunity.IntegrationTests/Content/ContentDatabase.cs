using System.Text;

using Opportunity.Application.Storage;
using Opportunity.Core.Pages;
using Opportunity.Core.Storage;
using Opportunity.IntegrationTests.Authorization;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Storage;
using Opportunity.Storage.FileSystem;

using ObjectArea = Opportunity.Core.Storage.ObjectArea;

namespace Opportunity.IntegrationTests.Content;

/// <summary>
/// A migrated database (security state as in <see cref="AuthorizationDatabase"/>) plus a filesystem object store in a
/// temporary directory, with helpers that store a document's native, text and page images exactly as import and
/// rendering register them (object written, <c>stored_object</c> row, document / page references).
/// </summary>
internal sealed class ContentDatabase : IAsyncDisposable
{
    private ContentDatabase(AuthorizationDatabase security, string root, IObjectStore? store)
    {
        Security = security;
        StoreRoot = root;
        Store = store ?? new FileSystemObjectStore(new FileSystemObjectStoreOptions { RootPath = root });
    }

    public AuthorizationDatabase Security { get; }

    public string StoreRoot { get; }

    /// <summary>The filesystem store under <see cref="StoreRoot"/>, or the store given to <see cref="CreateAsync"/>.</summary>
    public IObjectStore Store { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<ContentDatabase> CreateAsync(MigrationPostgresFixture postgres, IObjectStore? store = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "opp-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new ContentDatabase(await AuthorizationDatabase.CreateAsync(postgres), root, store);
    }

    /// <summary>A document with a native, extracted text and one imported page set holding page 1 (PNG review image, WebP thumbnail).</summary>
    public async Task<StoredDocument> DocumentAsync(Guid workspaceId, string[]? classes = null, StoredObjectState nativeState = StoredObjectState.Committed)
    {
        var documentId = await Security.DocumentAsync(workspaceId, classes ?? []);
        var native = Encoding.UTF8.GetBytes($"native bytes of {documentId} -- PK\u0003\u0004 not really a zip");
        var text = Encoding.UTF8.GetBytes($"Extracted text of {documentId}: privileged merger memo");
        var png = Png(documentId);
        var thumbnail = Encoding.ASCII.GetBytes("RIFF....WEBPVP8 thumbnail of " + documentId);

        var nativeId = await PutAsync(workspaceId, documentId, ObjectKeys.Native(workspaceId, documentId, Sha(native)), ObjectArea.Native, native, nativeState);
        var textId = await PutAsync(workspaceId, documentId, ObjectKeys.Text(workspaceId, documentId, Sha(text)), ObjectArea.Text, text);
        await Security.Core.ExecuteAsync(
            "UPDATE opportunity.document SET native_object_id = @n, text_object_id = @t, file_extension = 'docx' WHERE workspace_id = @ws AND document_id = @doc",
            ("n", nativeId), ("t", textId), ("ws", workspaceId), ("doc", documentId));

        var pageSet = await Security.Core.InsertPageSetAsync(workspaceId, documentId);
        await Security.Core.ExecuteAsync(
            "INSERT INTO opportunity.page (workspace_id, page_set_id, ordinal, document_id, width_pt, height_pt, color_mode) VALUES (@ws, @ps, 1, @doc, 612, 792, 3)",
            ("ws", workspaceId), ("ps", pageSet), ("doc", documentId));
        var rendition = Guid.NewGuid();
        var pngId = await PutAsync(workspaceId, documentId, ObjectKeys.Rendition(workspaceId, documentId, rendition, "p000001.png"), ObjectArea.Rendition, png);
        var thumbId = await PutAsync(workspaceId, documentId, ObjectKeys.Rendition(workspaceId, documentId, rendition, "p000001.thumb.webp"), ObjectArea.Rendition, thumbnail);
        await PageImageAsync(workspaceId, pageSet, PageImagePurpose.Review, pngId, PageImageFormat.Png);
        await PageImageAsync(workspaceId, pageSet, PageImagePurpose.Thumbnail, thumbId, PageImageFormat.WebP);
        await Security.Core.ExecuteAsync(
            "UPDATE opportunity.document SET active_page_set_id = @ps WHERE workspace_id = @ws AND document_id = @doc",
            ("ps", pageSet), ("ws", workspaceId), ("doc", documentId));

        var controlNumber = await Security.Core.ScalarAsync<string>(
            "SELECT control_number FROM opportunity.document WHERE document_id = @doc", ("doc", documentId));
        return new StoredDocument(documentId, controlNumber, native, text, png, thumbnail, nativeId, textId, pngId);
    }

    /// <summary>A document with only the given artifacts (none when null), stored as import stores them; no page set.</summary>
    public async Task<Guid> ArtifactDocumentAsync(Guid workspaceId, byte[]? text, byte[]? native = null)
    {
        var documentId = await Security.DocumentAsync(workspaceId, []);
        if (native is not null)
        {
            var nativeId = await PutAsync(workspaceId, documentId, ObjectKeys.Native(workspaceId, documentId, Sha(native)), ObjectArea.Native, native);
            await Security.Core.ExecuteAsync(
                "UPDATE opportunity.document SET native_object_id = @n WHERE workspace_id = @ws AND document_id = @doc",
                ("n", nativeId), ("ws", workspaceId), ("doc", documentId));
        }

        if (text is not null)
        {
            var textId = await PutAsync(workspaceId, documentId, ObjectKeys.Text(workspaceId, documentId, Sha(text)), ObjectArea.Text, text);
            await Security.Core.ExecuteAsync(
                "UPDATE opportunity.document SET text_object_id = @t WHERE workspace_id = @ws AND document_id = @doc",
                ("t", textId), ("ws", workspaceId), ("doc", documentId));
        }

        return documentId;
    }

    /// <summary>Adds a page to the document's active page set, with an image registered for <paramref name="purpose"/> when given.</summary>
    public async Task AddPageAsync(
        Guid workspaceId, Guid documentId, int ordinal, PageImagePurpose? purpose = null, PageImageFormat format = PageImageFormat.Png, bool imageMissing = false)
    {
        var pageSet = await Security.Core.ScalarAsync<Guid>(
            "SELECT active_page_set_id FROM opportunity.document WHERE workspace_id = @ws AND document_id = @doc", ("ws", workspaceId), ("doc", documentId));
        await Security.Core.ExecuteAsync(
            """
            INSERT INTO opportunity.page (workspace_id, page_set_id, ordinal, document_id, width_pt, height_pt, rotation, color_mode, image_missing)
            VALUES (@ws, @ps, @ord, @doc, 792, 612, 90, 1, @missing)
            """,
            ("ws", workspaceId), ("ps", pageSet), ("ord", ordinal), ("doc", documentId), ("missing", imageMissing));
        if (purpose is { } p)
        {
            var bytes = Encoding.ASCII.GetBytes($"page {ordinal} of {documentId}");
            var original = p == PageImagePurpose.Original;
            var key = original
                ? ObjectKeys.Image(workspaceId, documentId, Sha(bytes))
                : ObjectKeys.Rendition(workspaceId, documentId, Guid.NewGuid(), $"p{ordinal:D6}.png");
            var objectId = await PutAsync(workspaceId, documentId, key, original ? ObjectArea.Image : ObjectArea.Rendition, bytes);
            await PageImageAsync(workspaceId, pageSet, p, objectId, format, ordinal);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Security.DisposeAsync();
        try
        {
            Directory.Delete(StoreRoot, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: the directory is under the temp path.
        }
    }

    private static Sha256Digest Sha(byte[] bytes) => Sha256Digest.FromBytes(System.Security.Cryptography.SHA256.HashData(bytes));

    private static byte[] Png(Guid documentId) => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. documentId.ToByteArray()];

    private Task PageImageAsync(Guid workspaceId, Guid pageSet, PageImagePurpose purpose, Guid objectId, PageImageFormat format, int ordinal = 1) =>
        Security.Core.ExecuteAsync(
            """
            INSERT INTO opportunity.page_image (workspace_id, page_set_id, ordinal, purpose, object_id, width_px, height_px, dpi_x, dpi_y, format)
            VALUES (@ws, @ps, @ord, @purpose, @obj, 1275, 1650, 150, 150, @format)
            """,
            ("ws", workspaceId), ("ps", pageSet), ("ord", ordinal), ("purpose", (short)purpose), ("obj", objectId), ("format", (short)format));

    private async Task<Guid> PutAsync(
        Guid workspaceId, Guid documentId, ObjectKey key, ObjectArea area, byte[] bytes, StoredObjectState state = StoredObjectState.Committed)
    {
        using (var content = new MemoryStream(bytes))
        {
            await Store.PutAsync(key, content, cancellationToken: Ct);
        }

        var objectId = Guid.CreateVersion7();
        await Security.Core.ExecuteAsync(
            """
            INSERT INTO opportunity.stored_object
                (workspace_id, object_id, logical_key, area, document_id, sha256, size_bytes, key_id, encryption_scheme, state)
            VALUES (@ws, @id, @key, @area, @doc, @sha, @size, @keyId, 1, @state)
            """,
            ("ws", workspaceId), ("id", objectId), ("key", key.Value), ("area", (short)area), ("doc", documentId),
            ("sha", System.Security.Cryptography.SHA256.HashData(bytes)), ("size", (long)bytes.Length),
            ("keyId", ObjectStorageOptions.InstallationDefaultKeyId), ("state", (short)state));
        return objectId;
    }
}

internal sealed record StoredDocument(
    Guid DocumentId,
    string ControlNumber,
    byte[] Native,
    byte[] Text,
    byte[] PageImage,
    byte[] Thumbnail,
    Guid NativeObjectId,
    Guid TextObjectId,
    Guid PageImageObjectId);
