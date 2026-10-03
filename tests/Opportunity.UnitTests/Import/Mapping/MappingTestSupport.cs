using System.Text;

using Opportunity.Contracts.Import;
using Opportunity.Core.Fields;
using Opportunity.Import.Mapping;

namespace Opportunity.UnitTests.Import.Mapping;

internal static class MappingTestSupport
{
    public const char Dc4 = '\u0014';
    public const char Thorn = 'þ';
    public const char Reg = '®';

    public static readonly Guid Workspace = Guid.Parse("0199a8a0-0000-7000-8000-00000000aaaa");

    public static FieldDefinition Custom(int id, string name, FieldType type, bool multi = false, FieldStorage storage = FieldStorage.Metadata,
        DatePrecision? precision = null, SecurityClass? security = null) => new()
        {
            WorkspaceId = Workspace,
            FieldId = id,
            Name = name,
            Type = type,
            Storage = storage,
            IsMultiValue = multi || type == FieldType.MultiChoice,
            DatePrecision = type == FieldType.Date ? precision ?? DatePrecision.DateTime : null,
            TextAnalysis = type == FieldType.Text ? TextAnalysis.Prose : null,
            IsSecurityAffecting = security.HasValue,
            SecurityClass = security,
            IsSearchable = true,
        };

    /// <summary>System fields plus the given custom fields and choices.</summary>
    public static FieldCatalog Catalog(params FieldDefinition[] custom) => Catalog(custom, []);

    public static FieldCatalog Catalog(IEnumerable<FieldDefinition> custom, IEnumerable<Choice> choices) =>
        new([.. SystemFields.Create(Workspace), .. custom], choices);

    /// <summary>A Concordance DAT (DC4 / þ / ®), every value qualified, CRLF rows.</summary>
    public static string Concordance(params string[][] rows) =>
        string.Concat(rows.Select(r => string.Join(Dc4, r.Select(v => Thorn + v.Replace("þ", "þþ", StringComparison.Ordinal) + Thorn)) + "\r\n"));

    public static MemoryStream Utf8(string text, bool bom = true) =>
        new(bom ? [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)] : Encoding.UTF8.GetBytes(text));

    public static Task<MappingPreviewResult> PreviewAsync(string dat, ImportProfileDefinition? profile, FieldCatalog catalog, MappingPreviewOptions? options = null) =>
        MappingPreviewer.PreviewAsync(Utf8(dat), profile, catalog, options, TestContext.Current.CancellationToken);

    public static ColumnPreview Column(this MappingPreviewResult result, string name) => result.Columns.Single(c => c.Column == name);

    public static CellPreview Cell(this RowPreview row, string column, string? target = null) =>
        row.Cells.Single(c => c.Column == column && (target is null || c.Target == target));

    public static MappingTarget FieldTarget(int id, string? name = null) => new() { Kind = MappingTargetKind.Field, FieldId = id, FieldName = name };

    public static MappingTarget Structural(StructuralTarget target) => new() { Kind = MappingTargetKind.Structural, Structural = target };

    public static MappingTarget NewField(string name, ImportFieldType type, bool multi = false) =>
        new() { Kind = MappingTargetKind.NewField, NewField = new NewFieldSpec { Name = name, Type = type, IsMultiValue = multi } };

    public static ColumnMapping Map(string column, params MappingTarget[] targets) => new() { Column = column, Targets = targets };

    public static ColumnMapping Map(string column, ColumnParsing parsing, params MappingTarget[] targets) =>
        new() { Column = column, Targets = targets, Parsing = parsing };
}
