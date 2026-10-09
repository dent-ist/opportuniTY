using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Extensions.Logging;

using Opportunity.Rendering.Renderers;

namespace Opportunity.Rendering.Sandboxing;

/// <summary>
/// The render worker's <see cref="IRenderer"/> (E11-T03): runs <see cref="RasterRenderer"/> in a separate process per
/// document (<see cref="RenderSandboxChild"/>) with no network, no access outside the document's work directory, a
/// clean environment, an unprivileged user and CPU, memory, file-size and wall-clock limits. A document that exceeds a
/// limit or crashes the renderer kills only its own process and surfaces as <see cref="RenderLimitException"/>; the
/// worker keeps going with the next document. Pages stream back one at a time; while the worker consumes a page's
/// files the child is stopped (SIGSTOP), so the files cannot change under it, and every file is validated first
/// (<see cref="SandboxOutputValidator"/>). Renditions are byte-identical to the in-process renderer's, so the
/// renderer identity is the same.
/// </summary>
public sealed partial class SandboxedRenderer : IRenderer
{
    private static int _postureLogged;
    private static int _workerProtected;

    private readonly RenderSettings _settings;
    private readonly RenderSandboxOptions _options;
    private readonly ILogger<SandboxedRenderer> _logger;

    public SandboxedRenderer(RenderSettings? settings, RenderSandboxOptions options, ILogger<SandboxedRenderer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();
        _settings = settings ?? new RenderSettings();
        _options = options;
        _logger = logger;
        Identity = new RasterRenderer(_settings).Identity;
    }

    public RendererIdentity Identity { get; }

    /// <summary>One source in a process of its own (work directory: the output directory's parent).</summary>
    public async IAsyncEnumerable<RenderedPage> RenderAsync(RenderRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var work = Path.GetDirectoryName(Path.GetFullPath(request.OutputDirectory).TrimEnd(Path.DirectorySeparatorChar))
            ?? throw new ArgumentException("The output directory needs a parent work directory.", nameof(request));
        var session = new Session(this, work);
        await using (session.ConfigureAwait(false))
        {
            await foreach (var page in session.RenderAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return page;
            }
        }
    }

    public IRenderSession BeginDocument(string workDirectory) => new Session(this, workDirectory);

    /// <summary>
    /// Starts a sandbox that applies its controls and then tries to connect to <paramref name="endpoints"/> (host:port),
    /// open sockets, read and write outside its work directory, read the worker's environment, signal the worker and
    /// start a program, and reports what succeeded. For tests and operators' checks of a deployment.
    /// </summary>
    public async Task<SandboxProbeReport> ProbeAsync(string workDirectory, IReadOnlyList<string> endpoints, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var session = new Session(this, workDirectory);
        await using (session.ConfigureAwait(false))
        {
            return await session.ProbeAsync(endpoints, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool DropsUser => LinuxSandbox.IsSupported && LinuxSandbox.EffectiveUserId == 0;

    private ProcessStartInfo StartInfo(SandboxStart start)
    {
        var child = _options.ChildAssemblyPath ?? Path.Combine(AppContext.BaseDirectory, RenderSandboxOptions.ChildAssemblyFileName);
        if (!File.Exists(child))
        {
            throw new RenderSandboxUnavailableException($"The render sandbox entry point {RenderSandboxOptions.ChildAssemblyFileName} is missing.");
        }

        var info = new ProcessStartInfo(_options.HostPath ?? DotnetHost())
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            WorkingDirectory = "/",
        };
        info.ArgumentList.Add(child);
        info.ArgumentList.Add(RenderSandboxChild.StartArgument);
        info.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(SandboxProtocol.Serialize(start))));

        // A clean environment: no connection strings, keys or tokens of the worker reach the renderer.
        info.Environment.Clear();
        info.Environment["DOTNET_EnableDiagnostics"] = "0";
        info.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1";
        info.Environment["DOTNET_gcServer"] = "0";
        info.Environment["DOTNET_GCgen0size"] = "0x1000000";
        info.Environment["DOTNET_GCHeapHardLimit"] = "0x" + (_options.MemoryBytes / 2).ToString("X", CultureInfo.InvariantCulture);
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["TZ"] = "UTC";
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root)
        {
            info.Environment["DOTNET_ROOT"] = root;
        }

