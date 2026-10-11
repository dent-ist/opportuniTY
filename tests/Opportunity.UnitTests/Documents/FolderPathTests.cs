using AwesomeAssertions;

using Opportunity.Core.Documents;

namespace Opportunity.UnitTests.Documents;

/// <summary>#179: imported folder paths (Q-55).</summary>
public class FolderPathTests
{
    [Theory]
    [InlineData(@"\Mail\Inbox", new[] { "Mail", "Inbox" })]
    [InlineData("Mail/Inbox/", new[] { "Mail", "Inbox" })]
    [InlineData(@" /Mail // In box \ ", new[] { "Mail", "In box" })]
    [InlineData(@"C:\Users\Smith", new[] { "C:", "Users", "Smith" })]
    [InlineData(@"\\", new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData(null, new string[0])]
    public void Separators_and_blanks_are_normalized(string? raw, string[] expected)
    {
        FolderPath.TryParse(raw, out var segments, out var error).Should().BeTrue(error);
        segments.Should().Equal(expected);
    }

    [Fact]
    public void Paths_that_cannot_be_folders_are_refused_with_a_reason()
    {
        FolderPath.TryParse("Mail\tBox", out _, out var control).Should().BeFalse();
        control.Should().Contain("control character");
        FolderPath.TryParse(new string('a', FolderPath.MaxNameLength + 1), out _, out var name).Should().BeFalse();
        name.Should().Contain("longer than");
        FolderPath.TryParse(string.Join('/', Enumerable.Repeat("a", FolderPath.MaxDepth + 1)), out _, out var depth).Should().BeFalse();
        depth.Should().Contain("deeper than");
        FolderPath.TryParse(string.Join('/', Enumerable.Repeat(new string('a', 200), 30)), out _, out var length).Should().BeFalse();
        length.Should().Contain("longer than");
    }

    [Fact]
    public void Folders_match_case_insensitively_and_list_their_ancestors()
    {
        FolderPath.Key(FolderPath.Join(["Mail", "Inbox"])).Should().Be(FolderPath.Key(@"MAIL\inbox"));
        FolderPath.Prefixes(["Mail", "Inbox", "Archive"]).Should().Equal("Mail", @"Mail\Inbox", @"Mail\Inbox\Archive");
    }
}
