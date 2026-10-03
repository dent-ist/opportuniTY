namespace Opportunity.Core.Pages;

/// <summary>ADR-012 §1.1. Stored as smallint.</summary>
public enum PageSetSource : short
{
    Imported = 1,
    Rendered = 2,
}

/// <summary>ADR-012 §1.1. Stored as smallint.</summary>
public enum PageSetStatus : short
{
    Pending = 0,
    Ready = 1,
    Incomplete = 2,
    Failed = 3,
}

/// <summary>ADR-012 §1.3. Stored as smallint.</summary>
public enum PageColorMode : short
{
    Bitonal = 1,
    Gray = 2,
    Color = 3,
}

/// <summary>ADR-012 §1.4. Stored as smallint.</summary>
public enum PageImagePurpose : short
{
    Original = 1,
    Review = 2,
    Production = 3,
    Thumbnail = 4,
}

/// <summary>ADR-012 §1.4. Stored as smallint.</summary>
public enum PageImageFormat : short
{
    TiffG4 = 1,
    Jpeg = 2,
    Png = 3,
    WebP = 4,
}
