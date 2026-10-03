using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;

namespace Opportunity.Api.Conventions;

/// <summary>
/// Opaque collection cursors (ADR-019 §2.8): the keyset position plus the (user, workspace) the page was served to. A
/// cursor presented by another user or for another workspace is rejected. The position only narrows a query that is
/// authorized on its own, so a hand-made cursor can skip rows but never reveal any.
/// </summary>
public static class PageCursor
{
    private const int MaxLength = 2048;

    public static string Encode(Guid userId, Guid? workspaceId, params string[] position)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new State(userId, workspaceId, position));
        return WebEncoders.Base64UrlEncode(json);
    }

    /// <summary>The position, or null when the cursor is malformed or bound to another user or workspace.</summary>
    public static string[]? Decode(string cursor, Guid userId, Guid? workspaceId, int positionLength)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        if (cursor.Length is 0 or > MaxLength)
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<State>(Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cursor)));
            return state is { P: { } p } && state.U == userId && state.W == workspaceId && p.Length == positionLength && p.All(v => v is not null)
                ? p
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    public static ValidationProblem Invalid() =>
        Problems.Validation(new Dictionary<string, string[]> { ["cursor"] = ["The cursor is not valid for this collection."] });

    private sealed record State(Guid U, Guid? W, string[] P);
}
