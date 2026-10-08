using Opportunity.Application.Content;
using Opportunity.Contracts.Api;
using Opportunity.Core.Pages;

namespace Opportunity.Api.Content;

/// <summary>Maps the viewer's read models to the API contract (E11-T01).</summary>
internal static class DocumentResources
{
    public static DocumentResource ToResource(DocumentViewerRecord record, IReadOnlySet<int> restrictedFields)
    {
        var d = record.Document;
        var text = record.Text;
        var pageSet = record.ActivePageSet;
        return new DocumentResource(
            d.DocumentId,
            d.ControlNumber,
            record.DocumentVersion,
            record.DisplayTimeZone,
            d.FamilyId,
            d.ParentDocumentId,
            new DocumentTextInfo(
                Available: text is { Quarantined: false },
                Missing: d.TextMissing || text is null,
                d.TextTruncated,
                d.TextEncodingWarning,
                text?.SizeBytes,
                d.TextLength,
                TextChunks.ChunkBytes,
                text is { Quarantined: false } ? TextChunks.Count(text.SizeBytes) : 0),
            new DocumentNativeInfo(record.Native is not null, d.NativeMissing, record.Native?.SizeBytes, d.FileExtension),
            new DocumentImagesInfo(
                pageSet is not null,
                // The active set decides (a Ready Rendered set replaces an Incomplete Imported one, Q-68); ImagesIncomplete
                // stays the import's OPT finding and only speaks when there is no active set.
                pageSet is null ? d.ImagesIncomplete : pageSet.Status is not PageSetStatus.Ready,
                pageSet?.PageCount ?? 0,
                pageSet is null ? null : Enum.Parse<DocumentPageSetSource>(pageSet.Source.ToString()),
                pageSet is null ? null : Enum.Parse<DocumentPageSetStatus>(pageSet.Status.ToString())),
            [.. DocumentFieldDisplay.Build(record, restrictedFields).Select(ToResource)]);
    }

    public static DocumentPageResource ToResource(DocumentPageInfo page) => new(
        page.PageNumber,
        page.WidthPt,
        page.HeightPt,
        page.Rotation,
        Enum.Parse<DocumentPageColorMode>(page.ColorMode.ToString()),
        page.ImageMissing,
        page.ImageFormat is not null,
        page.ImageFormat switch
        {
            PageImageFormat.Png => "image/png",
            PageImageFormat.Jpeg => "image/jpeg",
            PageImageFormat.WebP => "image/webp",
            _ => null,
        },
        page.ImageWidthPx,
        page.ImageHeightPx,
        page.HasThumbnail);

    private static DocumentFieldValueResource ToResource(DocumentFieldEntry entry)
    {
        var f = entry.Field;
        return new DocumentFieldValueResource(
            f.FieldId,
            f.Name,
            Enum.Parse<FieldResourceType>(f.Type.ToString()),
            entry.TypeLabel,
            Enum.Parse<FieldResourceStorage>(f.Storage.ToString()),
            Enum.Parse<DocumentFieldFormat>(entry.Format.ToString()),
            f.IsMultiValue || f.Type == Core.Fields.FieldType.MultiChoice,
            f.IsSystem,
            f.IsHidden,
            f.DecimalScale,
            entry.Value,
            entry.DisplayValue,
            entry.RawValue,
            f.IsChoice ? [.. entry.Choices.Select(c => new FieldChoiceResource(c.ChoiceId, c.Name, c.IsActive, c.SystemKey))] : null);
    }
}
