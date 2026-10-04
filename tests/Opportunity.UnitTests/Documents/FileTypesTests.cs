using AwesomeAssertions;

using Opportunity.Core.Documents;

namespace Opportunity.UnitTests.Documents;

public sealed class FileTypesTests
{
    [Theory]
    [InlineData("msg", null, null, "Email")]
    [InlineData(".MSG", null, null, "Email")]
    [InlineData("xlsx", null, null, "Spreadsheet")]
    [InlineData("html", null, null, "Web Page")]
    [InlineData("pdf", null, null, "PDF")]
    [InlineData(null, "application/pdf", null, "PDF")]
    [InlineData(null, "text/html; charset=utf-8", null, "Web Page")]
    [InlineData(null, "image/x-unknown", null, "Image")]
    [InlineData("txt", "message/rfc822", null, "Email")]
    [InlineData(null, null, "Board minutes.docx", "Word Processing")]
    [InlineData("", "", "", null)]
    [InlineData("xyz", "application/x-unknown", null, null)]
    [InlineData(null, null, "README", null)]
    public void Describe_prefers_the_mime_type_then_the_extension(string? extension, string? mime, string? fileName, string? expected)
    {
        FileTypes.Describe(extension, mime, fileName).Should().Be(expected);
    }
}
