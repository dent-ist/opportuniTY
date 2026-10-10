using System.IO.Compression;
using System.Text;

using AwesomeAssertions;

using Opportunity.Application.PrivilegeLogs;
using Opportunity.Contracts.Api;
using Opportunity.Core.Fields;
using Opportunity.Core.Productions;
using Opportunity.Core.Redactions;
using Opportunity.Production.PrivilegeLogs;

namespace Opportunity.UnitTests.Productions;

/// <summary>
/// E13-T03: which documents a privilege log lists (AC 1), templates normalized canonically with recorded exclusion rules
/// (AC 3), and log files that are pure functions of their frozen inputs (AC 2).
/// </summary>
public class PrivilegeLogRulesTests
{
    private static readonly FieldCatalog Catalog = new(
    [
        new FieldDefinition { FieldId = SystemFields.FileName, Name = "File Name", Type = FieldType.Text, Storage = FieldStorage.Column, IsSystem = true },
        new FieldDefinition { FieldId = SystemFields.FileType, Name = "File Type", Type = FieldType.Keyword, Storage = FieldStorage.Column, IsSystem = true },
        new FieldDefinition { FieldId = SystemFields.DocumentDate, Name = "Document Date", Type = FieldType.Date, Storage = FieldStorage.Column, IsSystem = true },
        new FieldDefinition { FieldId = PrivilegeFields.Status, Name = "Privilege Status", Type = FieldType.SingleChoice, Storage = FieldStorage.Coding, IsSystem = true },
        new FieldDefinition { FieldId = PrivilegeFields.Basis, Name = "Privilege Basis", Type = FieldType.MultiChoice, Storage = FieldStorage.Coding, IsSystem = true },
        new FieldDefinition { FieldId = PrivilegeFields.Description, Name = "Privilege Description", Type = FieldType.Text, Storage = FieldStorage.Coding, IsSystem = true },
        new FieldDefinition { FieldId = PrivilegeFields.AttorneysInvolved, Name = "Attorneys Involved", Type = FieldType.Keyword, Storage = FieldStorage.Coding, IsSystem = true },
        new FieldDefinition { FieldId = PrivilegeFields.LogCategory, Name = "Log Category", Type = FieldType.SingleChoice, Storage = FieldStorage.Coding, IsSystem = true },
        new FieldDefinition { FieldId = 1001, Name = "From", Type = FieldType.Text, Storage = FieldStorage.Metadata },
        new FieldDefinition { FieldId = 1002, Name = "Subject", Type = FieldType.Text, Storage = FieldStorage.Metadata },
        new FieldDefinition { FieldId = 1003, Name = "Internal Note", Type = FieldType.Text, Storage = FieldStorage.Coding },
    ],
    [
        new Choice { FieldId = PrivilegeFields.LogCategory, ChoiceId = 501, Name = "Outside counsel" },
    ]);

    private static readonly HashSet<int> None = [];

    private static PrivilegeLogCandidate Candidate(string? status, PrivilegeLogMember? member = null) =>
        new(Guid.CreateVersion7(), "DOC0001", Guid.CreateVersion7(), 0, status, member);

    private static PrivilegeLogMember Member(ProductionOutputKind output, params RedactionReasonCategory[] reasons) =>
        new(1, output, "ABC0000001", "ABC0000002", reasons.Length, [.. reasons.Select((c, i) => new PrivilegeLogRedactionReason("R" + i, "Reason " + i, c))]);

