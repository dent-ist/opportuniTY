using AwesomeAssertions;

using Opportunity.Application.Storage;

namespace Opportunity.UnitTests.Storage;

public class PresignPolicyTests
{
    private static readonly Guid Ws = Guid.NewGuid();
    private static readonly ObjectKey Native = ObjectKeys.Native(Ws, Guid.NewGuid(), Sha256Digest.Compute("n"u8));
    private static readonly ObjectKey Upload = ObjectKeys.ImportUpload(Ws, Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void Get_defaults_to_60_seconds_and_allows_30_to_300()
    {
        var policy = PresignPolicy.Default;

        policy.ResolveGetTtl(Native, null).Should().Be(TimeSpan.FromSeconds(60));
        policy.ResolveGetTtl(Native, TimeSpan.FromSeconds(30)).Should().Be(TimeSpan.FromSeconds(30));
        policy.ResolveGetTtl(Native, TimeSpan.FromSeconds(300)).Should().Be(TimeSpan.FromSeconds(300));
        policy.Invoking(p => p.ResolveGetTtl(Native, TimeSpan.FromSeconds(29))).Should().Throw<ArgumentOutOfRangeException>();
        policy.Invoking(p => p.ResolveGetTtl(Native, TimeSpan.FromSeconds(301))).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Installation_settings_cannot_exceed_the_hard_bounds()
    {
        var tooLong = () => new PresignPolicy(maxGetTtl: TimeSpan.FromMinutes(10));
        var defaultAboveMax = () => new PresignPolicy(defaultGetTtl: TimeSpan.FromSeconds(120), maxGetTtl: TimeSpan.FromSeconds(60));
        var putTooLong = () => new PresignPolicy(defaultPutTtl: TimeSpan.FromSeconds(901));

        tooLong.Should().Throw<ArgumentOutOfRangeException>();
        defaultAboveMax.Should().Throw<ArgumentOutOfRangeException>();
        putTooLong.Should().Throw<ArgumentOutOfRangeException>();
        new PresignPolicy(maxGetTtl: TimeSpan.FromSeconds(120)).Invoking(p => p.ResolveGetTtl(Native, TimeSpan.FromSeconds(121)))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Get_is_limited_to_natives_and_export_or_production_packages()
    {
        var policy = PresignPolicy.Default;

        PresignPolicy.MayPresignGet(ObjectKeys.ExportFile(Ws, Guid.NewGuid(), Guid.NewGuid(), "vol001/a.pdf")).Should().BeTrue();
        PresignPolicy.MayPresignGet(ObjectKeys.ProductionFile(Ws, Guid.NewGuid(), 1, Guid.NewGuid(), "vol001/a.tif")).Should().BeTrue();
        policy.Invoking(p => p.ResolveGetTtl(ObjectKeys.Rendition(Ws, Guid.NewGuid(), Guid.NewGuid(), "p000001.png"), null))
            .Should().Throw<ArgumentException>();
        policy.Invoking(p => p.ResolveGetTtl(ObjectKeys.Text(Ws, Guid.NewGuid(), Sha256Digest.Compute("t"u8)), null))
            .Should().Throw<ArgumentException>();
        policy.Invoking(p => p.ResolveGetTtl(Upload, null)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Put_is_limited_to_upload_staging_and_900_seconds()
    {
        var policy = PresignPolicy.Default;

        policy.ResolvePutTtl(Upload, TimeSpan.FromSeconds(900)).Should().Be(TimeSpan.FromSeconds(900));
        policy.Invoking(p => p.ResolvePutTtl(Upload, TimeSpan.FromSeconds(901))).Should().Throw<ArgumentOutOfRangeException>();
        policy.Invoking(p => p.ResolvePutTtl(Native, null)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Presigned_urls_are_redacted_when_formatted()
    {
        var url = new PresignedObjectUrl(new Uri("https://store/b/k?X-Amz-Signature=secret"), "GET", DateTimeOffset.UnixEpoch);

        url.ToString().Should().NotContain("secret").And.NotContain("https://");
    }

    [Theory]
    [InlineData("ABC0000001.pdf", "attachment; filename=\"ABC0000001.pdf\"; filename*=UTF-8''ABC0000001.pdf")]
    [InlineData("../../etc/passwd", "attachment; filename=\"_.._etc_passwd\"; filename*=UTF-8''_.._etc_passwd")]
    [InlineData("a\"b\r\nc.txt", "attachment; filename=\"a_b__c.txt\"; filename*=UTF-8''a_b__c.txt")]
    [InlineData("café résumé.docx", "attachment; filename=\"caf_ r_sum_.docx\"; filename*=UTF-8''caf%C3%A9%20r%C3%A9sum%C3%A9.docx")]
    [InlineData("evil‮gpj.exe", "attachment; filename=\"evil_gpj.exe\"; filename*=UTF-8''evil_gpj.exe")]
    [InlineData("  ", "attachment; filename=\"download\"; filename*=UTF-8''download")]
    public void Content_disposition_is_always_a_sanitized_attachment(string fileName, string expected)
    {
        ContentDispositionHeader.Attachment(fileName).Should().Be(expected);
    }

    [Fact]
    public void Sha256_digests_are_lowercase_hex()
    {
        var digest = Sha256Digest.Compute("abc"u8);

        digest.Hex.Should().Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        Sha256Digest.TryParse(digest.Hex.ToUpperInvariant(), out _).Should().BeFalse();
        Sha256Digest.Parse(digest.Hex).Should().Be(digest);
    }
}
