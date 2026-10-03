namespace Opportunity.Contracts.Api;

/// <summary>
/// Body of <c>POST /api/v1/workspaces/{workspaceId}/documents/{documentId}/views</c>: the viewer displayed the
/// document as the active document (<c>Document.Viewed</c>). <see cref="RetrievalId"/> is the
/// <c>X-Opportunity-Retrieval-Id</c> header of the gateway response that delivered what was displayed.
/// </summary>
public sealed record DocumentViewRecord(Guid? RetrievalId);
