using System.Data.Common;
using System.Text.Json;

using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;

namespace Opportunity.Jobs;

/// <summary>
/// ADR-010 §7 error classes for exceptions escaping a chunk executor. Explicit <see cref="PermanentChunkException"/> /
/// <see cref="TransientChunkException"/> win; database errors follow <see cref="DbException.IsTransient"/>; timeouts and
/// I/O are transient; validation-shaped exceptions are permanent. Anything else is treated as transient, so it is
/// retried with backoff until the chunk's attempts run out instead of failing on a first unexpected hiccup.
/// </summary>
public static class ChunkErrorClassifier
{
    public static ChunkError Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            PermanentChunkException permanent => ChunkError.Permanent(permanent.Code, permanent.Message),
            TransientChunkException transient => ChunkError.Transient(transient.Code, transient.Message),
            PermanentMessageException => Permanent(exception),
            DbException db => db.IsTransient ? Transient(exception) : Permanent(exception),
            TimeoutException or IOException or HttpRequestException or OperationCanceledException => Transient(exception),
            ArgumentException or FormatException or InvalidDataException or JsonException or InvalidCastException
                or NotSupportedException or KeyNotFoundException => Permanent(exception),
            _ => Transient(exception),
        };
    }

    private static ChunkError Permanent(Exception exception) => ChunkError.Permanent(Code(exception), Describe(exception));

    private static ChunkError Transient(Exception exception) => ChunkError.Transient(Code(exception), Describe(exception));

    private static string Code(Exception exception) => exception.GetType().Name;

    private static string Describe(Exception exception) => $"{exception.GetType().Name}: {exception.Message}";
}
