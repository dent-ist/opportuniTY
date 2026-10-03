using AwesomeAssertions;

using Opportunity.Core.Fields;

namespace Opportunity.UnitTests.Fields;

/// <summary>ADR-008 R11: default query names of the field catalogue.</summary>
public sealed class FieldQueryNameTests
{
    private static readonly Guid Ws = Guid.NewGuid();

    [Fact]
    public void Structural_fields_use_the_adr_007_names()
    {
        var names = FieldQueryNames.Assign(SystemFields.Create(Ws));

        names[SystemFields.ControlNumber].Should().Be("controlnumber");
        names[SystemFields.DocumentDate].Should().Be("date");
        names[SystemFields.FileExtension].Should().Be("extension");
        names[SystemFields.BegAttach].Should().Be("beg_attach");
        names.Values.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Custom_names_are_generated_from_the_display_name_and_deduplicated()
    {
        var fields = SystemFields.Create(Ws).Concat(
        [
            Custom(1000, "Privilege Status"),
            Custom(1001, "privilege-status"),
            Custom(1002, "File Name"),
            Custom(1003, "2024 Notes"),
            Custom(1004, "Text"),
            Custom(1005, "Café / Résumé"),
            Custom(1006, "—"),
        ]);

        var names = FieldQueryNames.Assign(fields);

        names[1000].Should().Be("privilege_status");
        names[1001].Should().Be("privilege_status_2");
        names[1002].Should().Be("file_name");
        names[1003].Should().Be("f_2024_notes");
        names[1004].Should().Be("text_2", "text is the default field");
        names[1005].Should().Be("café_résumé");
        names[1006].Should().Be("f1006");

        // The QL field-name rule: a letter, then letters, digits, '_', '.' or '-'.
        names.Values.Should().OnlyContain(n => char.IsLetter(n[0]) && n.All(c => char.IsLetterOrDigit(c) || c == '_'));
    }

    private static FieldDefinition Custom(int id, string name) =>
        new() { WorkspaceId = Ws, FieldId = id, Name = name, Type = FieldType.Keyword, Storage = FieldStorage.Metadata };
}