        return info;
    }

    /// <summary>The dotnet host running this process, else the one next to the shared runtime.</summary>
    private static string DotnetHost()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hostPath && File.Exists(hostPath))
        {
            return hostPath;
        }

        if (Environment.ProcessPath is { } process && Path.GetFileNameWithoutExtension(process) == "dotnet")
        {
            return process;
        }

        // <root>/shared/Microsoft.NETCore.App/<version>/
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar));
        var candidate = Path.Combine(runtime.Parent?.Parent?.Parent?.FullName ?? "/usr/share/dotnet", "dotnet");
        return File.Exists(candidate) ? candidate : "dotnet";
    }

    private SandboxStart StartMessage(string workDirectory, IReadOnlyList<string>? probe) => new(
        workDirectory,
        _settings,
        new SandboxLimits(_options.CpuSeconds, _options.EffectiveDataBytes, _options.MaxFileBytes, _options.MaxOpenFiles),
        DropsUser ? _options.RunAsUser : null,
        DropsUser ? _options.RunAsGroup : null,
        probe);

    private void CheckPosture(SandboxControls controls)
    {
        if (Interlocked.Exchange(ref _postureLogged, 1) == 0)
        {
            LogPosture(_logger, controls.UserId, controls.NoNewPrivileges, controls.ResourceLimits, controls.LandlockAbi, controls.Seccomp);
        }

        var seccompAvailable = LinuxSandbox.IsSupported && SeccompFilter.ForCurrentArchitecture() is not null;
        if (_options.RequireSeccomp && seccompAvailable && !controls.Seccomp)
        {
            throw new RenderSandboxUnavailableException("The render sandbox could not install its seccomp filter.");
        }

        if (_options.RequireLandlock && controls.LandlockAbi == 0)
        {
            throw new RenderSandboxUnavailableException("Landlock is required (Render:Sandbox:RequireLandlock) but unavailable.");
        }

        if (LinuxSandbox.IsSupported && controls.LandlockAbi == 0 && !DropsUser && _options.ProtectWorkerWithoutLandlock
            && Interlocked.Exchange(ref _workerProtected, 1) == 0)
        {
            LinuxSandbox.SetNotDumpable();
            LogWorkerProtected(_logger);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Render sandbox controls: user {UserId}, no_new_privs {NoNewPrivileges}, resource limits {ResourceLimits}, Landlock ABI {LandlockAbi}, seccomp {Seccomp}")]
    private static partial void LogPosture(ILogger logger, uint userId, bool noNewPrivileges, bool resourceLimits, int landlockAbi, bool seccomp);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Landlock is unavailable: the render sandbox cannot confine file access by path. The worker process was made non-dumpable so the renderer cannot read its environment or memory.")]
    private static partial void LogWorkerProtected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Render process {ProcessId} stopped: {Code}. Last output: {Diagnostics}")]
    private static partial void LogStopped(ILogger logger, int processId, string code, string diagnostics);

    /// <summary>One document: at most one live child; a child that died is replaced on the next request.</summary>
    private sealed class Session(SandboxedRenderer owner, string workDirectory) : IRenderSession
    {
        private readonly string _work = Path.GetFullPath(workDirectory).TrimEnd(Path.DirectorySeparatorChar);
        private Child? _child;

        public async IAsyncEnumerable<RenderedPage> RenderAsync(RenderRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var input = Path.GetFullPath(request.InputPath);
            var output = Path.GetFullPath(request.OutputDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (!IsInside(input) || !IsInside(output))
            {
                throw new ArgumentException("The source and output must be inside the session's work directory.", nameof(request));
            }

            if (DropsUser)
            {
                // The renderer runs as an unprivileged user: hand it the work directory, the source and the output directory.
                LinuxSandbox.ChangeOwner(_work, owner._options.RunAsUser, owner._options.RunAsGroup);
                LinuxSandbox.ChangeOwner(input, owner._options.RunAsUser, owner._options.RunAsGroup);
                LinuxSandbox.ChangeOwner(output, owner._options.RunAsUser, owner._options.RunAsGroup);
            }

            var child = await EnsureChildAsync(cancellationToken).ConfigureAwait(false);
            await child.SendAsync(SandboxProtocol.Serialize(new SandboxRenderRequest(input, output, request.Pages, request.Review)), cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var message = await child.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                switch (message.Type)
                {
                    case SandboxMessageTypes.Page when message.Page is { } reported:
                        // Freeze the child while its files are validated and consumed.
                        child.Suspend();
                        await child.FailIfPeakOverLimitAsync().ConfigureAwait(false);
                        var page = SandboxOutputValidator.Validate(reported, output, request.Pages, owner._options.MaxFileBytes);
                        if (page is null)
                        {
                            throw await child.FailAsync(RenderErrorCodes.SandboxCrashed, "The render process reported an invalid page.").ConfigureAwait(false);
                        }

                        yield return page;
                        child.Resume();
                        await child.SendAsync(SandboxProtocol.Next, cancellationToken).ConfigureAwait(false);
                        break;
                    case SandboxMessageTypes.End:
                        await child.FailIfPeakOverLimitAsync().ConfigureAwait(false);
                        child.Progress();
                        yield break;
                    case SandboxMessageTypes.Failed when message.Code is { } code && RenderErrorCodesSet.IsKnown(code):
                        child.Progress();
                        throw new RenderException(code, Bounded(message.Message) ?? "The source cannot be rendered.");
                    case SandboxMessageTypes.Fatal when message.Code == RenderErrorCodes.MemoryLimit:
                        throw await child.FailAsync(RenderErrorCodes.MemoryLimit, "The render process ran out of memory.").ConfigureAwait(false);
                    default:
                        throw await child.FailAsync(RenderErrorCodes.SandboxCrashed, "The render process violated the protocol.").ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Endorses one page in the document's process (E12-T04). The child is stopped while its file is checked and read
        /// (<see cref="EndorsedOutputValidator"/>); the file is then deleted and its bytes returned.
        /// </summary>
        public async Task<Endorsing.EndorsedImage> EndorseAsync(Endorsing.EndorseRequest request, CancellationToken cancellationToken = default)
        {
            Endorsing.PageEndorser.Validate(request);
            var input = request.InputPath is null ? null : Path.GetFullPath(request.InputPath);
            var output = Path.GetFullPath(request.OutputDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if ((input is not null && !IsInside(input)) || !IsInside(output))
            {
                throw new ArgumentException("The source and output must be inside the session's work directory.", nameof(request));
            }

            if (DropsUser)
            {
                LinuxSandbox.ChangeOwner(_work, owner._options.RunAsUser, owner._options.RunAsGroup);
                if (input is not null)
                {
                    LinuxSandbox.ChangeOwner(input, owner._options.RunAsUser, owner._options.RunAsGroup);
                }

                LinuxSandbox.ChangeOwner(output, owner._options.RunAsUser, owner._options.RunAsGroup);
            }

            var child = await EnsureChildAsync(cancellationToken).ConfigureAwait(false);
            await child.SendAsync(SandboxProtocol.Serialize(new SandboxRenderRequest(input, output, null, false, new SandboxEndorseRequest(
                request.Frame, request.Format, request.Layout, request.Dpi, request.BlankWidthPx, request.BlankHeightPx))), cancellationToken).ConfigureAwait(false);
            var message = await child.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            switch (message.Type)
            {
                case SandboxMessageTypes.Endorsed when message.Endorsed is { } reported:
                    child.Suspend();
                    Endorsing.EndorsedImage? image;
                    try
                    {
                        await child.FailIfPeakOverLimitAsync().ConfigureAwait(false);
                        image = EndorsedOutputValidator.Read(reported, output, request.Format, owner._options.MaxFileBytes);
                        if (image is null)
                        {
                            throw await child.FailAsync(RenderErrorCodes.SandboxCrashed, "The render process reported an invalid endorsed page.").ConfigureAwait(false);
                        }

                        File.Delete(Path.Combine(output, reported.FileName));
                    }
                    finally
                    {
                        child.Resume();
                    }

                    return image;
                case SandboxMessageTypes.Failed when message.Code is { } code && RenderErrorCodesSet.IsKnown(code):
                    child.Progress();
                    throw new RenderException(code, Bounded(message.Message) ?? "The page cannot be endorsed.");
                case SandboxMessageTypes.Fatal when message.Code == RenderErrorCodes.MemoryLimit:
                    throw await child.FailAsync(RenderErrorCodes.MemoryLimit, "The render process ran out of memory.").ConfigureAwait(false);
                default:
                    throw await child.FailAsync(RenderErrorCodes.SandboxCrashed, "The render process violated the protocol.").ConfigureAwait(false);
            }
        }

        /// <summary>The live child, or a new one when there is none (or it died).</summary>
        private async Task<Child> EnsureChildAsync(CancellationToken cancellationToken)
        {
            if (_child is null || _child.HasEnded)
            {
                if (_child is not null)
                {
                    await _child.DisposeAsync().ConfigureAwait(false);
                    _child = null;
                }

                _child = await Child.StartAsync(owner, owner.StartMessage(_work, null), cancellationToken).ConfigureAwait(false);
            }

            return _child;
        }

        public async Task<SandboxProbeReport> ProbeAsync(IReadOnlyList<string> endpoints, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(_work);
            if (DropsUser)
            {
                LinuxSandbox.ChangeOwner(_work, owner._options.RunAsUser, owner._options.RunAsGroup);
            }

            _child = await Child.StartAsync(owner, owner.StartMessage(_work, endpoints), cancellationToken).ConfigureAwait(false);
            var message = await _child.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            return message is { Type: SandboxMessageTypes.Probe, Probe: { } report }
                ? report
                : throw new RenderSandboxUnavailableException("The probe did not report.");
        }

        public async ValueTask DisposeAsync()
        {
            if (_child is { } child)
            {
                _child = null;
                await child.DisposeAsync().ConfigureAwait(false);
            }
        }

        private bool IsInside(string path) =>
            path.StartsWith(_work + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !path.Contains("/../", StringComparison.Ordinal);

        private static string? Bounded(string? text) => text is null ? null : text.Length <= 300 ? text : text[..300];
    }

    /// <summary>A running sandbox process with its watchdog (memory, wall clock, progress).</summary>
    private sealed class Child : IAsyncDisposable
    {
        private readonly SandboxedRenderer _owner;
        private readonly Process _process;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _watchdog;
        private readonly Task _stderr;
        private readonly StringBuilder _diagnostics = new();
        private long _started = Stopwatch.GetTimestamp();
        private int _ready;
        private readonly Decoder _decoder = new UTF8Encoding(false, throwOnInvalidBytes: false).GetDecoder();
        private readonly byte[] _buffer = new byte[4096];
        private readonly char[] _chars = new char[4097];
        private readonly StringBuilder _line = new();
        private int _bufferOffset;
        private int _bufferCount;
        private long _lastProgress = Stopwatch.GetTimestamp();
        private int _suspended;
        private string? _killReason;

        private Child(SandboxedRenderer owner, Process process)
        {
            _owner = owner;
            _process = process;
            _watchdog = WatchAsync(_stop.Token);
            _stderr = DrainErrorsAsync();
        }

        public bool HasEnded => _killReason is not null || _process.HasExited;

        public static async Task<Child> StartAsync(SandboxedRenderer owner, SandboxStart start, CancellationToken cancellationToken)
        {
            Process process;
            try
            {
                process = Process.Start(owner.StartInfo(start)) ?? throw new RenderSandboxUnavailableException("The render process did not start.");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new RenderSandboxUnavailableException("The render process could not be started.", ex);
            }

            var child = new Child(owner, process);
            try
            {
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                startup.CancelAfter(owner._options.StartupTimeout);
                SandboxMessage ready;
                try
                {
                    ready = await child.ReceiveAsync(startup.Token).ConfigureAwait(false);
                }
                catch (RenderLimitException ex)
                {
                    throw new RenderSandboxUnavailableException("The render process did not start: " + ex.Message, ex);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new RenderSandboxUnavailableException("The render process did not become ready in time.");
                }

                if (ready is not { Type: SandboxMessageTypes.Ready, Controls: { } controls })
                {
                    throw new RenderSandboxUnavailableException(
                        "The render sandbox could not be applied: " + (ready.Message is { } m && m.Length <= 300 ? m : "no reason given") + ".");
                }

                owner.CheckPosture(controls);
                child.MarkReady();
                return child;
            }
            catch
            {
                await child.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public void Progress() => Interlocked.Exchange(ref _lastProgress, Stopwatch.GetTimestamp());

        /// <summary>The sandbox is applied: the document's time limits start now (startup has its own timeout).</summary>
        public void MarkReady()
        {
            Interlocked.Exchange(ref _started, Stopwatch.GetTimestamp());
            Progress();
            Volatile.Write(ref _ready, 1);
        }

        public async Task SendAsync(string line, CancellationToken cancellationToken)
        {
            try
            {
                await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw await FailAsync(RenderErrorCodes.SandboxCrashed, "The render process went away.", ex, ended: true).ConfigureAwait(false);
            }
        }

        /// <summary>The next message; a dead or killed child, an over-long line or garbage throws <see cref="RenderLimitException"/>.</summary>
        public async Task<SandboxMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            string? line;
            try
            {
                line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Kill("cancelled");
                throw;
            }
            catch (IOException ex)
            {
                throw await FailAsync(RenderErrorCodes.SandboxCrashed, "The render process output failed.", ex, ended: true).ConfigureAwait(false);
            }

            if (line is null)
            {
                throw await FailAsync(RenderErrorCodes.SandboxCrashed, "The render process ended without a result.", ended: true).ConfigureAwait(false);
            }

            Progress();
            try
            {
                return SandboxProtocol.Deserialize<SandboxMessage>(line)
                    ?? throw await FailAsync(RenderErrorCodes.SandboxCrashed, "The render process sent an empty message.").ConfigureAwait(false);
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw await FailAsync(RenderErrorCodes.SandboxCrashed, "The render process sent an invalid message.", ex).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Kills the child (if it still runs) and returns the exception for the outcome: the watchdog's reason if it killed
        /// the process, a CPU-limit signal, else <paramref name="code"/>.
        /// </summary>
        public async Task<RenderLimitException> FailAsync(string code, string message, Exception? inner = null, bool ended = false)
        {
            if (ended && Volatile.Read(ref _killReason) is null)
            {
                // The output ended: let the process finish dying on its own so its exit status tells why.
                await WaitForExitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }

            var exitedByItself = _process.HasExited && Volatile.Read(ref _killReason) is null;
            Kill(code);
            await WaitForExitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var reason = Volatile.Read(ref _killReason) ?? code;
            if (exitedByItself)
            {
                // Killed by the kernel: SIGXCPU is RLIMIT_CPU; SIGKILL from anyone but this worker is the OOM killer
                // (a container or cgroup memory limit). Other deaths are crashes.
                reason = _process.ExitCode switch
                {
                    128 + 24 => RenderErrorCodes.CpuLimit,
                    128 + 9 => RenderErrorCodes.MemoryLimit,
                    _ => code,
                };
            }

            var text = reason switch
            {
                RenderErrorCodes.Timeout => "The render process exceeded its time limit and was stopped.",
                RenderErrorCodes.MemoryLimit => "The render process exceeded its memory limit and was stopped.",
                RenderErrorCodes.CpuLimit => "The render process exceeded its CPU-time limit and was stopped.",
                _ => message,
            };
            string diagnostics;
            lock (_diagnostics)
            {
                diagnostics = _diagnostics.ToString();
            }

            LogStopped(_owner._logger, _process.Id, reason, diagnostics);
            return new RenderLimitException(reason == "cancelled" ? RenderErrorCodes.SandboxCrashed : reason, text, inner);
        }

        private async Task WaitForExitAsync(TimeSpan timeout)
        {
            try
            {
                await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }

        /// <summary>Stops every thread of the child (SIGSTOP) and waits until they are stopped.</summary>
        public void Suspend()
        {
            if (!LinuxSandbox.IsSupported || HasEnded)
            {
                return;
            }

            Interlocked.Exchange(ref _suspended, 1);
            if (!LinuxSandbox.StopAndWait(_process.Id, TimeSpan.FromSeconds(5)))
            {
                Kill(RenderErrorCodes.SandboxCrashed);
            }
        }

        public void Resume()
        {
            if (LinuxSandbox.IsSupported && !HasEnded)
            {
                LinuxSandbox.Continue(_process.Id);
            }

            Interlocked.Exchange(ref _suspended, 0);
            Progress();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    if (Volatile.Read(ref _suspended) == 1)
                    {
                        Resume();
                    }

                    // End of input ends the session; a child that does not leave promptly is killed.
                    try
                    {
                        _process.StandardInput.Close();
                    }
                    catch (IOException)
                    {
                    }

                    try
                    {
                        await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        Kill("session-ended");
                        await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                await _stop.CancelAsync().ConfigureAwait(false);
                try
                {
                    await Task.WhenAll(_watchdog, _stderr).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                _stop.Dispose();
                _process.Dispose();
            }
        }

        private void Kill(string reason)
        {
            if (Interlocked.CompareExchange(ref _killReason, reason, null) is not null)
            {
                return;
            }

            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        private async Task WatchAsync(CancellationToken stop)
        {
            var options = _owner._options;
            using var timer = new PeriodicTimer(options.SampleInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
                {
                    if (_process.HasExited)
                    {
                        return;
                    }

                    if (Volatile.Read(ref _suspended) == 1)
                    {
                        // The worker is consuming a page; that time is not the renderer's.
                        Progress();
                        continue;
                    }

                    if (Volatile.Read(ref _ready) == 1
                        && (Stopwatch.GetElapsedTime(Interlocked.Read(ref _started)) > options.DocumentTimeout
                            || Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastProgress)) > options.PageTimeout))
                    {
                        Kill(RenderErrorCodes.Timeout);
                        return;
                    }

                    if (ResidentBytes() > options.MemoryBytes)
                    {
                        Kill(RenderErrorCodes.MemoryLimit);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>
        /// The watchdog samples the resident size, so a fast host can decode an over-limit page between two samples;
        /// the kernel's peak (VmHWM) is checked before each page is accepted and at the end, so the limit always holds.
        /// </summary>
        public async Task FailIfPeakOverLimitAsync()
        {
            if (PeakResidentBytes() > _owner._options.MemoryBytes)
            {
                throw await FailAsync(RenderErrorCodes.MemoryLimit, "The render process exceeded its memory limit.").ConfigureAwait(false);
            }
        }

        private long PeakResidentBytes()
        {
            try
            {
                foreach (var line in File.ReadLines($"/proc/{_process.Id}/status"))
                {
                    if (line.StartsWith("VmHWM:", StringComparison.Ordinal))
                    {
                        var kib = line["VmHWM:".Length..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                        return long.Parse(kib, CultureInfo.InvariantCulture) * 1024;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
            {
            }

            return ResidentBytes();
        }

        private long ResidentBytes()
        {
            try
            {
                _process.Refresh();
                return _process.WorkingSet64;
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
        }

        /// <summary>Keeps the last 2 KiB of the child's stderr for the log of a failure (runtime errors; never document content).</summary>
        private async Task DrainErrorsAsync()
        {
            var buffer = new char[1024];
            try
            {
                int read;
                while ((read = await _process.StandardError.ReadAsync(buffer.AsMemory(), _stop.Token).ConfigureAwait(false)) > 0)
                {
                    lock (_diagnostics)
                    {
                        _diagnostics.Append(buffer, 0, read);
                        if (_diagnostics.Length > 2048)
                        {
                            _diagnostics.Remove(0, _diagnostics.Length - 2048);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }

        /// <summary>Reads one line of at most <see cref="SandboxProtocol.MaxLineChars"/> characters (an over-long line is a violation).</summary>
        private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var stream = _process.StandardOutput.BaseStream;
            _line.Clear();
            while (true)
            {
                if (_bufferOffset == _bufferCount)
                {
                    _bufferOffset = 0;
                    _bufferCount = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                    if (_bufferCount == 0)
                    {
                        return null;
                    }
                }

                var available = _buffer.AsSpan(_bufferOffset, _bufferCount - _bufferOffset);
                var newline = available.IndexOf((byte)'\n');
                var take = newline >= 0 ? newline : available.Length;
                var chars = _decoder.GetChars(_buffer, _bufferOffset, take, _chars, 0, flush: newline >= 0);
                _line.Append(_chars, 0, chars);
                _bufferOffset += newline >= 0 ? take + 1 : take;
                if (_line.Length > SandboxProtocol.MaxLineChars)
                {
                    throw await FailAsync(RenderErrorCodes.SandboxCrashed, "The render process sent an over-long message.").ConfigureAwait(false);
                }

                if (newline >= 0)
                {
                    return _line.ToString();
                }
            }
        }
    }
}

/// <summary>The sandbox itself could not be started or applied (a deployment problem, not the document's): the chunk is retried.</summary>
public sealed class RenderSandboxUnavailableException : Exception
{
    public RenderSandboxUnavailableException()
    {
    }

    public RenderSandboxUnavailableException(string message)
        : base(message)
    {
    }

    public RenderSandboxUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
