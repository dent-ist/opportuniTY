namespace Opportunity.Core.Documents;

/// <summary>
/// The derived <c>FileType</c> system field (ADR-003 §1): a short, vendor-neutral category of the document's format,
/// used when the load file supplies no file type of its own. Derived from the MIME type when it is recognised, else
/// from the file extension (or the file name's extension). Unknown formats stay blank rather than guessed.
/// </summary>
public static class FileTypes
{
    public const string Email = "Email";
    public const string WordProcessing = "Word Processing";
    public const string Spreadsheet = "Spreadsheet";
    public const string Presentation = "Presentation";
    public const string Pdf = "PDF";
    public const string WebPage = "Web Page";
    public const string Text = "Text";
    public const string Image = "Image";
    public const string Audio = "Audio";
    public const string Video = "Video";
    public const string Archive = "Archive";
    public const string Calendar = "Calendar";
    public const string Contact = "Contact";
    public const string Database = "Database";

    private static readonly Dictionary<string, string> ByExtension = Build(
        (Email, ["msg", "eml", "emlx", "mbox", "pst", "ost", "nsf", "mht", "mhtml", "oft"]),
        (WordProcessing, ["doc", "docx", "docm", "dot", "dotx", "dotm", "odt", "ott", "rtf", "wpd", "wps", "pages", "hwp"]),
        (Spreadsheet, ["xls", "xlsx", "xlsm", "xlsb", "xlt", "xltx", "xltm", "ods", "ots", "csv", "tsv", "numbers"]),
        (Presentation, ["ppt", "pptx", "pptm", "pps", "ppsx", "pot", "potx", "odp", "otp", "key"]),
        (Pdf, ["pdf"]),
        (WebPage, ["htm", "html", "xhtml", "shtml"]),
        (Text, ["txt", "text", "log", "md", "xml", "json", "yaml", "yml", "ini", "cfg"]),
        (Image, ["jpg", "jpeg", "jpe", "png", "gif", "bmp", "tif", "tiff", "webp", "heic", "heif", "svg", "ico", "emf", "wmf"]),
        (Audio, ["mp3", "wav", "wma", "m4a", "aac", "flac", "ogg", "oga", "amr", "aiff", "aif"]),
        (Video, ["mp4", "m4v", "mov", "avi", "wmv", "mkv", "webm", "mpg", "mpeg", "3gp", "flv"]),
        (Archive, ["zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "xz", "cab"]),
        (Calendar, ["ics", "vcs"]),
        (Contact, ["vcf", "vcard"]),
        (Database, ["mdb", "accdb", "db", "sqlite", "dbf"]));

    private static readonly Dictionary<string, string> ByMimeType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["message/rfc822"] = Email,
        ["application/vnd.ms-outlook"] = Email,
        ["application/msword"] = WordProcessing,
        ["application/rtf"] = WordProcessing,
        ["text/rtf"] = WordProcessing,
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = WordProcessing,
        ["application/vnd.oasis.opendocument.text"] = WordProcessing,
        ["application/vnd.ms-excel"] = Spreadsheet,
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = Spreadsheet,
        ["application/vnd.oasis.opendocument.spreadsheet"] = Spreadsheet,
        ["text/csv"] = Spreadsheet,
        ["application/vnd.ms-powerpoint"] = Presentation,
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = Presentation,
        ["application/vnd.oasis.opendocument.presentation"] = Presentation,
        ["application/pdf"] = Pdf,
        ["text/html"] = WebPage,
        ["application/xhtml+xml"] = WebPage,
        ["text/plain"] = Text,
        ["text/calendar"] = Calendar,
        ["text/vcard"] = Contact,
        ["text/x-vcard"] = Contact,
        ["application/zip"] = Archive,
        ["application/x-7z-compressed"] = Archive,
        ["application/vnd.rar"] = Archive,
        ["application/x-rar-compressed"] = Archive,
        ["application/x-tar"] = Archive,
        ["application/gzip"] = Archive,
    };

    /// <summary>The category of a document, or null when neither the MIME type nor the extension is recognised.</summary>
    /// <param name="extension">File extension with or without the leading dot.</param>
    /// <param name="mimeType">MIME type, parameters allowed (<c>text/html; charset=utf-8</c>).</param>
    /// <param name="fileName">Used for its extension when <paramref name="extension"/> is blank.</param>
    public static string? Describe(string? extension, string? mimeType, string? fileName = null)
    {
        if (FromMimeType(mimeType) is { } fromMime)
        {
            return fromMime;
        }

        var ext = Clean(extension);
        if (ext.Length == 0 && !string.IsNullOrWhiteSpace(fileName))
        {
            var dot = fileName.LastIndexOf('.');
            ext = dot >= 0 && dot < fileName.Length - 1 ? Clean(fileName[(dot + 1)..]) : string.Empty;
        }

        return ext.Length > 0 && ByExtension.TryGetValue(ext, out var type) ? type : null;
    }

    private static string? FromMimeType(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return null;
        }

        var semicolon = mimeType.IndexOf(';', StringComparison.Ordinal);
        var bare = (semicolon >= 0 ? mimeType[..semicolon] : mimeType).Trim();
        if (ByMimeType.TryGetValue(bare, out var type))
        {
            return type;
        }

        return bare.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? Image
            : bare.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? Audio
            : bare.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? Video
            : null;
    }

    private static string Clean(string? extension) => (extension ?? string.Empty).Trim().TrimStart('.');

    private static Dictionary<string, string> Build(params (string Type, string[] Extensions)[] groups)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (type, extensions) in groups)
        {
            foreach (var extension in extensions)
            {
                map.Add(extension, type);
            }
        }

        return map;
    }
}
