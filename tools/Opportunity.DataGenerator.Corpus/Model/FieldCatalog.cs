using System.Globalization;

using Opportunity.DataGenerator.Corpus.Profiles;

namespace Opportunity.DataGenerator.Corpus.Model;

public enum FieldType
{
    Text,
    LongText,
    MultiValue,
    DateTime,
    Date,
    WholeNumber,
    Currency,
    Boolean,
    SingleChoice,
    MultiChoice,
}

public sealed record FieldDefinition(int Ordinal, string Name, FieldType Type);

/// <summary>
/// The metadata fields every document carries (by ordinal). Values are stored in an object array indexed by
/// ordinal: string, string[] (multi-value/choice), DateTimeOffset, DateOnly, long, decimal or bool; null = absent.
/// </summary>
public sealed class FieldCatalog
{
    public const int From = 0;
    public const int To = 1;
    public const int Cc = 2;
    public const int Bcc = 3;
    public const int Subject = 4;
    public const int DateSent = 5;
    public const int DateReceived = 6;
    public const int TimeZone = 7;
    public const int MessageId = 8;
    public const int InReplyTo = 9;
    public const int ConversationIndex = 10;
    public const int InclusiveEmail = 11;
    public const int Importance = 12;
    public const int FileName = 13;
    public const int FileExtension = 14;
    public const int FilePath = 15;
    public const int FileSize = 16;
    public const int Author = 17;
    public const int Title = 18;
    public const int DateCreated = 19;
    public const int DateLastModified = 20;
    public const int PageCount = 21;
    public const int Language = 22;
    public const int HasHiddenContent = 23;
    public const int Confidentiality = 24;
    public const int Keywords = 25;
    public const int Comments = 26;
    public const int ProjectCode = 27;
    public const int Amount = 28;
    public const int RecordDate = 29;
    public const int SourceSystem = 30;
    public const int StandardCount = 31;

    private static readonly FieldDefinition[] Standard =
    [
        new(From, "From", FieldType.Text),
        new(To, "To", FieldType.MultiValue),
        new(Cc, "CC", FieldType.MultiValue),
        new(Bcc, "BCC", FieldType.MultiValue),
        new(Subject, "Subject", FieldType.Text),
        new(DateSent, "DateSent", FieldType.DateTime),
        new(DateReceived, "DateReceived", FieldType.DateTime),
        new(TimeZone, "TimeZone", FieldType.SingleChoice),
        new(MessageId, "MessageId", FieldType.Text),
        new(InReplyTo, "InReplyTo", FieldType.Text),
        new(ConversationIndex, "ConversationIndex", FieldType.Text),
        new(InclusiveEmail, "InclusiveEmail", FieldType.Boolean),
        new(Importance, "Importance", FieldType.SingleChoice),
        new(FileName, "FileName", FieldType.Text),
        new(FileExtension, "FileExtension", FieldType.SingleChoice),
        new(FilePath, "FilePath", FieldType.Text),
        new(FileSize, "FileSize", FieldType.WholeNumber),
        new(Author, "Author", FieldType.Text),
        new(Title, "Title", FieldType.Text),
        new(DateCreated, "DateCreated", FieldType.DateTime),
        new(DateLastModified, "DateLastModified", FieldType.DateTime),
        new(PageCount, "PageCount", FieldType.WholeNumber),
        new(Language, "Language", FieldType.SingleChoice),
        new(HasHiddenContent, "HasHiddenContent", FieldType.Boolean),
        new(Confidentiality, "Confidentiality", FieldType.SingleChoice),
        new(Keywords, "Keywords", FieldType.MultiChoice),
        new(Comments, "Comments", FieldType.LongText),
        new(ProjectCode, "ProjectCode", FieldType.SingleChoice),
        new(Amount, "Amount", FieldType.Currency),
        new(RecordDate, "RecordDate", FieldType.Date),
        new(SourceSystem, "SourceSystem", FieldType.SingleChoice),
    ];

    private static readonly FieldType[] ExtraTypeCycle =
    [
        FieldType.Text, FieldType.WholeNumber, FieldType.Date, FieldType.SingleChoice,
        FieldType.Boolean, FieldType.Currency, FieldType.MultiChoice, FieldType.DateTime,
    ];

    public FieldCatalog(FieldProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var fields = new List<FieldDefinition>(Standard);
        for (int i = 0; i < profile.ExtraCustomFields; i++)
        {
            string name = string.Create(CultureInfo.InvariantCulture, $"Custom{i + 1:D3}");
            fields.Add(new FieldDefinition(StandardCount + i, name, ExtraTypeCycle[i % ExtraTypeCycle.Length]));
        }

        Fields = fields;
    }

    public IReadOnlyList<FieldDefinition> Fields { get; }

    public int Count => Fields.Count;
}