    [Fact]
    public void Withheld_and_privilege_redacted_documents_are_listed_and_documents_produced_in_full_never_are()
    {
        const string withhold = PrivilegeFields.Keys.Withhold;
        const string redact = PrivilegeFields.Keys.Redact;
        const string none = PrivilegeFields.Keys.NotPrivileged;
        var production = PrivilegeLogSource.Production;

        // Not produced: withheld when coded Withhold.
        PrivilegeLogRules.Classify(Candidate(withhold), production, false).Should().Be(PrivilegeLogEntryTreatment.Withheld);
        PrivilegeLogRules.Classify(Candidate(redact), production, false).Should().BeNull();
        PrivilegeLogRules.Classify(Candidate(null), production, false).Should().BeNull();

        // Produced as a placeholder: withheld only when coded Withhold.
        PrivilegeLogRules.Classify(Candidate(withhold, Member(ProductionOutputKind.Placeholder)), production, false).Should().Be(PrivilegeLogEntryTreatment.Withheld);
        PrivilegeLogRules.Classify(Candidate(none, Member(ProductionOutputKind.Placeholder)), production, false).Should().BeNull();

        // Produced in full: never, whatever the coding says now.
        foreach (var status in new[] { withhold, redact, none, null })
        {
            PrivilegeLogRules.Classify(Candidate(status, Member(ProductionOutputKind.Image)), production, true).Should().BeNull();
            PrivilegeLogRules.Classify(Candidate(status, Member(ProductionOutputKind.Native)), production, true).Should().BeNull();
        }

        // Produced with redactions: privilege reasons or a Redact call make it a privilege redaction.
        PrivilegeLogRules.Classify(Candidate(null, Member(ProductionOutputKind.Image, RedactionReasonCategory.Privilege)), production, false)
            .Should().Be(PrivilegeLogEntryTreatment.Redacted);
        PrivilegeLogRules.Classify(Candidate(redact, Member(ProductionOutputKind.Image, RedactionReasonCategory.Other)), production, false)
            .Should().Be(PrivilegeLogEntryTreatment.Redacted);
        PrivilegeLogRules.Classify(Candidate(null, Member(ProductionOutputKind.Image, RedactionReasonCategory.Privacy, RedactionReasonCategory.Privilege)), production, false)
            .Should().Be(PrivilegeLogEntryTreatment.Redacted);

        // Privacy only: left out unless configured; "Other" only: never.
        PrivilegeLogRules.Classify(Candidate(null, Member(ProductionOutputKind.Image, RedactionReasonCategory.Privacy)), production, false).Should().BeNull();
        PrivilegeLogRules.Classify(Candidate(null, Member(ProductionOutputKind.Image, RedactionReasonCategory.Privacy)), production, true)
            .Should().Be(PrivilegeLogEntryTreatment.RedactedPrivacy);
        PrivilegeLogRules.Classify(Candidate(null, Member(ProductionOutputKind.Image, RedactionReasonCategory.Other)), production, true).Should().BeNull();

        // A frozen-set log follows the privilege call.
        PrivilegeLogRules.Classify(Candidate(withhold), PrivilegeLogSource.Snapshot, false).Should().Be(PrivilegeLogEntryTreatment.Withheld);
        PrivilegeLogRules.Classify(Candidate(redact), PrivilegeLogSource.Snapshot, false).Should().Be(PrivilegeLogEntryTreatment.Redacted);
        PrivilegeLogRules.Classify(Candidate(none), PrivilegeLogSource.Snapshot, false).Should().BeNull();
    }

    [Fact]
    public void Priv_ids_family_ranges_and_cells_are_formatted_safely()
    {
        PrivilegeLogRules.PrivId("PRIV", 7, 4).Should().Be("PRIV0007");
        PrivilegeLogRules.PrivId("LOG-", 12345, 3).Should().Be("LOG-12345");
        PrivilegeLogRules.FamilyRange([("A1", "A2")]).Should().BeEmpty("a family of one has no range");
        PrivilegeLogRules.FamilyRange([("A1", "A2"), ("PRIV0001", "PRIV0001"), ("A3", "A4")]).Should().Be("A1 - A4");
        PrivilegeLogRules.Clean("a\u0000b\u001fc\td\ne").Should().Be("abc\td\ne");
    }

