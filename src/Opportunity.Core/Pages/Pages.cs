namespace Opportunity.Core.Pages;

/// <summary>An ordered set of pages for one document, imported from OPT or rendered (ADR-012 §1.1).</summary>
public sealed class PageSet
{
    public Guid WorkspaceId { get; set; }

    public Guid PageSetId { get; set; }

    public Guid DocumentId { get; set; }

    public PageSetSource Source { get; set; }

    public Guid? ImportJobId { get; set; }

    public string? RendererName { get; set; }

    public string? RendererVersion { get; set; }

    public byte[]? RenderSettingsHash { get; set; }

    public int PageCount { get; set; }

    public PageSetStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A logical page: 1-based ordinal and geometry at rotation 0 (ADR-012 §1.3).</summary>
public sealed class Page
{
    public Guid WorkspaceId { get; set; }

    public Guid PageSetId { get; set; }

    public int Ordinal { get; set; }

    public Guid DocumentId { get; set; }

    /// <summary>OPT image key (received page-level Bates); null for rendered sets.</summary>
    public string? ImageKey { get; set; }

    /// <summary>Width in 1/72-inch points at rotation 0.</summary>
    public decimal WidthPt { get; set; }

    public decimal HeightPt { get; set; }

    /// <summary>0, 90, 180 or 270 degrees clockwise, applied for upright display.</summary>
    public short Rotation { get; set; }

    public PageColorMode ColorMode { get; set; }

    /// <summary>Frame index inside a multi-page TIFF, else 0.</summary>
    public int SourceFrame { get; set; }

    public bool ImageMissing { get; set; }
}

/// <summary>One raster of a page (ADR-012 §1.4), stored as a registered object (ADR-011).</summary>
public sealed class PageImage
{
    public Guid WorkspaceId { get; set; }

    public Guid PageSetId { get; set; }

    public int Ordinal { get; set; }

    public PageImagePurpose Purpose { get; set; }

    public Guid ObjectId { get; set; }

    public int WidthPx { get; set; }

    public int HeightPx { get; set; }

    public int DpiX { get; set; }

    public int DpiY { get; set; }

    public PageImageFormat Format { get; set; }
}
