using System.Text.RegularExpressions;

using AwesomeAssertions;

using Opportunity.Core.Text;
using Opportunity.UnitTests.Security;

namespace Opportunity.UnitTests.Exports;

/// <summary>
/// E08-T09 / ADR-015 D15.5 / T-48: formula-leading cells of every platform-produced CSV and DAT are neutralized by one
/// shared rule, and no writer carries its own copy of it.
/// </summary>
public sealed partial class FormulaNeutralizerTests
{
    [Theory]
    [InlineData("=SUM(A1:A3)", "'=SUM(A1:A3)")]
    [InlineData("+1 555 0100", "'+1 555 0100")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("-", "'-")]
    [InlineData("-2+3+cmd|' /C calc'!A0", "'-2+3+cmd|' /C calc'!A0")]
    [InlineData("-1e3", "'-1e3")]
    [InlineData("-1.", "'-1.")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tlead", "'\tlead")]
    [InlineData("\rlead", "'\rlead")]
    [InlineData("＝SUM(A1)", "'＝SUM(A1)")]
    [InlineData("-12.5", "-12.5")]
    [InlineData("-7", "-7")]
    [InlineData("plain = text", "plain = text")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Formula_leading_cells_are_prefixed_and_plain_negative_numbers_are_not(string? value, string expected) =>
        FormulaNeutralizer.Neutralize(value).Should().Be(expected);

    [Fact]
    public void No_csv_or_dat_writer_has_its_own_copy_of_the_rule()
    {
        var src = Path.Combine(PermissionMatrixTests.RepositoryRoot(), "src");
        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(Path.Combine("Text", "FormulaNeutralizer.cs"), StringComparison.Ordinal))
            .Where(f => OwnRule().IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(src, f))
            .ToList();

        offenders.Should().BeEmpty("every platform-produced CSV/DAT neutralizes through FormulaNeutralizer (ADR-015 D15.5)");
    }

    [GeneratedRegex(@"\[0\]\s+is\s+'='")]
    private static partial Regex OwnRule();
}
