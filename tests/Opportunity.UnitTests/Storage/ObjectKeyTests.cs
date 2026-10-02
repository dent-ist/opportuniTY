using System.Text;

using AwesomeAssertions;

using Opportunity.Application.Storage;

namespace Opportunity.UnitTests.Storage;

/// <summary>ADR-011 §1 key grammar and layouts.</summary>
public class ObjectKeyTests
{
    private static readonly Guid Ws = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly Guid Doc = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
    private static readonly Sha256Digest Sha = Sha256Digest.Compute("x"u8);

    public static TheoryData<string, ObjectArea> ValidKeys => new()
    {
        { $"ws/{Ws:N}/docs/{Doc:N}/native/{Sha}", ObjectArea.Native },
        { $"ws/{Ws:N}/docs/{Doc:N}/text/{Sha}", ObjectArea.Text },
        { $"ws/{Ws:N}/docs/{Doc:N}/image/{Sha}", ObjectArea.Image },
        { $"ws/{Ws:N}/docs/{Doc:N}/rend/{Doc:N}/p000001.thumb.webp", ObjectArea.Rendition },
        { $"ws/{Ws:N}/imports/{Doc:N}/source/{Sha}", ObjectArea.ImportSource },
        { $"ws/{Ws:N}/imports/{Doc:N}/upload/{Doc:N}", ObjectArea.ImportUpload },
        { $"ws/{Ws:N}/snapshots/{Doc:N}/000042.bin", ObjectArea.SnapshotManifest },
        { $"ws/{Ws:N}/exports/{Doc:N}/{Doc:N}/vol001/natives/abc0000001.pdf", ObjectArea.Export },
        { $"ws/{Ws:N}/productions/{Doc:N}/v2/{Doc:N}/vol001/images/img001/abc0000001.tif", ObjectArea.Production },
        { $"ws/{Ws:N}/reports/{Doc:N}/{Sha}", ObjectArea.Report },
        { $"ws/{Ws:N}/tmp/{Doc:N}/a/b.bin", ObjectArea.Tmp },
        { $"sys/certificates/{Doc:N}/{Sha}", ObjectArea.Certificate },
        { "sys/audit/2026/10/segment-000001.jsonl", ObjectArea.Audit },
    };

    [Theory]
    [MemberData(nameof(ValidKeys))]
    public void Valid_layouts_parse_with_their_area(string value, ObjectArea area)
    {
        var key = ObjectKey.Parse(value);

        key.Value.Should().Be(value);
        key.Area.Should().Be(area);
        key.WorkspaceId.Should().Be(value.StartsWith("ws/", StringComparison.Ordinal) ? Ws : null);
    }

    public static TheoryData<string> InvalidKeys => new()
    {
        "",
        "/",
        $"ws/{Ws:N}/docs/{Doc:N}/native/{Sha}/",
        $"/ws/{Ws:N}/docs/{Doc:N}/native/{Sha}",
        $"ws/{Ws:N}//docs/{Doc:N}/native/{Sha}",
        $"ws/{Ws:N}/docs/{Doc:N}/../{Doc:N}/native/{Sha}",
        $"ws/{Ws:N}/docs/{Doc:N}/./native/{Sha}",
        $"ws/{Ws:N}/tmp/{Doc:N}/..",
        $"ws/{Ws:N}/tmp/{Doc:N}/..\\..\\etc\\passwd",
        $"ws/{Ws:N}/tmp/{Doc:N}/Upper.bin",
        $"ws/{Ws:N}/tmp/{Doc:N}/with space.bin",
        $"ws/{Ws:N}/tmp/{Doc:N}/café.bin",
        $"ws/{Ws:N}/tmp/{Doc:N}/a%2f..",
        $"ws/{Ws:D}/tmp/{Doc:N}/a",
        $"ws/{Ws.ToString("N").ToUpperInvariant()}/tmp/{Doc:N}/a",
        $"ws/{Guid.Empty:N}x/tmp/{Doc:N}/a",
        $"ws/{Ws:N}/docs/{Doc:N}/native/not-a-sha",
        $"ws/{Ws:N}/docs/{Doc:N}/native/{Sha.Hex.ToUpperInvariant()}",
        $"ws/{Ws:N}/docs/{Doc:N}/thumbnails/{Sha}",
        $"ws/{Ws:N}/docs/{Doc:N}/rend/not-an-id/p000001.png",
        $"ws/{Ws:N}/imports/{Doc:N}/upload/custodian-smith.zip",
        $"ws/{Ws:N}/snapshots/{Doc:N}/42.bin",
        $"ws/{Ws:N}/productions/{Doc:N}/v0/{Doc:N}/a",
        $"ws/{Ws:N}/productions/{Doc:N}/2/{Doc:N}/a",
        $"ws/{Ws:N}/exports/{Doc:N}/{Doc:N}",
        $"ws/{Ws:N}/billing/{Doc:N}",
        $"ws/{Ws:N}",
        "matters/acme-v-globex/native.pdf",
        "sys/backups/x",
        $"sys/certificates/{Doc:N}/not-a-sha",
        "ws/" + Ws.ToString("N") + "/tmp/" + Doc.ToString("N") + "/" + new string('a', 512),
    };