    [Fact]
    public void Templates_are_normalized_canonically_with_presets_resolved_in_the_workspace()
    {
        var (a, errorsA) = PrivilegeLogTemplateRules.Normalize(null, Catalog, None);
        var (b, _) = PrivilegeLogTemplateRules.Normalize(new PrivilegeLogTemplateDefinition(Preset: PrivilegeLogPreset.DocumentByDocument, DateFormat: " yyyy-MM-dd "),
            Catalog, None);
        errorsA.Should().BeEmpty();
        PrivilegeLogTemplateRules.Serialize(a!).Should().Be(PrivilegeLogTemplateRules.Serialize(b!), "equal templates have equal bytes");
        a!.Columns!.Select(c => c.Header).Should().Equal("Priv ID", "Date", "From", "Subject/File Name", "Doc Type", "Privilege Basis", "Description",
            "Attorneys Involved", "Family Range", "Withheld/Redacted");
        a.PrivIdPrefix.Should().Be("PRIV");
        a.PrivIdStart.Should().Be(1);
        a.PrivIdPadding.Should().Be(4);
        a.TimeZone.Should().Be("UTC");
        a.MultiValueSeparator.Should().Be("; ");
        PrivilegeLogTemplateRules.Deserialize(PrivilegeLogTemplateRules.Serialize(a)).Should().BeEquivalentTo(a);

        var (metadataOnly, _) = PrivilegeLogTemplateRules.Normalize(new PrivilegeLogTemplateDefinition(Preset: PrivilegeLogPreset.MetadataOnly), Catalog, None);
        metadataOnly!.Columns!.Select(c => c.Header).Should().NotContain(["Description", "Attorneys Involved"]);

        // A hidden field is left out of a preset and refused in a template, like an unknown one.
        var hidden = new HashSet<int> { 1001 };
        PrivilegeLogTemplateRules.Normalize(null, Catalog, hidden).Definition!.Columns!.Select(c => c.Header).Should().NotContain("From");
        var (refused, errors) = PrivilegeLogTemplateRules.Normalize(new PrivilegeLogTemplateDefinition(
            [new PrivilegeLogColumn(PrivilegeLogColumnKind.PrivId), new PrivilegeLogColumn(PrivilegeLogColumnKind.Field, FieldId: 1001)]), Catalog, hidden);
        refused.Should().BeNull();
        errors.Keys.Should().Equal("columns[1].fieldId");
        PrivilegeLogTemplateRules.FieldIdsOf(a, Catalog).Should().Contain([SystemFields.DocumentDate, 1001, 1002, SystemFields.FileName, PrivilegeFields.Basis]);
    }

    [Fact]
    public void Exclusion_rules_need_a_condition_and_valid_fields_and_choices()
    {
        var (ok, errors) = PrivilegeLogTemplateRules.Normalize(new PrivilegeLogTemplateDefinition(ExclusionRules:
        [
            new PrivilegeLogExclusionRule("  Post-complaint outside counsel  ", OnOrAfter: new DateOnly(2026, 1, 15), LogCategoryChoiceIds: [501, 501],
                AttorneysInvolved: ["Riley Counsel", "riley counsel", "Avery Advocate"]),
        ]), Catalog, None);
        errors.Should().BeEmpty();
        var rule = ok!.ExclusionRules!.Single();
        rule.Label.Should().Be("Post-complaint outside counsel");
        rule.DateFieldId.Should().Be(SystemFields.DocumentDate, "the date range tests Document Date by default");
        rule.LogCategoryChoiceIds.Should().Equal(501);
        rule.AttorneysInvolved.Should().Equal("Avery Advocate", "Riley Counsel");

        var (_, problems) = PrivilegeLogTemplateRules.Normalize(new PrivilegeLogTemplateDefinition(
            Columns: [new PrivilegeLogColumn(PrivilegeLogColumnKind.Treatment, FieldId: SystemFields.FileName)],
            PrivIdPrefix: "-bad",
            PrivIdPadding: 11,
            DateFormat: "hh:mm",
            TimeZone: "Mars/Olympus",
            ExclusionRules:
            [
                new PrivilegeLogExclusionRule("No condition"),
                new PrivilegeLogExclusionRule("Bad range", OnOrAfter: new DateOnly(2026, 2, 1), Before: new DateOnly(2026, 1, 1)),
                new PrivilegeLogExclusionRule("Not a date", DateFieldId: 1002, OnOrAfter: new DateOnly(2026, 1, 1)),
                new PrivilegeLogExclusionRule("Unknown category", LogCategoryChoiceIds: [999]),
            ]), Catalog, None);
        problems.Keys.Should().BeEquivalentTo(
        [
            "columns[0].fieldId", "privIdPrefix", "privIdPadding", "dateFormat", "timeZone", "exclusionRules[0]", "exclusionRules[1].before",
            "exclusionRules[2].dateFieldId", "exclusionRules[3].logCategoryChoiceIds",
        ]);
    }

