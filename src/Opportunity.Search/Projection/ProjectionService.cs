using System.Text;

using Opportunity.Application.Search.Projection;
using Opportunity.Application.Storage;

namespace Opportunity.Search.Projection;

/// <summary>
/// The entry point of index workers (E07-T03, E07-T04): reads a batch of documents from PostgreSQL in one snapshot,
/// loads their capped text and builds their projection writes with the registered <see cref="IProjectionBuilder"/>.
/// Swapping the builder (ADR-004b) changes nothing here or in the workers.
/// </summary>
public interface IProjectionService
{
    int Generation { get; }

    /// <summary>One <see cref="ProjectionDocument"/> per requested id, in request order (duplicates collapsed).</summary>
    Task<IReadOnlyList<ProjectionDocument>> BuildAsync(
        Guid workspaceId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default);
}

/// <summary>Loads a document's extracted text, reading no more than the cap needs (ADR-007 R9).</summary>
public interface IProjectionTextLoader
{
    Task<ProjectionText?> LoadAsync(ProjectionSource source, CancellationToken cancellationToken = default);
}

internal sealed class ProjectionService(IProjectionSourceReader reader, IProjectionTextLoader texts, IProjectionBuilder builder)
    : IProjectionService
{
    public int Generation => builder.Generation;

    public async Task<IReadOnlyList<ProjectionDocument>> BuildAsync(
        Guid workspaceId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default)
    {
        var batch = await reader.ReadAsync(workspaceId, documentIds, cancellationToken).ConfigureAwait(false);
        var documents = new List<ProjectionDocument>(batch.Documents.Count);
        foreach (var source in batch.Documents)
        {
            if (source.WorkspaceId != workspaceId)
            {
                throw new InvalidOperationException("The projection source reader returned a document of another workspace.");
            }

            var text = source.State == ProjectionSourceState.Live
                ? await texts.LoadAsync(source, cancellationToken).ConfigureAwait(false)
                : null;
            documents.Add(builder.Build(source, batch.Catalog, text));
        }

        return documents;
    }
}

/// <summary>
/// Reads extracted text (UTF-8) from object storage with a byte range just large enough for the cap: a UTF-16 code
/// unit takes at most 3 UTF-8 bytes, so <c>3 × (cap + 1)</c> bytes always hold the cap plus the one character that tells
/// whether to cut.
/// </summary>
internal sealed class ObjectStoreProjectionTextLoader(IObjectStore store, ProjectionOptions options) : IProjectionTextLoader
{
    public async Task<ProjectionText?> LoadAsync(ProjectionSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.TextObjectKey is not { } key)
        {
            return null;
        }

        var cap = options.IndexedTextCap;
        Stream stream;
        try
        {
            stream = await store.OpenReadAsync(ObjectKey.Parse(key), new ByteRange(0, 3L * (cap + 1)), cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException)
        {
            // An empty object: the range starts at its end.
            return new ProjectionText(string.Empty, false);
        }

        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return await IndexedText.ReadAsync(reader, cap, cancellationToken).ConfigureAwait(false);
        }
    }
}
