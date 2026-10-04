using Opportunity.Core.Storage;

namespace Opportunity.Application.Import;

/// <summary>
/// A volume file an import stored in object storage for one document (a native, its extracted text or a page image),
/// waiting to be registered in the <c>StoredObject</c> registry by the chunk transaction that writes the document
/// (ADR-011 §2.4: upload first, register together with the referencing row). The key is content-addressed under the
/// document, so a retried chunk addresses the same object.
/// </summary>
/// <param name="DocumentId">The document the key lives under; the registry row belongs to it.</param>
/// <param name="LogicalKey">The content-addressed key, e.g. <c>ws/{ws}/docs/{documentId}/native/{sha256}</c>.</param>
/// <param name="ContentType">Sniffed from the bytes (natives, images) or fixed (UTF-8 text); never from the load file.</param>
public sealed record ImportObject(
    Guid DocumentId,
    ObjectArea Area,
    string LogicalKey,
    byte[] Sha256,
    long SizeBytes,
    string ContentType,
    string KeyId,
    EncryptionScheme EncryptionScheme);