    [Fact]
    public void Log_files_are_pure_functions_of_the_frozen_metadata_and_rows()
    {
        var metadata = new PrivilegeLogMetadata(1, PrivilegeLogSourceKind.Production, Guid.Parse("01890000-0000-7000-8000-000000000001"), "Production 1", 1,
            Guid.Parse("01890000-0000-7000-8000-000000000002"), null, null, "Document-by-document", ["Priv ID", "Description", "Withheld/Redacted"], false,
            [new PrivilegeLogAppliedRule("Post-complaint", "Document Date", new DateOnly(2026, 1, 15), null, [], [], 3)], 2, 1, 1, 0, 3);
        IReadOnlyList<string>[] rows =
        [
            ["ABC0000001", "=HYPERLINK(\"x\")", "Redacted"],
            ["PRIV0001", "Advice, \"quoted\"\nsecond line", "Withheld"],
        ];

        var csv = PrivilegeLogFiles.Csv(metadata, rows);
        PrivilegeLogFiles.Csv(PrivilegeLogFiles.DeserializeMetadata(PrivilegeLogFiles.SerializeMetadata(metadata)), rows).Should().Equal(csv);
        csv.AsSpan(0, 3).ToArray().Should().Equal(Encoding.UTF8.GetPreamble());
        Encoding.UTF8.GetString(csv.AsSpan(3)).Should().Be(
            "Priv ID,Description,Withheld/Redacted\r\nABC0000001,\"'=HYPERLINK(\"\"x\"\")\",Redacted\r\nPRIV0001,\"Advice, \"\"quoted\"\"\nsecond line\",Withheld\r\n",
            "formula-like text is neutralized (T-48) and RFC 4180 quoting applies");

        var xlsx = PrivilegeLogFiles.Xlsx(metadata, rows);
        PrivilegeLogFiles.Xlsx(metadata, rows).Should().Equal(xlsx, "no times or random identifiers go into the package");
        using var zip = new ZipArchive(new MemoryStream(xlsx), ZipArchiveMode.Read);
        zip.Entries.Select(e => e.FullName).Should().Contain(["xl/worksheets/sheet1.xml", "xl/worksheets/sheet2.xml"]);
        string Read(string name)
        {
            using var reader = new StreamReader(zip.GetEntry(name)!.Open());
            return reader.ReadToEnd();
        }

        Read("xl/workbook.xml").Should().Contain("Privilege Log").And.Contain("Log Information");
        Read("xl/worksheets/sheet1.xml").Should().Contain("t=\"inlineStr\"").And.Contain("=HYPERLINK(&quot;x&quot;)").And.NotContain("<f>");
        Read("xl/worksheets/sheet2.xml").Should().Contain("Exclusion rule: Post-complaint").And.Contain("Document Date on or after 2026-01-15 (3 documents excluded)");

        var content = PrivilegeLogFiles.ContentSha256(PrivilegeLogFiles.SerializeMetadata(metadata), csv);
        PrivilegeLogFiles.ContentSha256(PrivilegeLogFiles.SerializeMetadata(metadata with { ExcludedByRules = 4 }), csv).Should().NotEqual(content);
    }
}
