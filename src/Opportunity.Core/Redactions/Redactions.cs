namespace Opportunity.Core.Redactions;

/// <summary>How a redaction is drawn at production (ADR-012 §3.1, ticket review E11-T04). Stored as smallint.</summary>
public enum RedactionType : short
{
    /// <summary>An opaque black box.</summary>
    Black = 1,

    /// <summary>A white box with a black border and the reason's label printed inside ("Redacted – Privileged").</summary>
    Labelled = 2,
}

/// <summary>What one revision row does to its redaction (ADR-012 §3.3). Stored as smallint.</summary>
public enum RedactionOperation : short
{
    Add = 1,
    Modify = 2,
    Remove = 3,
}

/// <summary>Reason categories (ADR-012 §3.2): the privilege log uses Privilege, privacy handling uses Privacy. Stored as smallint.</summary>
public enum RedactionReasonCategory : short
{
    Privilege = 1,
    Privacy = 2,
    Other = 3,
}

/// <summary>Who wrote a revision (ADR-012 §3.3; SystemRule and Model are reserved for E20-T04). Stored as smallint.</summary>
public enum RedactionActorType : short
{
    Human = 1,
    SystemRule = 2,
    Model = 3,
}

/// <summary>
/// A rectangle in normalized page space (ADR-012 §2): origin top-left of the page at rotation 0, x right, y down, in
/// millionths of the page width and height. Integers, so client, server and burner never drift apart.
/// </summary>
public readonly record struct NormalizedRect(int X, int Y, int W, int H)
{
    /// <summary>One page side in normalized units.</summary>
    public const int Scale = 1_000_000;

    public bool IsWithinPage => X >= 0 && Y >= 0 && W >= 1 && H >= 1 && (long)X + W <= Scale && (long)Y + H <= Scale;

    /// <summary>The full page (a full-page redaction is valid and common, ADR-012 §2.5).</summary>
    public static NormalizedRect FullPage { get; } = new(0, 0, Scale, Scale);

    /// <summary>
    /// The device pixels this rectangle covers on a raster of the page (ADR-012 §2.4): rounded outward (floor of the
    /// start, ceiling of the end), so a burned box is never smaller than the drawn box. Every raster of a page shares its
    /// aspect ratio (§1.4), so the same rectangle applies to the review image at any DPI and to the production image.
    /// </summary>
    public PixelRect ToPixels(int rasterWidthPx, int rasterHeightPx)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rasterWidthPx);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rasterHeightPx);
        var left = FloorDiv((long)X * rasterWidthPx);
        var top = FloorDiv((long)Y * rasterHeightPx);
        var right = CeilDiv(((long)X + W) * rasterWidthPx);
        var bottom = CeilDiv(((long)Y + H) * rasterHeightPx);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// The smallest normalized rectangle containing a pixel rectangle of a raster (the inverse of <see cref="ToPixels"/>,
    /// rounded outward as well), e.g. a box drawn on a review image.
    /// </summary>
    public static NormalizedRect FromPixels(PixelRect pixels, int rasterWidthPx, int rasterHeightPx)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rasterWidthPx);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rasterHeightPx);
        var x = (int)Math.Clamp((long)pixels.X * Scale / rasterWidthPx, 0, Scale - 1);
        var y = (int)Math.Clamp((long)pixels.Y * Scale / rasterHeightPx, 0, Scale - 1);
        var right = (int)Math.Clamp(((long)(pixels.X + pixels.Width) * Scale + rasterWidthPx - 1) / rasterWidthPx, x + 1, Scale);
        var bottom = (int)Math.Clamp(((long)(pixels.Y + pixels.Height) * Scale + rasterHeightPx - 1) / rasterHeightPx, y + 1, Scale);
        return new NormalizedRect(x, y, right - x, bottom - y);
    }

    private static int FloorDiv(long value) => (int)(value / Scale);

    private static int CeilDiv(long value) => (int)((value + Scale - 1) / Scale);
}

/// <summary>A rectangle in device pixels of one raster.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>Geometry rules of ADR-012 §2.5.</summary>
public static class RedactionGeometry
{
    /// <summary>Production resolution (Q-21: TIFF G4 300 DPI).</summary>
    public const int ProductionDpi = 300;

    /// <summary>Minimum burned size in device pixels at <see cref="ProductionDpi"/>.</summary>
    public const int MinimumDevicePixels = 2;

    /// <summary>
    /// The smallest width and height (normalized units) a redaction may have on a page of the given size in points:
    /// 2 × 2 device pixels at 300 DPI.
    /// </summary>
    public static (int Width, int Height) MinimumSize(decimal widthPt, decimal heightPt)
    {
        static int Min(decimal sidePt)
        {
            if (sidePt <= 0)
            {
                return 1;
            }

            var sidePx = sidePt / 72m * ProductionDpi;
            return (int)Math.Clamp(Math.Ceiling(MinimumDevicePixels * NormalizedRect.Scale / sidePx), 1, NormalizedRect.Scale);
        }

        return (Min(widthPt), Min(heightPt));
    }
}

/// <summary>A reason of the seeded picklist (ADR-012 §3.2).</summary>
public sealed record DefaultRedactionReason(string Code, string Name, RedactionReasonCategory Category, string BoxLabel, int SortOrder);

public static class RedactionDefaults
{
    /// <summary>The Redaction Set every workspace starts with.</summary>
    public const string SetName = "Default";

    /// <summary>
    /// ADR-012 §3.2: the reasons every workspace starts with, privilege first, then privacy (E20-T04 builds on these),
    /// then other. Administrators rename, relabel, reorder, deactivate or add to them.
    /// </summary>
    public static IReadOnlyList<DefaultRedactionReason> Reasons { get; } =
    [
        new("AttorneyClient", "Attorney-Client Privilege", RedactionReasonCategory.Privilege, "Redacted – Privileged", 10),
        new("WorkProduct", "Work Product", RedactionReasonCategory.Privilege, "Redacted – Work Product", 20),
        new("CommonInterest", "Common Interest", RedactionReasonCategory.Privilege, "Redacted – Privileged", 30),
        new("PII", "PII", RedactionReasonCategory.Privacy, "Redacted – PII", 40),
        new("PHI", "PHI", RedactionReasonCategory.Privacy, "Redacted – PHI", 50),
        new("PersonalDataGdpr", "Personal Data – GDPR", RedactionReasonCategory.Privacy, "Redacted – Personal Data", 60),
        new("TradeSecret", "Trade Secret", RedactionReasonCategory.Other, "Redacted – Trade Secret", 70),
        new("NonResponsive", "Non-Responsive", RedactionReasonCategory.Other, "Redacted – Non-Responsive", 80),
        new("Other", "Other", RedactionReasonCategory.Other, "Redacted", 90),
    ];
}
