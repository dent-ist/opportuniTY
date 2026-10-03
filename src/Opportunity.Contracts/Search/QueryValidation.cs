using System.Text.Json;

namespace Opportunity.Contracts.Search;

/// <summary>Body of <c>POST /api/v1/workspaces/{workspaceId}/query-validations</c>: query-language text only, never DSL.</summary>
public sealed record QueryValidationRequest(string? Query);

/// <summary>
/// Result of parsing a query (ADR-008 §4, R15) for the query bar. A malformed query is a normal result
/// (<see cref="Valid"/> = <c>false</c> with positioned <see cref="Errors"/>), not an HTTP error.
/// </summary>
public sealed record QueryValidationResult
{
    public required bool Valid { get; init; }

    /// <summary>Version of the <see cref="Ast"/> JSON schema.</summary>
    public required int AstVersion { get; init; }

    /// <summary>Canonical AST JSON (<c>{"astVersion", "root"}</c>, nodes with <c>kind</c> and <c>span</c>); null when invalid.</summary>
    public JsonElement? Ast { get; init; }

    /// <summary>The normalized interpretation, e.g. <c>contract AND termination</c> for <c>contract termination</c>; null when invalid.</summary>
    public string? Normalized { get; init; }

    public required IReadOnlyList<QueryValidationDiagnostic> Errors { get; init; }

    public required IReadOnlyList<QueryValidationDiagnostic> Warnings { get; init; }
}

/// <summary>A positioned error or warning. <see cref="Expected"/> lists the tokens that would be valid at the span.</summary>
public sealed record QueryValidationDiagnostic(string Code, string Message, TextSpan Span, IReadOnlyList<string> Expected);

/// <summary>Half-open <c>[start, end)</c> in UTF-16 code units of the submitted query (JavaScript string indices).</summary>
public sealed record TextSpan(int Start, int End);
