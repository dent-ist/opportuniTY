using System.Globalization;

using AwesomeAssertions;

using Opportunity.DataGenerator.Corpus.Generation;
using Opportunity.DataGenerator.Corpus.Model;
using Opportunity.DataGenerator.Corpus.Profiles;

namespace Opportunity.DataGenerator.Tests;

/// <summary>
/// Structural invariants every generated corpus must satisfy (families, duplicates, threads). Violations are
/// collected and asserted once: per-document assertions are too slow for tens of thousands of documents.
/// </summary>
public class IntegrityTests
{
    private static readonly Lazy<List<GeneratedFamily>> Families =
        new(() => [.. new CorpusGenerator(TestCorpus.Profile(20_000), 77, 4).GenerateFamilies()]);

    private static readonly Lazy<List<GeneratedDocument>> Docs = new(() => [.. Families.Value.SelectMany(f => f.Documents)]);

    [Fact]
    public void Documents_are_numbered_consecutively_in_load_order()
    {
        var errors = new Violations();
        List<GeneratedDocument> docs = Docs.Value;
        docs.Count.Should().Be(20_000);
        for (int i = 0; i < docs.Count; i++)
        {
            errors.Expect(docs[i].DocIndex == i, $"docIndex {docs[i].DocIndex} at {i}");
            errors.Expect(docs[i].ControlNumber == "OPP" + (i + 1).ToString("D10", CultureInfo.InvariantCulture), $"control number {docs[i].ControlNumber} at {i}");
        }

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Families_are_contiguous_parent_first_and_well_formed()
    {
        var errors = new Violations();
        int maxDepth = new CorpusProfile().Families.MaxAttachmentDepth;
        foreach (GeneratedFamily family in Families.Value)
        {
            IReadOnlyList<GeneratedDocument> docs = family.Documents;
            GeneratedDocument parent = docs[0];
            errors.Expect(parent.ParentControlNumber == null && parent.AttachmentDepth == 0, $"{parent.ControlNumber}: parent has a parent");
            for (int i = 0; i < docs.Count; i++)
            {
                GeneratedDocument d = docs[i];
                string id = d.ControlNumber;
                errors.Expect(d.FamilySequence == i, $"{id}: family sequence");
                errors.Expect(d.FamilyId == parent.ControlNumber, $"{id}: family id");
                errors.Expect(d.BegAttach == parent.ControlNumber && d.EndAttach == docs[^1].ControlNumber, $"{id}: beg/end attach");
                errors.Expect(d.Custodian == family.Custodian, $"{id}: custodian differs within family");
                errors.Expect(d.AttachmentDepth <= maxDepth, $"{id}: depth {d.AttachmentDepth}");
                if (i == 0)
                {
                    continue;
                }

                // Pre-order: the parent node precedes the child, is one level up, and everything between is its descendant.
                int p = d.ParentSequence;
                errors.Expect(p >= 0 && p < i, $"{id}: parent sequence {p}");
                if (p < 0 || p >= i)
                {
                    continue;
                }

                errors.Expect(d.ParentControlNumber == docs[p].ControlNumber, $"{id}: parent control number");
                errors.Expect(d.AttachmentDepth == docs[p].AttachmentDepth + 1, $"{id}: depth vs parent");
                for (int k = p + 1; k < i; k++)
                {
                    errors.Expect(docs[k].AttachmentDepth > docs[p].AttachmentDepth, $"{id}: not pre-order");
                }
            }
        }

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Duplicate_groups_are_consistent()
    {
        var errors = new Violations();
        int groups = 0;
        foreach (IGrouping<string, GeneratedDocument> group in Docs.Value.GroupBy(d => d.Md5))
        {
            List<GeneratedDocument> members = [.. group];
            string[] custodians = [.. members.Select(m => m.Custodian).Distinct().Order(StringComparer.Ordinal)];
            GeneratedDocument first = members[0];
            errors.Expect(first.IsDuplicatePrimary && first.DuplicateType == DuplicateType.None, $"{first.ControlNumber}: first occurrence must be primary");
            foreach (GeneratedDocument m in members)
            {
                errors.Expect(m.AllCustodians.SequenceEqual(custodians), $"{m.ControlNumber}: AllCustodians");
                errors.Expect(m.DuplicateCustodians.SequenceEqual(custodians.Where(c => c != m.Custodian)), $"{m.ControlNumber}: DuplicateCustodians");
                errors.Expect(m.Content.Sha256 == first.Content.Sha256 && m.TextBytes == first.TextBytes, $"{m.ControlNumber}: content differs within MD5 group");
                errors.Expect(m.DuplicateGroupId == (members.Count == 1 ? null : first.DuplicateGroupId) && (members.Count == 1 || m.DuplicateGroupId != null),
                    $"{m.ControlNumber}: duplicate group id");
            }

            for (int i = 1; i < members.Count; i++)
            {
                GeneratedDocument m = members[i];
                IEnumerable<GeneratedDocument> earlier = members.Take(i);
                DuplicateType expected = earlier.Any(e => e.FamilyId == m.FamilyId) ? DuplicateType.WithinFamily
                    : earlier.Any(e => e.Custodian == m.Custodian) ? DuplicateType.ExactMd5
                    : DuplicateType.CrossCustodian;
                errors.Expect(!m.IsDuplicatePrimary && m.DuplicateType == expected, $"{m.ControlNumber}: type {m.DuplicateType}, expected {expected}");
            }

            groups += members.Count > 1 ? 1 : 0;
        }

        groups.Should().BeGreaterThan(1000);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Family_duplicate_copies_are_whole_family_copies()
    {
        var errors = new Violations();
        foreach (IGrouping<string?, GeneratedFamily> group in Families.Value.Where(f => f.Parent.FamilyDuplicateGroupId != null).GroupBy(f => f.Parent.FamilyDuplicateGroupId))
        {
            List<GeneratedFamily> copies = [.. group];
            errors.Expect(copies.Count > 1, $"{group.Key}: single copy");
            errors.Expect(copies.Select(c => c.CopyIndex).Distinct().Count() == copies.Count, $"{group.Key}: copy indexes");
            string[] reference = [.. copies[0].Documents.Select(d => d.Md5)];
            errors.Expect(copies.All(c => c.Documents.Select(d => d.Md5).SequenceEqual(reference)), $"{group.Key}: members differ");
            errors.Expect(copies.All(c => c.Documents.All(d => d.FamilyDuplicateGroupId == group.Key)), $"{group.Key}: member group ids");
        }

        Families.Value.Where(f => f.Parent.FamilyDuplicateGroupId == null).Should().OnlyContain(f => f.CopyIndex == 0);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Email_threads_have_consistent_conversation_index_and_reply_chain()
    {
        var errors = new Violations();
        List<GeneratedDocument> emails = [.. Docs.Value.Where(d => d.EmailThreadId != null && d.IsDuplicatePrimary)];
        var byMessageId = emails.ToDictionary(d => (string)d.Fields[FieldCatalog.MessageId]!);
        int replies = 0;
        foreach (GeneratedDocument reply in emails.Where(d => d.Fields[FieldCatalog.InReplyTo] != null))
        {
            string id = reply.ControlNumber;
            GeneratedDocument parent = byMessageId[(string)reply.Fields[FieldCatalog.InReplyTo]!];
            string childIndex = (string)reply.Fields[FieldCatalog.ConversationIndex]!;
            string parentIndex = (string)parent.Fields[FieldCatalog.ConversationIndex]!;
            errors.Expect(parent.EmailThreadId == reply.EmailThreadId, $"{id}: thread id");
            errors.Expect(childIndex.StartsWith(parentIndex, StringComparison.Ordinal) && childIndex.Length == parentIndex.Length + 10, $"{id}: ConversationIndex");
            errors.Expect((DateTimeOffset)reply.Fields[FieldCatalog.DateSent]! >= (DateTimeOffset)parent.Fields[FieldCatalog.DateSent]!, $"{id}: date before parent");
            errors.Expect(((string)reply.Fields[FieldCatalog.Subject]!).StartsWith("RE: ", StringComparison.Ordinal)
                || ((string)reply.Fields[FieldCatalog.Subject]!).StartsWith("FW: ", StringComparison.Ordinal), $"{id}: subject prefix");
            replies++;
        }

        replies.Should().BeGreaterThan(100);
        emails.Where(d => d.Fields[FieldCatalog.InReplyTo] == null)
            .Should().OnlyContain(d => ((string)d.Fields[FieldCatalog.ConversationIndex]!).Length == 44);

        // Leaves of the reply tree are inclusive.
        var repliedTo = emails.Select(d => d.Fields[FieldCatalog.InReplyTo] as string).Where(x => x != null).ToHashSet();
        emails.Where(d => !repliedTo.Contains((string)d.Fields[FieldCatalog.MessageId]!))
            .Should().OnlyContain(d => (bool)d.Fields[FieldCatalog.InclusiveEmail]!);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Recipients_stay_within_bounds_and_the_custodian_is_on_the_message()
    {
        var errors = new Violations();
        int checkedCount = 0;
        foreach (GeneratedFamily family in Families.Value.Where(f => f.CopyIndex == 0 && f.EmailThreadId != null))
        {
            GeneratedDocument d = family.Parent;
            var to = (string[])d.Fields[FieldCatalog.To]!;
            errors.Expect(to.Length is >= 1 and <= 501, $"{d.ControlNumber}: {to.Length} recipients");
            string all = string.Join("|", [(string)d.Fields[FieldCatalog.From]!, .. to,
                .. (d.Fields[FieldCatalog.Cc] as string[] ?? []), .. (d.Fields[FieldCatalog.Bcc] as string[] ?? [])]);
            errors.Expect(all.Contains(d.Custodian.Split(" (")[0], StringComparison.Ordinal), $"{d.ControlNumber}: custodian not on message");
            checkedCount++;
        }

        checkedCount.Should().BeGreaterThan(1000);
        errors.Should().BeEmpty();
    }

    private sealed class Violations : List<string>
    {
        public void Expect(bool condition, string message)
        {
            if (!condition && Count < 20)
            {
                Add(message);
            }
        }
    }
}
