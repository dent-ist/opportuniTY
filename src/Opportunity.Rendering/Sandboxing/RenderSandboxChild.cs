using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

using Opportunity.Rendering.Renderers;

using SkiaSharp;

namespace Opportunity.Rendering.Sandboxing;

/// <summary>
/// The render sandbox process (E11-T03; entry point <c>Opportunity.Rendering.Sandbox</c>), one per document. It confines
/// itself before it reads anything from the document:
/// <list type="number">
/// <item><b>Stage 1</b> (the main thread, before anything else): leave root for the configured user (glibc applies it
/// to every thread), set the resource limits and no_new_privs, and restrict the thread with Landlock. Landlock applies to
/// the calling thread only, and the runtime already runs other threads, so the process then re-executes itself: after
/// <c>execve</c> every thread of the new image is born inside the Landlock domain.</item>
/// <item><b>Stage 2</b> (<c>--confined</c>): verify the confinement, load PDFium and Skia, install the seccomp filter
/// on every thread (no sockets, no exec, no new processes, …) and only then answer <c>ready</c> and render requests
/// (<see cref="SandboxProtocol"/>).</item>
/// </list>
/// Everything runs synchronously on the main thread, so no untrusted input is handled by thread-pool threads.
/// </summary>
public static class RenderSandboxChild
{
    public const string StartArgument = "--start";

    private const string ConfinedArgument = "--confined";

