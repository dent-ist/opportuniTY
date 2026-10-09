using System.Net;
using System.Net.Sockets;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Opportunity.DataGenerator.Corpus.Volumes;
using Opportunity.Rendering.Renderers;
using Opportunity.Rendering.Sandboxing;

namespace Opportunity.UnitTests.Rendering.Sandbox;

/// <summary>
/// E11-T03: the render sandbox for real (Linux): child processes per document, the kernel controls they apply, limits
/// that kill only the offending document, and a hostile corpus (generated in memory) that renders or fails safely
/// without a single connection to a canary listener. Work directories live under a temp directory removed afterwards.
/// </summary>
public sealed class SandboxedRendererTests : IDisposable
{
    private readonly string _root;
    private readonly Canary _canary = new();

    public SandboxedRendererTests()
    {
        // Under /tmp with the default mode, so the unprivileged sandbox user can reach it when the tests run as root.
        _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "opp-sandbox-test-" + Guid.NewGuid().ToString("N"))).FullName;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _canary.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Pages_render_in_the_sandbox_exactly_as_in_process()
    {
        SkipUnlessLinux();
        var renderer = Sandboxed();
        var inProcess = new RasterRenderer();
        var pdf = SyntheticPdf.Create(2, colorBox: true);
        var tiff = PageImages.Tiff(["S-1", "S-2"], width: 850, height: 1100, dpi: 100);

        renderer.Identity.Should().BeEquivalentTo(inProcess.Identity, "a sandboxed rendition is the same rendition");
        foreach (var source in new[] { pdf, tiff })
        {
            var sandboxed = await RenderAsync(renderer, source);
            var direct = await RenderAsync(inProcess, source);
            sandboxed.Error.Should().BeNull();
            sandboxed.Pages.Should().HaveCount(2);
            sandboxed.Pages.Select(p => p.Review).Should().BeEquivalentTo(direct.Pages.Select(p => p.Review), o => o.WithStrictOrdering());
            sandboxed.Pages.Select(p => p.Thumbnail).Should().BeEquivalentTo(direct.Pages.Select(p => p.Thumbnail), o => o.WithStrictOrdering());
        }
    }

    [Fact]
    public async Task Pages_are_endorsed_in_the_sandbox_byte_for_byte_as_in_process()
    {
        SkipUnlessLinux();
        var renderer = Sandboxed();
        var work = Directory.CreateDirectory(Path.Combine(_root, "endorse")).FullName;
        var tiff = Path.Combine(work, "source.tif");
        await File.WriteAllBytesAsync(tiff, PageImages.Tiff(["E-1", "E-2"], width: 850, height: 1100, dpi: 100), Ct);
        var jpeg = Path.Combine(work, "source.jpg");
        await File.WriteAllBytesAsync(jpeg, PageImages.Jpeg("E-3", 425, 550, 50), Ct);
        var output = Directory.CreateDirectory(Path.Combine(work, "out")).FullName;
        var layout = new Opportunity.Rendering.Endorsing.EndorsementLayout(
        [
            new(Opportunity.Core.Productions.EndorsementPosition.BottomLeft, "CONFIDENTIAL"),
            new(Opportunity.Core.Productions.EndorsementPosition.BottomRight, "ABC0000002"),
        ]);
        Opportunity.Rendering.Endorsing.EndorseRequest[] requests =
        [
            new(tiff, 1, output, Opportunity.Core.Pages.PageImageFormat.TiffG4, layout),
            new(jpeg, 0, output, Opportunity.Core.Pages.PageImageFormat.Jpeg, layout),
            new(null, 0, output, Opportunity.Core.Pages.PageImageFormat.TiffG4, layout with { BodyLines = ["Withheld for Privilege"] }, 300, 2550, 3300),
            new(tiff, 0, output, Opportunity.Core.Pages.PageImageFormat.TiffG4, layout, Redactions:
            [
                new(new Opportunity.Core.Redactions.NormalizedRect(100_000, 100_000, 400_000, 200_000), Opportunity.Core.Redactions.RedactionType.Black),
                new(new Opportunity.Core.Redactions.NormalizedRect(200_000, 600_000, 600_000, 100_000), Opportunity.Core.Redactions.RedactionType.Labelled,
                    "Redacted – PII"),
            ]),
        ];

        var session = renderer.BeginDocument(work);
        await using (session)
        {
            foreach (var request in requests)
            {
                var sandboxed = await session.EndorseAsync(request, Ct);
                var direct = Opportunity.Rendering.Endorsing.PageEndorser.Endorse(request);
                sandboxed.Content.Should().Equal(direct.Content, "an endorsed page is the same in the sandbox and in process");
                sandboxed.Should().BeEquivalentTo(direct, o => o.Excluding(i => i.Content));
                Directory.EnumerateFiles(output).Should().BeEmpty("the worker removes the endorsed file once read");
            }

            var garbage = Path.Combine(work, "garbage");
            await File.WriteAllBytesAsync(garbage, [1, 2, 3, 4, 5], Ct);
            var failed = async () => await session.EndorseAsync(requests[0] with { InputPath = garbage, Frame = 0 }, Ct);
            (await failed.Should().ThrowAsync<RenderException>()).Which.Code.Should().Be(RenderErrorCodes.Unsupported);
            ChildProcesses().Should().ContainSingle("one document, one process, also for its endorsements");
        }

        ChildProcesses().Should().BeEmpty();
    }

    [Fact]
    public async Task One_process_renders_every_source_of_a_document_and_ends_with_the_session()
    {
        SkipUnlessLinux();
        var renderer = Sandboxed();
        var work = Directory.CreateDirectory(Path.Combine(_root, "doc")).FullName;
        var session = renderer.BeginDocument(work);
        await using (session)
        {
            for (var i = 0; i < 2; i++)
            {
                var input = Path.Combine(work, $"source-{i}");
                await File.WriteAllBytesAsync(input, SyntheticPdf.Create(1), Ct);
                var output = Directory.CreateDirectory(Path.Combine(work, $"out-{i}")).FullName;
                await foreach (var page in session.RenderAsync(new RenderRequest(input, output), Ct))
                {
                    page.Review.Should().NotBeNull();
                }
            }

            ChildProcesses().Should().ContainSingle("one document, one process");
        }

        ChildProcesses().Should().BeEmpty("the process is torn down with the session");
    }

    [Fact]
    public async Task The_sandbox_has_no_network_no_files_outside_its_work_directory_no_processes_and_a_clean_environment()
    {
        SkipUnlessLinux();
        Environment.SetEnvironmentVariable("OPP_SANDBOX_TEST_SECRET", "not-a-real-secret");
        var renderer = Sandboxed();

        var report = await renderer.ProbeAsync(Path.Combine(_root, "probe"), [_canary.Endpoint, "127.0.0.1:1"], Ct);

        report.ConnectedEndpoints.Should().BeEmpty();
        report.EndpointErrors.Should().HaveCount(2);
        _canary.Connections.Should().Be(0);
        report.OpenedUdpSocket.Should().BeFalse();
        report.OpenedUnixSocket.Should().BeFalse();
        report.StartedProcess.Should().BeFalse();
        report.SignalledParent.Should().BeFalse();
        report.ReadParentEnvironment.Should().BeFalse();
        report.EnvironmentNames.Should().OnlyContain(n => n.StartsWith("DOTNET_", StringComparison.Ordinal) || n == "TZ");
        report.Controls.Seccomp.Should().BeTrue();
        report.Controls.NoNewPrivileges.Should().BeTrue();
        report.Controls.ResourceLimits.Should().BeTrue();
        report.Controls.UserId.Should().NotBe(0u, "the renderer never runs as root");
        if (report.Controls.LandlockAbi > 0)
        {
            report.ReadOutsideWorkDirectory.Should().BeFalse();
            report.WroteOutsideWorkDirectory.Should().BeFalse();
        }
    }

    [Fact]
    public async Task A_hostile_corpus_renders_or_fails_safely_within_limits_and_never_connects()
    {
        SkipUnlessLinux();
        var renderer = Sandboxed(o => o.DocumentTimeout = TimeSpan.FromSeconds(60));

        foreach (var file in HostileFiles.All(_canary.Url))
        {
            var result = await RenderAsync(renderer, file.Bytes);
            var because = $"{file.Name}: {result.Error}";
            result.Error.Should().NotBe(RenderErrorCodes.SandboxCrashed, because);
            switch (file.Expected)
            {
                case HostileOutcome.Renders:
                    result.Error.Should().BeNull(because);
                    result.Pages.Should().NotBeEmpty(because).And.OnlyContain(p => p.Review != null && p.Error == null, because);
                    break;
                case HostileOutcome.FailsPage:
                    result.Pages.Should().OnlyContain(p => p.Review == null && p.Error != null, because);
                    break;
                case HostileOutcome.Fails:
                    result.Error.Should().BeOneOf([RenderErrorCodes.Unsupported, RenderErrorCodes.Unreadable], because);
                    break;
                case HostileOutcome.RendersOrFails:
                    break;
            }

            // Whatever the input smuggled in, a stored rendition is a PNG the renderer drew.
            result.Pages.Where(p => p.Review != null).Should().OnlyContain(p => p.ReviewHead.StartsWith("\u0089PNG", StringComparison.Ordinal), because);
        }

        _canary.Connections.Should().Be(0, "no hostile file made the renderer connect anywhere");
    }

    [Fact]
    public async Task A_document_that_stalls_is_killed_and_the_next_document_renders()
    {
        SkipUnlessLinux();
        var renderer = Sandboxed(o => o.PageTimeout = TimeSpan.FromSeconds(1));

        var slow = await RenderAsync(renderer, HostileFiles.PdfSlowPage(400_000));
        var next = await RenderAsync(renderer, SyntheticPdf.Create(1));

        slow.Error.Should().Be(RenderErrorCodes.Timeout);
        slow.Limit.Should().BeTrue();
        next.Error.Should().BeNull();
        next.Pages.Should().ContainSingle();
    }

    [Fact]
    public async Task A_document_over_its_CPU_time_is_killed_by_the_kernel()
    {
        SkipUnlessLinux();
        var renderer = Sandboxed(o => o.CpuSeconds = 1);

        var slow = await RenderAsync(renderer, HostileFiles.PdfSlowPage(400_000));

        slow.Error.Should().Be(RenderErrorCodes.CpuLimit);
    }

    [Fact]
    public async Task A_document_over_its_memory_limit_is_killed()
    {
        SkipUnlessLinux();
        var renderer = Sandboxed(o => o.MemoryBytes = 160L << 20);

        var big = await RenderAsync(renderer, PageImages.Png("big", 7_000, 7_000, 300));

        big.Error.Should().Be(RenderErrorCodes.MemoryLimit);
        (await RenderAsync(renderer, SyntheticPdf.Create(1))).Error.Should().BeNull();
    }

    [Fact]
    public async Task A_renderer_that_lies_about_its_output_is_stopped_and_nothing_outside_is_used()
    {
        SkipUnlessLinux();
        // A stand-in for a compromised renderer: claims a sandbox, then reports a page whose file is a system file.
        var script = Path.Combine(_root, "evil-child.sh");
        await File.WriteAllTextAsync(script, """
            echo '{"type":"ready","controls":{"userId":1000,"noNewPrivileges":true,"resourceLimits":true,"landlockAbi":0,"seccomp":true}}'
            read request
            echo '{"type":"page","page":{"index":0,"widthPt":612,"heightPt":792,"colorMode":"Gray","review":{"fileName":"../../../../etc/passwd","widthPx":10,"heightPx":10,"dpi":72,"format":"Png"}}}'
            read next
            """, Ct);
        var renderer = Sandboxed(o =>
        {
            o.HostPath = "/bin/sh";
            o.ChildAssemblyPath = script;
            o.ProtectWorkerWithoutLandlock = false;
        });

        var result = await RenderAsync(renderer, SyntheticPdf.Create(1));

        result.Error.Should().Be(RenderErrorCodes.SandboxCrashed);
        result.Pages.Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_sandbox_is_a_deployment_error_not_a_document_failure()
    {
        SkipUnlessLinux();
        var renderer = Sandboxed(o => o.ChildAssemblyPath = Path.Combine(_root, "missing.dll"));

        var act = () => RenderAsync(renderer, SyntheticPdf.Create(1));

        await act.Should().ThrowAsync<RenderSandboxUnavailableException>();
    }

    private static void SkipUnlessLinux() => Assert.SkipUnless(OperatingSystem.IsLinux(), "The render sandbox and PDFium are Linux-only.");

    private static SandboxedRenderer Sandboxed(Action<RenderSandboxOptions>? configure = null)
    {
        var options = new RenderSandboxOptions { ProtectWorkerWithoutLandlock = false };
        configure?.Invoke(options);
        return new SandboxedRenderer(null, options, NullLogger<SandboxedRenderer>.Instance);
    }

    /// <summary>Renders one source in a fresh work directory; collects page facts and the first bytes of each review raster.</summary>
    private async Task<Result> RenderAsync(IRenderer renderer, byte[] source)
    {
        var work = Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var input = Path.Combine(work, "source");
            await File.WriteAllBytesAsync(input, source, Ct);
            var output = Directory.CreateDirectory(Path.Combine(work, "out")).FullName;
            var pages = new List<PageFacts>();
            var session = renderer.BeginDocument(work);
            await using (session)
            {
                try
                {
                    await foreach (var page in session.RenderAsync(new RenderRequest(input, output), Ct))
                    {
                        var head = page.Review is { } r ? System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(r.Path).AsSpan(0, 4)) : "";
                        pages.Add(new PageFacts(page.Index, page.Error, Strip(page.Review), Strip(page.Thumbnail), head));
                    }
                }
                catch (RenderException ex)
                {
                    return new Result(pages, ex.Code, ex is RenderLimitException);
                }
            }

            return new Result(pages, null, false);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static object? Strip(RasterFile? file) => file is null ? null : new { file.WidthPx, file.HeightPx, file.Dpi, file.Format, Bytes = File.ReadAllBytes(file.Path) };

    private static List<int> ChildProcesses()
    {
        var self = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var children = new List<int>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var pid))
            {
                continue;
            }

            try
            {
                var stat = File.ReadAllText(Path.Combine(directory, "stat"));
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                var cmdline = File.ReadAllText(Path.Combine(directory, "cmdline"));
                if (fields[1] == self && fields[0] != "Z" && cmdline.Contains(RenderSandboxOptions.ChildAssemblyFileName, StringComparison.Ordinal))
                {
                    children.Add(pid);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return children;
    }

    private sealed record PageFacts(int Index, string? Error, object? Review, object? Thumbnail, string ReviewHead);

    private sealed record Result(List<PageFacts> Pages, string? Error, bool Limit);

    /// <summary>A loopback listener that counts every connection made to it.</summary>
    private sealed class Canary : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private int _connections;

        public Canary()
        {
            _listener.Start();
            _ = AcceptAsync();
        }

        public string Endpoint => $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public string Url => $"http://{Endpoint}/";

        public int Connections => Volatile.Read(ref _connections);

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref _connections);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }
    }
}
