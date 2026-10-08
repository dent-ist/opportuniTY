using AwesomeAssertions;

using Opportunity.Api.Content;
using Opportunity.Application.Content;

namespace Opportunity.IntegrationTests.Content;

/// <summary>
/// E11-T03: the gateway shows only derived renditions inline, as image types a browser displays (or plain text); a
/// native, or anything unexpected stored under a rendition, is an <c>application/octet-stream</c> attachment, so HTML,
/// SVG or script never reaches the browser as an active type from the app origin (and every content response carries
/// <c>nosniff</c> and the sandbox CSP).
/// </summary>
public sealed class RenditionDeliveryTypeTests
{
    [Theory]
    [InlineData(ContentRendition.Native, "text/html", "application/octet-stream", true)]
    [InlineData(ContentRendition.Native, "application/pdf", "application/octet-stream", true)]
    [InlineData(ContentRendition.Native, "image/svg+xml", "application/octet-stream", true)]
    [InlineData(ContentRendition.PageImage, "image/png", "image/png", false)]
    [InlineData(ContentRendition.PageImage, "IMAGE/JPEG", "image/jpeg", false)]
    [InlineData(ContentRendition.Thumbnail, "image/webp", "image/webp", false)]
    [InlineData(ContentRendition.PageImage, "image/tiff", "application/octet-stream", true)]
    [InlineData(ContentRendition.PageImage, "image/svg+xml", "application/octet-stream", true)]
    [InlineData(ContentRendition.Thumbnail, "text/html; charset=utf-8", "application/octet-stream", true)]
    [InlineData(ContentRendition.Text, "text/plain; charset=utf-8", "text/plain; charset=utf-8", false)]
    [InlineData(ContentRendition.Text, "text/html", "application/octet-stream", true)]
    public void Only_displayable_derived_renditions_are_served_inline(ContentRendition rendition, string stored, string served, bool attachment)
    {
        ProtectedContentGateway.DeliveredType(rendition, stored).Should().Be((served, attachment));
    }
}
