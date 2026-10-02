using AwesomeAssertions;

using Opportunity.Core.Documents;

namespace Opportunity.UnitTests.Documents;

public class ControlNumberTests
{
    [Theory]
    [InlineData("abc0001", false, null, "ABC0001")]
    [InlineData("abc0001", true, null, "abc0001")]
    [InlineData("  ABC  0001\t", false, null, "ABC 0001")]
    [InlineData("\u00A0\uFEFFABC\u00A0\u00A00001\uFEFF", false, null, "ABC 0001")]
    [InlineData("r\u00E9sum\u00E9", false, null, "R\u00C9SUM\u00C9")]
    [InlineData("0001", false, "vol1-", "VOL1-0001")]
    [InlineData("ABC/0001.a", false, null, "ABC/0001.A")]
    public void Normalize_applies_adr009_r2(string raw, bool caseSensitive, string? prefix, string expected)
    {
        ControlNumber.Normalize(raw, caseSensitive, prefix).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \u00A0")]
    [InlineData("ABC\u00010001")]
    public void Normalize_rejects_empty_and_control_characters(string raw)
    {
        ControlNumber.TryNormalize(raw, false, null, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Normalize_rejects_more_than_255_characters()
    {
        ControlNumber.TryNormalize(new string('A', 255), false, null, out _, out _).Should().BeTrue();
        ControlNumber.TryNormalize(new string('A', 256), false, null, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Sort_key_orders_digit_runs_numerically()
    {
        string[] controlNumbers = ["ABC10", "ABC0011", "ABC9", "ABC1", "AB", "ABD1", "ABC9.2", "ABC9.10"];

        var sorted = controlNumbers.OrderBy(ControlNumber.SortKey, StringComparer.Ordinal).ToArray();

        sorted.Should().Equal("AB", "ABC1", "ABC9", "ABC9.2", "ABC9.10", "ABC10", "ABC0011", "ABD1");
    }

    [Fact]
    public void Sort_key_pads_runs_to_20_digits_and_keeps_longer_runs()
    {
        ControlNumber.SortKey("ABC9").Should().Be("ABC" + new string('0', 19) + "9");
        var longRun = new string('7', 25);
        ControlNumber.SortKey("X" + longRun).Should().Be("X" + longRun);
    }

    [Fact]
    public void Create_makes_a_standalone_family_of_one()
    {
        var workspaceId = Guid.CreateVersion7();

        var document = Document.Create(workspaceId, " abc0001 ", caseSensitive: false);

        document.WorkspaceId.Should().Be(workspaceId);
        document.DocumentId.Version.Should().Be(7);
        document.ControlNumber.Should().Be("abc0001");
        document.ControlNumberNorm.Should().Be("ABC0001");
        document.FamilyId.Should().Be(document.DocumentId);
        document.FamilySequence.Should().Be(0);
        document.ParentDocumentId.Should().BeNull();
    }
}
