using System.Globalization;

namespace Opportunity.DataGenerator.Corpus.Volumes;

/// <summary>Renders typed field values as DAT strings (before qualification/escaping).</summary>
public sealed class DatValueFormatter(VolumeOptions options)
{
    private readonly string _multiSeparator = options.Delimiters.MultiValue + " ";

    public string Format(object? value) => value switch
    {
        null => "",
        string s => s,
        string[] values => string.Join(_multiSeparator, values),
        DateTimeOffset dto => FormatDateTime(dto),
        DateOnly date => FormatDate(date),
        long l => l.ToString(CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "Y" : "N",
        _ => throw new InvalidOperationException("Unsupported field value type " + value.GetType().Name),
    };

    public string FormatDateTime(DateTimeOffset value) => options.DateFormat switch
    {
        DatDateFormat.Us => value.ToOffset(options.TimeZoneOffset).ToString("MM/dd/yyyy hh:mm:ss tt", CultureInfo.InvariantCulture),
        DatDateFormat.Eu => value.ToOffset(options.TimeZoneOffset).ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture),
        _ => value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
    };

    public string FormatDate(DateOnly value) => options.DateFormat switch
    {
        DatDateFormat.Us => value.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
        DatDateFormat.Eu => value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
        _ => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };
}