    [Theory]
    [MemberData(nameof(InvalidKeys))]
    public void Invalid_keys_are_rejected(string value)
    {
        ObjectKey.TryParse(value, out _).Should().BeFalse();
        var parse = () => ObjectKey.Parse(value);
        parse.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Keys_are_at_most_512_bytes()
    {
        var head = $"ws/{Ws:N}/tmp/{Doc:N}/";
        ObjectKey.TryParse(head + new string('a', 512 - head.Length), out _).Should().BeTrue();
        ObjectKey.TryParse(head + new string('a', 513 - head.Length), out _).Should().BeFalse();
    }

    [Fact]
    public void Random_strings_never_parse_unless_grammatical()
    {
        // Property-style check: arbitrary user-like strings (file names, control numbers, OPT paths) are rejected,
        // and anything accepted is plain grammar with no traversal segment.
        var random = new Random(20261002);
        const string alphabet = "abcXYZ019 ._-/\\:%é\u0000‮";
        for (var i = 0; i < 20_000; i++)
        {
            var suffix = new StringBuilder();
            for (var j = random.Next(1, 24); j > 0; j--)
            {
                suffix.Append(alphabet[random.Next(alphabet.Length)]);
            }

            var candidate = $"ws/{Ws:N}/tmp/{Doc:N}/{suffix}";
            if (ObjectKey.TryParse(candidate, out var key))
            {
                key.Value.Split('/').Should().NotContain(s => s.Length == 0 || s == "." || s == "..");
                key.Value.Should().MatchRegex("^[a-z0-9._/-]+$");
            }
        }
    }

    [Fact]
    public void Factories_build_the_adr_011_layouts()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        ObjectKeys.Native(Ws, Doc, Sha).Value.Should().Be($"ws/{Ws:N}/docs/{Doc:N}/native/{Sha}");
        ObjectKeys.Rendition(Ws, Doc, id, "p000001.png").Value.Should().Be($"ws/{Ws:N}/docs/{Doc:N}/rend/{id:N}/p000001.png");
        ObjectKeys.SnapshotManifest(Ws, id, 7).Value.Should().Be($"ws/{Ws:N}/snapshots/{id:N}/000007.bin");
        ObjectKeys.ProductionFile(Ws, id, 3, Doc, "VOL001/IMAGES/IMG001/ABC0000001.tif").Value
            .Should().Be($"ws/{Ws:N}/productions/{id:N}/v3/{Doc:N}/vol001/images/img001/abc0000001.tif");
        ObjectKeys.ImportUpload(Ws, id, Doc).Area.Should().Be(ObjectArea.ImportUpload);
        ObjectKeys.DestructionCertificate(id, Sha).WorkspaceId.Should().BeNull();
    }

    [Fact]
    public void Factories_reject_user_strings_and_empty_ids()
    {
        var traversal = () => ObjectKeys.ExportFile(Ws, Doc, Doc, "VOL001/../../../etc/passwd");
        var fileName = () => ObjectKeys.Rendition(Ws, Doc, Doc, "Smith deposition.pdf");
        var emptyWorkspace = () => ObjectKeys.Native(Guid.Empty, Doc, Sha);

        traversal.Should().Throw<ArgumentException>();
        fileName.Should().Throw<ArgumentException>();
        emptyWorkspace.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Content_addressed_keys_expose_their_digest_and_originals_are_flagged()
    {
        var native = ObjectKeys.Native(Ws, Doc, Sha);
        var rendition = ObjectKeys.Rendition(Ws, Doc, Doc, "doc.pdf");

        native.ContentSha256.Should().Be(Sha);
        native.DocumentId.Should().Be(Doc);
        native.IsOriginal.Should().BeTrue();
        rendition.IsContentAddressed.Should().BeFalse();
        rendition.IsOriginal.Should().BeFalse();
    }

    [Theory]
    [InlineData("ws/{ws}/", true)]
    [InlineData("ws/{ws}/docs/", true)]
    [InlineData("ws/{ws}/tmp/", true)]
    [InlineData("ws/{ws}/docs/{id}/", true)]
    [InlineData("ws/{ws}/tmp/{id}/", true)]
    [InlineData("ws/{ws}/docs/{id}/native/", false)]
    [InlineData("ws/{ws}/imports/{id}/", false)]
    [InlineData("ws/{ws}/docs/{id}/rend/{id}/", false)]
    [InlineData("sys/certificates/", false)]
    public void Only_adr_011_section_7_prefixes_are_deletable(string template, bool deletable)
    {
        var prefix = ObjectPrefix.Parse(template.Replace("{ws}", Ws.ToString("N"), StringComparison.Ordinal).Replace("{id}", Doc.ToString("N"), StringComparison.Ordinal));

        prefix.IsDeletable.Should().Be(deletable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ws/")]
    [InlineData("ws/{ws}")]
    [InlineData("ws/not-an-id/")]
    [InlineData("ws/{ws}/billing/")]
    [InlineData("ws/{ws}/../")]
    [InlineData("sys/")]
    [InlineData("sys/backups/")]
    [InlineData("other/")]
    public void Invalid_prefixes_are_rejected(string template)
    {
        ObjectPrefix.TryParse(template.Replace("{ws}", Ws.ToString("N"), StringComparison.Ordinal), out _).Should().BeFalse();
    }

    [Fact]
    public void Keys_know_whether_they_are_under_a_prefix()
    {
        var key = ObjectKeys.Native(Ws, Doc, Sha);

        key.IsUnder(ObjectPrefix.Workspace(Ws)).Should().BeTrue();
        key.IsUnder(ObjectPrefix.Document(Ws, Doc)).Should().BeTrue();
        key.IsUnder(ObjectPrefix.Workspace(Doc)).Should().BeFalse();
    }
}
