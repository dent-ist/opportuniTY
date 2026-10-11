using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

using Opportunity.Application.Import;

namespace Opportunity.Import.Scanning;

/// <summary>The scanner could not be asked (unreachable, refused, broken answer): the chunk is retried, nothing is judged.</summary>
public sealed class MalwareScannerUnavailableException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>
/// The default <see cref="IMalwareScanner"/> (ADR-015 D16.1): ClamAV <c>clamd</c> in its own container, spoken to over
/// TCP with the null-terminated <c>zINSTREAM</c> command (the bytes go in length-prefixed chunks; clamd never sees a
/// path). Nothing of ClamAV is linked into the product. <c>FOUND</c> is Infected, except the encrypted-content and
/// limit heuristics, which are Unscannable; a stream over <see cref="MalwareScanningOptions.MaxScanBytes"/> or clamd's
/// <c>StreamMaxLength</c> is Unscannable (<c>oversize</c>), and so is a scan that outlasts
/// <see cref="MalwareScanningOptions.ScanTimeout"/>. Any other failure throws
/// <see cref="MalwareScannerUnavailableException"/>.
/// </summary>
public sealed class ClamAvScanner(MalwareScanningOptions options, TimeProvider? time = null) : IMalwareScanner
{
    private const int ChunkBytes = 64 * 1024;
    private static readonly TimeSpan VersionTtl = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private volatile EngineVersion? _engine;

