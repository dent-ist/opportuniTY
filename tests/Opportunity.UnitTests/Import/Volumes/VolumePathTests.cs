using AwesomeAssertions;

using Opportunity.Import.Volumes;

namespace Opportunity.UnitTests.Import.Volumes;

/// <summary>E08-T04: lexical rules for load-file paths (separators, rebasing, traversal), including a fuzz test.</summary>
public class VolumePathTests
{
    public static TheoryData<string, string?, string> Accepted => new()
    {
        { @"NATIVES\0001\ABC0000001.pdf", null, "NATIVES/0001/ABC0000001.pdf" },
        { "NATIVES/0001/ABC0000001.pdf", null, "NATIVES/0001/ABC0000001.pdf" },
        { @".\TEXT\T001\ABC0000001.txt", null, "TEXT/T001/ABC0000001.txt" },
        { @"  TEXT\\T001\.\a.txt ", null, "TEXT/T001/a.txt" },
        { @"\\fileserver\exports\VOL001\NATIVES\a.msg", @"\\fileserver\exports\VOL001", "NATIVES/a.msg" },
        { @"\\FILESERVER\Exports\VOL001\NATIVES\a.msg", @"\\fileserver\exports\VOL001\", "NATIVES/a.msg" },
        { @"D:\Productions\VOL001\TEXT\b.txt", @"D:/Productions/VOL001", "TEXT/b.txt" },
        { "/mnt/vendor/VOL001/TEXT/b.txt", "/mnt/vendor", "VOL001/TEXT/b.txt" },
        { "NATIVES/Ünïcødé 名前 file.docx", null, "NATIVES/Ünïcødé 名前 file.docx" },
        { "a..b/c...d.txt", null, "a..b/c...d.txt" },
    };

    public static TheoryData<string, string?> Rejected => new()
    {
        { @"..\..\etc\passwd", null },
        { "NATIVES/../../secret.txt", null },
        { @"NATIVES\..\NATIVES\a.pdf", null },
        { @"NATIVES\...\a.pdf", null },
        { "/etc/passwd", null },
        { @"\etc\passwd", null },
        { @"\\server\share\a.pdf", null },
        { @"\\?\C:\a.pdf", null },
        { @"\\.\PhysicalDrive0", null },
        { @"C:\Windows\win.ini", null },
        { "C:relative.txt", null },
        { "file:///etc/passwd", null },
        { "NATIVES/a.pdf:stream", null },
        { "NATIVES/a*.pdf", null },
        { "NATIVES/a\0.pdf", null },
        { "NATIVES/a\n.pdf", null },
        { "", null },
        { "   ", null },
        { @".\.\", null },
        { @"\\fileserver\exports\VOL002\a.msg", @"\\fileserver\exports\VOL001" },
        { @"\\fileserver\exports\VOL0012\a.msg", @"\\fileserver\exports\VOL001" },
        { @"D:\Productions\VOL001\..\x.txt", @"D:\Productions\VOL001" },
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public void Relative_and_rebased_paths_parse(string raw, string? strip, string expected)
    {
        VolumePath.TryParse(raw, strip, out var segments, out var error).Should().BeTrue(error);
        VolumePath.Display(segments).Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(Rejected))]
    public void Traversal_absolute_unc_and_device_paths_are_rejected(string raw, string? strip)
    {
        VolumePath.TryParse(raw, strip, out var segments, out var error).Should().BeFalse();
        segments.Should().BeEmpty();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Fuzzed_paths_never_parse_to_anything_but_plain_relative_segments()
    {
        string[] pieces =
        [
            "..", ".", "", "...", "a", "NATIVES", "x.pdf", "C:", "c:", "\\\\", "//", "?", "*", "\0", "\u202e", "名前", " ", "..\u2215..",
            "%2e%2e", "a:b", "~", "$", "con", "aux.txt", ". .", "..;", "\\\\?\\", "file:", "%",
        ];
        string[] separators = ["\\", "/", "\\\\", "//", "/./", "\\..\\", ""];
        var random = new Random(78);
        for (var i = 0; i < 20_000; i++)
        {
            var parts = Enumerable.Range(0, random.Next(1, 7))
                .Select(_ => pieces[random.Next(pieces.Length)] + separators[random.Next(separators.Length)]);
            var raw = string.Concat(parts);
            var strip = random.Next(4) == 0 ? pieces[random.Next(pieces.Length)] : null;
            if (!VolumePath.TryParse(raw, strip, out var segments, out _))
            {
                continue;
            }

            segments.Should().NotBeEmpty(raw);
            foreach (var segment in segments)
            {
                segment.Should().NotBeNullOrEmpty(raw);
                segment.Trim('.').Should().NotBeEmpty(raw);
                segment.Should().NotContainAny(["/", "\\", ":", "*", "?", "\0"], raw);
                segment.Any(char.IsControl).Should().BeFalse(raw);
            }

            Path.IsPathRooted(VolumePath.Display(segments)).Should().BeFalse(raw);
        }
    }
}
