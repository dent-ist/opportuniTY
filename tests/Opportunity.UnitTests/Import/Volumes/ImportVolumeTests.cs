using AwesomeAssertions;

using Opportunity.Import.Volumes;

namespace Opportunity.UnitTests.Import.Volumes;

/// <summary>
/// E08-T04/T05: resolving load-file paths inside a volume on disk: symbolic links are never followed (inside or out),
/// letter case is forgiven, and a fuzz test over hostile paths never yields a file outside the volume or a link.
/// </summary>
public sealed class ImportVolumeTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "opp-volume-" + Guid.NewGuid().ToString("N"));
    private readonly string _share;
    private readonly string _volume;
    private readonly string _outside;

    public ImportVolumeTests()
    {
        _share = Path.Combine(_base, "share");
        _volume = Path.Combine(_share, "matter", "VOL001");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(Path.Combine(_volume, "NATIVES", "0001"));
        Directory.CreateDirectory(Path.Combine(_volume, "TEXT", "T001"));
        Directory.CreateDirectory(Path.Combine(_share, "other"));
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_volume, "NATIVES", "0001", "ABC0001.pdf"), "%PDF-1.7");
        File.WriteAllText(Path.Combine(_volume, "TEXT", "T001", "ABC0001.txt"), "text");
        File.WriteAllText(Path.Combine(_share, "other", "neighbour.txt"), "another matter");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "secret");
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Combine(_volume, "link-out.txt"), Path.Combine(_outside, "secret.txt"));
            File.CreateSymbolicLink(Path.Combine(_volume, "link-relative-out.txt"), "../../../outside/secret.txt");
            File.CreateSymbolicLink(Path.Combine(_volume, "link-neighbour.txt"), "../../other/neighbour.txt");
            File.CreateSymbolicLink(Path.Combine(_volume, "link-in.pdf"), "NATIVES/0001/ABC0001.pdf");
            File.CreateSymbolicLink(Path.Combine(_volume, "link-chain.pdf"), "link-in.pdf");
            File.CreateSymbolicLink(Path.Combine(_volume, "loop-a"), "loop-b");
            File.CreateSymbolicLink(Path.Combine(_volume, "loop-b"), "loop-a");
            File.CreateSymbolicLink(Path.Combine(_volume, "dangling.pdf"), "NATIVES/none.pdf");
            Directory.CreateSymbolicLink(Path.Combine(_volume, "DIR-OUT"), _outside);
            Directory.CreateSymbolicLink(Path.Combine(_volume, "DIR-IN"), Path.Combine(_volume, "NATIVES"));
            Directory.CreateSymbolicLink(Path.Combine(_volume, "DIR-ESCAPE-BACK"), "../../../outside/../share/matter/VOL001/TEXT");
        }
    }

    private ImportVolume Volume()
    {
        ImportVolume.TryOpen(new ImportVolumeOptions { VolumeShareRoot = _share }, "matter/VOL001", out var volume, out var error).Should().BeTrue(error);
        return volume!;
    }

    [Theory]
    [InlineData(@"NATIVES\0001\ABC0001.pdf", "NATIVES/0001/ABC0001.pdf")]
    [InlineData(@".\TEXT\T001\ABC0001.txt", "TEXT/T001/ABC0001.txt")]
    [InlineData(@"natives\0001\abc0001.PDF", "NATIVES/0001/ABC0001.pdf")]
    public void Files_inside_the_volume_resolve_to_their_real_path(string raw, string expected)
    {
        var file = Volume().Resolve(raw);

        file.Status.Should().Be(VolumeFileStatus.Found, file.Reason);
        file.FullPath.Should().Be(Path.Combine(Volume().Root, expected.Replace('/', Path.DirectorySeparatorChar)));
    }

    [Fact]
    public void Absolute_paths_resolve_after_the_strip_prefix_rebases_them()
    {
        var file = Volume().Resolve(@"\\vendor\deliveries\VOL001\NATIVES\0001\ABC0001.pdf", @"\\vendor\deliveries\VOL001");
        file.Status.Should().Be(VolumeFileStatus.Found, file.Reason);

        Volume().Resolve(@"\\vendor\deliveries\VOL001\NATIVES\0001\ABC0001.pdf").Status.Should().Be(VolumeFileStatus.Rejected);
    }

    [Theory]
    [InlineData(@"NATIVES\0001\missing.pdf")]
    [InlineData(@"NATIVES\0001")]
    [InlineData(@"NATIVES\0001\ABC0001.pdf\extra")]
    public void Absent_files_and_directories_are_missing(string raw) =>
        Volume().Resolve(raw).Status.Should().Be(VolumeFileStatus.Missing);

    [Theory]
    [InlineData(@"..\..\outside\secret.txt")]
    [InlineData(@"..\other\neighbour.txt")]
    [InlineData("/etc/passwd")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"\\server\share\secret.txt")]
    public void Traversal_and_absolute_paths_are_rejected(string raw) =>
        Volume().Resolve(raw).Status.Should().Be(VolumeFileStatus.Rejected);

    [Theory]
    [InlineData("link-out.txt")]
    [InlineData("link-relative-out.txt")]
    [InlineData("link-neighbour.txt")]
    [InlineData(@"DIR-OUT\secret.txt")]
    public void Symbolic_links_that_leave_the_volume_are_rejected(string raw)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symbolic links need elevated rights on Windows.");
        var file = Volume().Resolve(raw);

        file.Status.Should().Be(VolumeFileStatus.Rejected);
        file.FullPath.Should().BeNull();
    }

    [Theory]
    [InlineData("link-in.pdf", "NATIVES/0001/ABC0001.pdf")]
    [InlineData("link-chain.pdf", "NATIVES/0001/ABC0001.pdf")]
    [InlineData(@"DIR-IN\0001\ABC0001.pdf", "NATIVES/0001/ABC0001.pdf")]
    [InlineData(@"DIR-ESCAPE-BACK\T001\ABC0001.txt", "TEXT/T001/ABC0001.txt")]
    public void Symbolic_links_are_rejected_even_when_they_stay_inside_the_volume(string raw, string target)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symbolic links need elevated rights on Windows.");
        File.Exists(Path.Combine(Volume().Root, target.Replace('/', Path.DirectorySeparatorChar))).Should().BeTrue("the link's target exists");
        var file = Volume().Resolve(raw);

        file.Status.Should().Be(VolumeFileStatus.Rejected);
        file.FullPath.Should().BeNull();
        file.Reason.Should().Contain("symbolic link");
    }

    [Theory]
    [InlineData("loop-a")]
    [InlineData("dangling.pdf")]
    public void Link_loops_and_dangling_links_never_resolve(string raw)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symbolic links need elevated rights on Windows.");
        Volume().Resolve(raw).Status.Should().NotBe(VolumeFileStatus.Found);
    }

    [Fact]
    public void Try_resolve_refuses_only_rejected_paths_and_names_missing_files_inside_the_volume()
    {
        var volume = Volume();
        volume.TryResolve(@"NATIVES\0001\ABC0001.pdf", null, out var found, out _).Should().BeTrue();
        File.Exists(found).Should().BeTrue();
        volume.TryResolve(@"NATIVES\0001\none.pdf", null, out var missing, out _).Should().BeTrue();
        missing.Should().StartWith(volume.Root + Path.DirectorySeparatorChar);
        File.Exists(missing).Should().BeFalse();
        volume.TryResolve(@"..\..\outside\secret.txt", null, out var rejected, out var error).Should().BeFalse();
        (rejected, error).Should().Match<(string?, string?)>(r => r.Item1 == null && r.Item2 != null);
    }

    [Fact]
    public void A_volume_folder_must_exist_inside_the_share()
    {
        ImportVolume.TryOpen(new ImportVolumeOptions(), "matter/VOL001", out _, out var error).Should().BeFalse();
        error.Should().Contain("Import:VolumeShareRoot");
        ImportVolume.TryOpen(new ImportVolumeOptions { VolumeShareRoot = _share }, "../outside", out _, out _).Should().BeFalse();
        ImportVolume.TryOpen(new ImportVolumeOptions { VolumeShareRoot = _share }, "matter/VOL999", out _, out _).Should().BeFalse();
        ImportVolume.TryOpen(new ImportVolumeOptions { VolumeShareRoot = _share }, _outside, out _, out _).Should().BeFalse();
        if (!OperatingSystem.IsWindows())
        {
            ImportVolume.TryOpen(new ImportVolumeOptions { VolumeShareRoot = _share }, "matter/VOL001/DIR-OUT", out _, out _).Should().BeFalse();
        }

        ImportVolume.TryOpen(new ImportVolumeOptions { VolumeShareRoot = _share }, null, out var whole, out _).Should().BeTrue();
        whole!.Resolve("matter/VOL001/NATIVES/0001/ABC0001.pdf").Status.Should().Be(VolumeFileStatus.Found);
    }

    [Fact]
    public void Fuzzed_paths_never_resolve_outside_the_volume()
    {
        string[] pieces =
        [
            "..", ".", "", "NATIVES", "0001", "ABC0001.pdf", "TEXT", "T001", "ABC0001.txt", "link-out.txt", "link-in.pdf", "DIR-OUT", "DIR-IN",
            "DIR-ESCAPE-BACK", "loop-a", "secret.txt", "outside", "share", "matter", "VOL001", "other", "neighbour.txt", "C:", "\\\\", "~",
            "NATIVES/0001/ABC0001.pdf", @"TEXT\T001\ABC0001.txt",
        ];
        string[] separators = ["\\", "/", "//", ""];
        var volume = Volume();
        var random = new Random(4);
        var found = 0;
        for (var i = 0; i < 5_000; i++)
        {
            var raw = string.Concat(Enumerable.Range(0, random.Next(1, 6)).Select(_ => pieces[random.Next(pieces.Length)] + separators[random.Next(separators.Length)]));
            if (random.Next(5) == 0)
            {
                raw = (random.Next(2) == 0 ? _volume : _base) + "/" + raw;
            }

            var file = volume.Resolve(raw, random.Next(3) == 0 ? _volume : null);
            if (file.Status != VolumeFileStatus.Found)
            {
                file.FullPath.Should().BeNull();
                continue;
            }

            found++;
            file.FullPath.Should().StartWith(volume.Root + Path.DirectorySeparatorChar, raw);
            File.Exists(file.FullPath).Should().BeTrue(raw);
            File.ReadAllText(file.FullPath!).Should().NotBe("secret").And.NotBe("another matter", raw);
            new FileInfo(file.FullPath!).LinkTarget.Should().BeNull(raw);
        }

        found.Should().BeGreaterThan(0, "the fuzzer also produces valid paths");
    }

    public void Dispose()
    {
        if (Directory.Exists(_base))
        {
            Directory.Delete(_base, recursive: true);
        }
    }
}
