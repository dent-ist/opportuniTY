namespace Opportunity.Rendering.Sandboxing;

/// <summary>Configuration section <c>Render:Sandbox</c> (E11-T03, docs/architecture/rendering.md).</summary>
public sealed class RenderSandboxOptions
{
    public const string SectionName = "Render:Sandbox";

    /// <summary>File name of the sandbox entry point next to the worker (<c>Opportunity.Rendering.Sandbox</c>).</summary>
    public const string ChildAssemblyFileName = "Opportunity.Rendering.Sandbox.dll";

    /// <summary>
    /// Render every document in a sandboxed child process (default). False renders inside the worker process, for
    /// development on platforms without the sandbox only; never in a deployment that processes real data.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Wall-clock limit of one document's process.</summary>
    public TimeSpan DocumentTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Longest time without progress (a page or the end of a source) before the process is killed.</summary>
    public TimeSpan PageTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Time the process may take to start and apply the sandbox.</summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Resident memory limit; the worker samples the process every <see cref="SampleInterval"/> and kills it above this.</summary>
    public long MemoryBytes { get; set; } = 1L << 30;

    /// <summary>RLIMIT_DATA in the process (allocations beyond it fail at once); twice <see cref="MemoryBytes"/> when 0.</summary>
    public long DataBytes { get; set; }

    /// <summary>RLIMIT_CPU: CPU seconds of the process (SIGXCPU, then SIGKILL).</summary>
    public int CpuSeconds { get; set; } = 300;

    /// <summary>RLIMIT_FSIZE: largest file the process may write (a page raster).</summary>
    public long MaxFileBytes { get; set; } = 256L << 20;

    /// <summary>RLIMIT_NOFILE.</summary>
    public int MaxOpenFiles { get; set; } = 128;

    /// <summary>How often memory, time and progress are checked.</summary>
    public TimeSpan SampleInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The unprivileged user (and group) the renderer switches to when the worker runs as root (development, or an
    /// image run with a root user). The worker image runs as a non-root user and the renderer keeps that user.
    /// </summary>
    public uint RunAsUser { get; set; } = 65534;

    public uint RunAsGroup { get; set; } = 65534;

    /// <summary>Refuse to render when the seccomp filter cannot be installed (Linux x64/arm64). Default true.</summary>
    public bool RequireSeccomp { get; set; } = true;

    /// <summary>Refuse to render without Landlock (kernel 5.13+ with Landlock enabled). Default false: see the rendering doc.</summary>
    public bool RequireLandlock { get; set; }

    /// <summary>
    /// When Landlock is unavailable, make the worker process non-dumpable so a renderer running as the same user cannot
    /// read its environment or memory through /proc. Default true.
    /// </summary>
    public bool ProtectWorkerWithoutLandlock { get; set; } = true;

    /// <summary>The dotnet host; by default the one running the worker.</summary>
    public string? HostPath { get; set; }

    /// <summary>The sandbox entry assembly; by default <see cref="ChildAssemblyFileName"/> in the worker's directory.</summary>
    public string? ChildAssemblyPath { get; set; }

    public long EffectiveDataBytes => DataBytes > 0 ? DataBytes : MemoryBytes * 2;

    public void Validate()
    {
        Require(DocumentTimeout > TimeSpan.Zero, nameof(DocumentTimeout));
        Require(PageTimeout > TimeSpan.Zero, nameof(PageTimeout));
        Require(StartupTimeout > TimeSpan.Zero, nameof(StartupTimeout));
        Require(SampleInterval > TimeSpan.Zero, nameof(SampleInterval));
        Require(MemoryBytes >= 64L << 20, nameof(MemoryBytes));
        Require(DataBytes == 0 || DataBytes >= 64L << 20, nameof(DataBytes));
        Require(CpuSeconds > 0, nameof(CpuSeconds));
        Require(MaxFileBytes > 0, nameof(MaxFileBytes));
        Require(MaxOpenFiles >= 32, nameof(MaxOpenFiles));
        Require(RunAsUser != 0, nameof(RunAsUser));
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{SectionName}:{name} is out of range.");
        }
    }
}
