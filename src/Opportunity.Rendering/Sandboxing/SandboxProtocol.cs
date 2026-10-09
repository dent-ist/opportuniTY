using System.Text.Json;
using System.Text.Json.Serialization;

using Opportunity.Core.Pages;
using Opportunity.Rendering.Renderers;

namespace Opportunity.Rendering.Sandboxing;

/// <summary>
/// The line protocol between the render worker and its sandboxed child (one JSON object per line, UTF-8).
/// <list type="number">
/// <item>Parent → child: one <see cref="SandboxStart"/>. The child applies the sandbox and answers <c>ready</c> with the
/// controls in force (or <c>fatal</c>).</item>
/// <item>Parent → child: a <see cref="SandboxRenderRequest"/> per source. The child answers one <c>page</c> message per
/// page and waits for <see cref="SandboxProtocol.Next"/> before rendering the next one (the parent uploads and deletes
/// the page's files in between, so disk stays at about one page), then <c>end</c>, or <c>failed</c> with a
/// <see cref="RenderErrorCodes"/> code.</item>
/// <item>Parent → child: a <see cref="SandboxRenderRequest"/> with <c>endorse</c> set per page to endorse (E12-T04). The
/// child writes the endorsed page and answers <c>endorsed</c>, or <c>failed</c> with a <see cref="RenderErrorCodes"/> code.</item>
/// <item>The parent closes the child's stdin to end the session; the child exits.</item>
/// </list>
/// Everything the child sends is untrusted: lines are bounded, codes are checked against the known list and every file
/// it names is validated before the parent touches it (<see cref="SandboxOutputValidator"/>).
/// </summary>
internal static class SandboxProtocol
{
    public const string Next = "next";

    public const int MaxLineChars = 16 * 1024;

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, typeof(T), SandboxJsonContext.Default);

    public static T? Deserialize<T>(string line) => (T?)JsonSerializer.Deserialize(line, typeof(T), SandboxJsonContext.Default);
}

internal sealed record SandboxStart(
    string WorkDirectory,
    RenderSettings Settings,
    SandboxLimits Limits,
    uint? RunAsUser,
    uint? RunAsGroup,
    IReadOnlyList<string>? ProbeEndpoints = null);

/// <param name="CpuSeconds">RLIMIT_CPU: CPU time of the whole process.</param>
/// <param name="DataBytes">RLIMIT_DATA: private writable memory (heap, mappings) of the process.</param>
/// <param name="FileBytes">RLIMIT_FSIZE: largest file the process may write.</param>
/// <param name="OpenFiles">RLIMIT_NOFILE.</param>
internal sealed record SandboxLimits(long CpuSeconds, long DataBytes, long FileBytes, int OpenFiles);

/// <param name="InputPath">The source; null only for an endorsement of a blank (generated) page.</param>
/// <param name="Endorse">Set for an endorsement (E12-T04): the child endorses one page and answers <c>endorsed</c> (or <c>failed</c>).</param>
internal sealed record SandboxRenderRequest(string? InputPath, string OutputDirectory, IReadOnlyList<int>? Pages, bool Review, SandboxEndorseRequest? Endorse = null);

internal sealed record SandboxEndorseRequest(
    int Frame, PageImageFormat Format, Endorsing.EndorsementLayout Layout, int? Dpi, int BlankWidthPx, int BlankHeightPx);

/// <param name="FileName">A plain file name inside the request's output directory.</param>
internal sealed record SandboxEndorsed(string FileName, int WidthPx, int HeightPx, int Dpi, PageImageFormat Format, PageColorMode ColorMode);

internal static class SandboxMessageTypes
{
    public const string Ready = "ready";
    public const string Page = "page";
    public const string End = "end";
    public const string Failed = "failed";
    public const string Fatal = "fatal";
    public const string Probe = "probe";
    public const string Endorsed = "endorsed";
}

internal sealed record SandboxMessage(
    string Type,
    SandboxControls? Controls = null,
    SandboxPage? Page = null,
    string? Code = null,
    string? Message = null,
    SandboxProbeReport? Probe = null,
    SandboxEndorsed? Endorsed = null);

internal sealed record SandboxPage(
    int Index,
    decimal WidthPt,
    decimal HeightPt,
    PageColorMode ColorMode,
    SandboxRaster? Review,
    SandboxRaster? Thumbnail,
    string? Error);

/// <param name="FileName">A plain file name inside the request's output directory.</param>
internal sealed record SandboxRaster(string FileName, int WidthPx, int HeightPx, int Dpi, PageImageFormat Format);

/// <summary>The kernel controls in force in a render sandbox process (logged by the worker, asserted by tests).</summary>
/// <param name="UserId">Effective user the renderer runs as.</param>
/// <param name="NoNewPrivileges">no_new_privs is set (no set-user-ID or file-capability gain).</param>
/// <param name="ResourceLimits">CPU, data, file-size, core and open-file limits are set.</param>
/// <param name="LandlockAbi">Landlock ABI applied (0: unavailable; 4+: TCP denied; 6+: signals scoped).</param>
/// <param name="Seccomp">The seccomp filter is installed (no sockets, exec, ptrace, namespaces, other processes).</param>
public sealed record SandboxControls(uint UserId, bool NoNewPrivileges, bool ResourceLimits, int LandlockAbi, bool Seccomp);

/// <summary>What a probe child could do after applying the sandbox; every field should be false (blocked).</summary>
/// <param name="ConnectedEndpoints">Endpoints (host:port) a TCP connection succeeded to.</param>
public sealed record SandboxProbeReport(
    SandboxControls Controls,
    IReadOnlyList<string> ConnectedEndpoints,
    IReadOnlyList<string> EndpointErrors,
    bool OpenedUdpSocket,
    bool OpenedUnixSocket,
    bool ReadOutsideWorkDirectory,
    bool WroteOutsideWorkDirectory,
    bool ReadParentEnvironment,
    bool SignalledParent,
    bool StartedProcess,
    IReadOnlyList<string> EnvironmentNames);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(SandboxStart))]
[JsonSerializable(typeof(SandboxRenderRequest))]
[JsonSerializable(typeof(SandboxMessage))]
internal sealed partial class SandboxJsonContext : JsonSerializerContext;