    public async Task<MalwareScanResult> ScanAsync(Stream content, long length, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var engine = await EngineAsync(cancellationToken).ConfigureAwait(false);
        if (length > options.MaxScanBytes)
        {
            return MalwareScanResult.Unscannable(
                string.Create(CultureInfo.InvariantCulture, $"{MalwareScanReasons.Oversize}: {length} bytes, the scanner takes at most {options.MaxScanBytes}"),
                engine);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ScanTimeout);
        string answer;
        try
        {
            answer = await InstreamAsync(content, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return MalwareScanResult.Unscannable($"{MalwareScanReasons.Timeout}: no answer within {options.ScanTimeout}", engine);
        }

        return Interpret(answer, engine);
    }

    public async Task<MalwareScannerStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var answer = await CommandAsync("zVERSION\0", cancellationToken).ConfigureAwait(false);
            var (version, published) = ParseVersion(answer);
            return new MalwareScannerStatus(true, version, published);
        }
        catch (MalwareScannerUnavailableException ex)
        {
            return new MalwareScannerStatus(false, null, null, ex.Message);
        }
    }

    /// <summary>What a clamd answer means (<c>stream: OK</c>, <c>stream: NAME FOUND</c>, <c>… ERROR</c>).</summary>
    public static MalwareScanResult Interpret(string answer, string engine)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var text = answer.TrimEnd('\0', '\n', ' ');
        if (text.StartsWith("stream: ", StringComparison.Ordinal))
        {
            text = text["stream: ".Length..];
        }

        if (text == "OK")
        {
            return MalwareScanResult.Clean(engine);
        }

        if (text.EndsWith(" FOUND", StringComparison.Ordinal))
        {
            var signature = text[..^" FOUND".Length].Trim();
            return signature.StartsWith("Heuristics.Encrypted", StringComparison.Ordinal)
                ? MalwareScanResult.Unscannable($"{MalwareScanReasons.Encrypted}: {signature}", engine)
                : signature.StartsWith("Heuristics.Limits.Exceeded", StringComparison.Ordinal)
                    ? MalwareScanResult.Unscannable($"{MalwareScanReasons.ScannerLimit}: {signature}", engine)
                    : MalwareScanResult.Infected(signature, engine);
        }

        if (text.Contains("size limit exceeded", StringComparison.OrdinalIgnoreCase))
        {
            return MalwareScanResult.Unscannable($"{MalwareScanReasons.Oversize}: the scanner's stream limit was exceeded", engine);
        }

        throw new MalwareScannerUnavailableException($"clamd answered '{Truncate(text)}'.");
    }

    /// <summary>
    /// Parses a <c>VERSION</c> answer, <c>ClamAV 1.4.6/28136/Sun Sep 27 06:26:12 2026</c>: the engine and database version,
    /// and when the database was published (taken as UTC).
    /// </summary>
    public static (string Version, DateTimeOffset? Published) ParseVersion(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var text = answer.TrimEnd('\0', '\n', ' ');
        var parts = text.Split('/', 3);
        var version = parts.Length >= 2 ? parts[0] + "/" + parts[1] : parts[0];
        DateTimeOffset? published = null;
        if (parts.Length == 3)
        {
            var date = string.Join(' ', parts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (DateTime.TryParseExact(date, "ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                published = new DateTimeOffset(parsed, TimeSpan.Zero);
            }
        }

        return (version, published);
    }

    private async Task<string> EngineAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (_engine is { } cached && now - cached.At < VersionTtl)
        {
            return cached.Engine;
        }

        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.Reachable)
        {
            throw new MalwareScannerUnavailableException(status.Problem ?? "clamd is not reachable.");
        }

        var engine = status.Version ?? "ClamAV";
        _engine = new EngineVersion(engine, now);
        return engine;
    }

    private async Task<string> InstreamAsync(Stream content, CancellationToken cancellationToken)
    {
        using var client = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        var stream = client.GetStream();
        await using (stream.ConfigureAwait(false))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes + 4);
            try
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes("zINSTREAM\0"), cancellationToken).ConfigureAwait(false);
                try
                {
                    int n;
                    while ((n = await content.ReadAsync(buffer.AsMemory(4, ChunkBytes), cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)n);
                        await stream.WriteAsync(buffer.AsMemory(0, n + 4), cancellationToken).ConfigureAwait(false);
                    }

                    BinaryPrimitives.WriteUInt32BigEndian(buffer, 0);
                    await stream.WriteAsync(buffer.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // clamd closes the connection once the stream exceeds its StreamMaxLength; its answer says so.
                }

                return await ReadAnswerAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new MalwareScannerUnavailableException("The connection to clamd failed during the scan.", ex);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private async Task<string> CommandAsync(string command, CancellationToken cancellationToken)
    {
        using var client = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        var stream = client.GetStream();
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(command), cancellationToken).ConfigureAwait(false);
                return await ReadAnswerAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new MalwareScannerUnavailableException("The connection to clamd failed.", ex);
            }
        }
    }

    private async Task<TcpClient> ConnectAsync(CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connect.CancelAfter(options.ConnectTimeout);
        try
        {
            await client.ConnectAsync(options.Host, options.Port, connect.Token).ConfigureAwait(false);
            return client;
        }
        catch (Exception ex) when (ex is SocketException or IOException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            client.Dispose();
            throw new MalwareScannerUnavailableException($"clamd at {options.Host}:{options.Port} is not reachable.", ex);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>The answer up to its terminating NUL (or the end of the connection); at most 4 KiB.</summary>
    private static async Task<string> ReadAnswerAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var answer = new MemoryStream();
        var buffer = new byte[512];
        while (answer.Length < 4096)
        {
            var n = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            var end = Array.IndexOf(buffer, (byte)0, 0, n);
            answer.Write(buffer, 0, end < 0 ? n : end);
            if (end >= 0)
            {
                break;
            }
        }

        if (answer.Length == 0)
        {
            throw new MalwareScannerUnavailableException("clamd closed the connection without an answer.");
        }

        return Encoding.UTF8.GetString(answer.GetBuffer(), 0, (int)answer.Length);
    }

    private static string Truncate(string value) => value.Length <= 200 ? value : value[..200];

    /// <summary>The engine and database version, re-read every few minutes (one reference: safe across parallel scans).</summary>
    private sealed record EngineVersion(string Engine, DateTimeOffset At);
}