    /// <summary>Exit codes besides 0: invalid arguments or protocol (2), the sandbox could not be applied (3), out of memory (4).</summary>
    public static int Run(string[] args, Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        using var writer = new StreamWriter(output, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        var start = ParseStart(args);
        if (start is null)
        {
            Send(writer, new SandboxMessage(SandboxMessageTypes.Fatal, Code: "render-sandbox-protocol", Message: "Invalid start argument."));
            return 2;
        }

        var confined = Argument(args, ConfinedArgument);
        SandboxControls controls;
        try
        {
            if (confined is null)
            {
                var landlock = ConfineStage1(start);
                if (landlock > 0)
                {
                    // Does not return: the confined image continues at stage 2.
                    LinuxSandbox.ExecSelf([.. args, ConfinedArgument, landlock.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                }

                confined = "0";
            }

            controls = ConfineStage2(int.Parse(confined, System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is SandboxSetupException or DllNotFoundException or EntryPointNotFoundException or FormatException)
        {
            Send(writer, new SandboxMessage(SandboxMessageTypes.Fatal, Code: "render-sandbox-setup", Message: ex.Message));
            return 3;
        }

        Send(writer, new SandboxMessage(SandboxMessageTypes.Ready, Controls: controls));
        if (start.ProbeEndpoints is { } endpoints)
        {
            Send(writer, new SandboxMessage(SandboxMessageTypes.Probe, Probe: Probe(start, controls, endpoints)));
            return 0;
        }

        using var reader = new StreamReader(input, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var renderer = new RasterRenderer(start.Settings);
        try
        {
            while (reader.ReadLine() is { } line)
            {
                if (SandboxProtocol.Deserialize<SandboxRenderRequest>(line) is not { } request)
                {
                    return 2;
                }

                if (request.Endorse is { } endorse)
                {
                    Endorse(request, endorse, start.Settings, writer);
                    continue;
                }

                if (request.InputPath is not { } source)
                {
                    return 2;
                }

                if (!Render(renderer, request, source, reader, writer))
                {
                    return 0;
                }
            }
        }
        catch (OutOfMemoryException)
        {
            Send(writer, new SandboxMessage(SandboxMessageTypes.Fatal, Code: RenderErrorCodes.MemoryLimit, Message: "Out of memory."));
            return 4;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        {
            // Unexpected: the process ends and the worker treats the document like a crash (no content in the message).
            Send(writer, new SandboxMessage(SandboxMessageTypes.Fatal, Code: RenderErrorCodes.SandboxCrashed, Message: ex.GetType().Name));
            return 5;
        }

        return 0;
    }

    /// <summary>Renders one request; false when the parent went away (stdin closed) in the middle.</summary>
    private static bool Render(RasterRenderer renderer, SandboxRenderRequest request, string inputPath, StreamReader reader, StreamWriter writer)
    {
        try
        {
            foreach (var page in renderer.Render(new RenderRequest(inputPath, request.OutputDirectory, request.Pages, request.Review), CancellationToken.None))
            {
                Send(writer, new SandboxMessage(SandboxMessageTypes.Page, Page: new SandboxPage(
                    page.Index, page.WidthPt, page.HeightPt, page.ColorMode, ToRaster(page.Review), ToRaster(page.Thumbnail), page.Error)));

                // Back-pressure: the parent consumes (uploads and deletes) this page's files before the next is rendered.
                if (reader.ReadLine() != SandboxProtocol.Next)
                {
                    return false;
                }
            }
        }
        catch (RenderException ex)
        {
            Send(writer, new SandboxMessage(SandboxMessageTypes.Failed, Code: ex.Code, Message: ex.Message));
            return true;
        }

        Send(writer, new SandboxMessage(SandboxMessageTypes.End));
        return true;
    }

    /// <summary>Endorses one page (E12-T04) and answers <c>endorsed</c>, or <c>failed</c> with the page's error code.</summary>
    private static void Endorse(SandboxRenderRequest request, SandboxEndorseRequest endorse, RenderSettings settings, StreamWriter writer)
    {
        try
        {
            var file = Endorsing.PageEndorser.EndorseToFile(new Endorsing.EndorseRequest(
                request.InputPath, endorse.Frame, request.OutputDirectory, endorse.Format, endorse.Layout, endorse.Dpi, endorse.BlankWidthPx,
                endorse.BlankHeightPx, endorse.Redactions), settings);
            Send(writer, new SandboxMessage(SandboxMessageTypes.Endorsed, Endorsed: new SandboxEndorsed(
                Path.GetFileName(file.Path), file.WidthPx, file.HeightPx, file.Dpi, file.Format, file.ColorMode, file.PageTopPx, file.PageHeightPx)));
        }
        catch (RenderException ex)
        {
            Send(writer, new SandboxMessage(SandboxMessageTypes.Failed, Code: ex.Code, Message: ex.Message));
        }
    }

    private static SandboxRaster? ToRaster(RasterFile? file) =>
        file is null ? null : new SandboxRaster(Path.GetFileName(file.Path), file.WidthPx, file.HeightPx, file.Dpi, file.Format);

    private static void Send(StreamWriter writer, SandboxMessage message) => writer.WriteLine(SandboxProtocol.Serialize(message));

    private static string? Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static SandboxStart? ParseStart(string[] args)
    {
        try
        {
            var encoded = Argument(args, StartArgument);
            var start = encoded is null ? null : SandboxProtocol.Deserialize<SandboxStart>(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            return start is not null && Path.IsPathFullyQualified(start.WorkDirectory) ? start : null;
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>User, limits, no_new_privs and Landlock on this (the main) thread; returns the Landlock ABI applied (0: none).</summary>
    private static int ConfineStage1(SandboxStart start)
    {
        if (!LinuxSandbox.IsSupported)
        {
            return 0;
        }

        if (LinuxSandbox.EffectiveUserId == 0)
        {
            if (start.RunAsUser is not { } uid || uid == 0)
            {
                throw new SandboxSetupException("The renderer does not run as root: configure Render:Sandbox:RunAsUser.");
            }

            LinuxSandbox.SwitchUser(uid, start.RunAsGroup ?? uid);
        }

        var limits = start.Limits;
        LinuxSandbox.SetLimit(SandboxLimit.CoreBytes, 0);
        LinuxSandbox.SetLimit(SandboxLimit.CpuSeconds, (ulong)limits.CpuSeconds);
        LinuxSandbox.SetLimit(SandboxLimit.DataBytes, (ulong)limits.DataBytes);
        LinuxSandbox.SetLimit(SandboxLimit.FileBytes, (ulong)limits.FileBytes);
        LinuxSandbox.SetLimit(SandboxLimit.OpenFiles, (ulong)limits.OpenFiles);
        LinuxSandbox.SetNoNewPrivileges();

        // The dotnet host, the shared runtime and the C libraries are needed to start the confined image (read and
        // execute); the application directory and a few kernel files to run it (read); the work directory to render.
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar));
        var dotnetRoot = runtime.Parent?.Parent?.Parent?.FullName ?? runtime.FullName;
        string[] executable =
        [
            dotnetRoot,
            Path.GetDirectoryName(Environment.ProcessPath) ?? dotnetRoot,
            "/lib",
            "/lib64",
            "/usr/lib",
            "/usr/lib64",
        ];
        string[] readable =
        [
            AppContext.BaseDirectory,
            "/etc/ld.so.cache",
            "/etc/dotnet",
            "/proc/self",
            "/proc/meminfo",
            "/proc/stat",
            "/proc/cpuinfo",
            "/sys/fs/cgroup",
            "/sys/devices/system/cpu",
            "/dev/urandom",
            "/dev/null",
        ];
        return LinuxSandbox.RestrictFileSystem(executable, readable, [start.WorkDirectory]);
    }

    /// <summary>Verifies Landlock (when applied), loads the native libraries and installs the seccomp filter.</summary>
    private static SandboxControls ConfineStage2(int landlockAbi)
    {
        if (!LinuxSandbox.IsSupported)
        {
            LoadNativeLibraries();
            return new SandboxControls(uint.MaxValue, false, false, 0, false);
        }

        if (landlockAbi > 0 && Succeeds(() => _ = Directory.EnumerateFileSystemEntries("/").FirstOrDefault()))
        {
            throw new SandboxSetupException("Landlock was reported as applied but the root directory is readable.");
        }

        LoadNativeLibraries();
        var seccomp = LinuxSandbox.InstallSeccompFilter();
        return new SandboxControls(LinuxSandbox.EffectiveUserId, true, true, landlockAbi, seccomp);
    }

    private static void LoadNativeLibraries()
    {
        PdfiumNative.EnsureInitialized();
        Endorsing.PageEndorser.EnsureInitialized();
        using var bitmap = new SKBitmap(1, 1);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
    }

    /// <summary>Tries what a compromised renderer would try, after the sandbox is applied (tests and operators' checks).</summary>
    private static SandboxProbeReport Probe(SandboxStart start, SandboxControls controls, IReadOnlyList<string> endpoints)
    {
        var connected = new List<string>();
        var errors = new List<string>();
        foreach (var endpoint in endpoints)
        {
            try
            {
                var separator = endpoint.LastIndexOf(':');
                using var client = new TcpClient();
                client.Connect(endpoint[..separator], int.Parse(endpoint[(separator + 1)..], System.Globalization.CultureInfo.InvariantCulture));
                connected.Add(endpoint);
            }
            catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException or FormatException)
            {
                errors.Add($"{endpoint}: {ex.GetType().Name}");
            }
        }

        var parent = LinuxSandbox.IsSupported ? LinuxSandbox.ParentProcessId : 0;
        var outside = Path.Combine(Path.GetDirectoryName(start.WorkDirectory.TrimEnd('/'))!, "escape-" + Guid.NewGuid().ToString("N"));
        var report = new SandboxProbeReport(
            controls,
            connected,
            errors,
            OpenedUdpSocket: Succeeds(() => new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp).Dispose()),
            OpenedUnixSocket: Succeeds(() => new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified).Dispose()),
            ReadOutsideWorkDirectory: Succeeds(() => File.ReadAllBytes("/etc/hostname")) || Succeeds(() => File.ReadAllBytes("/etc/passwd")),
            WroteOutsideWorkDirectory: Succeeds(() => File.WriteAllText(outside, "x")) || Succeeds(() => File.WriteAllText("/tmp/opp-sandbox-escape-" + Guid.NewGuid().ToString("N"), "x")),
            ReadParentEnvironment: parent > 0 && Succeeds(() => File.ReadAllBytes($"/proc/{parent}/environ")),
            SignalledParent: parent > 0 && LinuxSandbox.CanSignal(parent),
            StartedProcess: Succeeds(() => Process.Start(new ProcessStartInfo("/bin/true") { UseShellExecute = false })?.WaitForExit(5_000)),
            EnvironmentNames: [.. Environment.GetEnvironmentVariables().Keys.Cast<string>().Order(StringComparer.Ordinal)]);
        if (report.WroteOutsideWorkDirectory)
        {
            TryDelete(outside);
        }

        return report;
    }

    private static bool Succeeds(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
