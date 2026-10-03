using AwesomeAssertions;

using Opportunity.Core.Documents;

namespace Opportunity.UnitTests.Documents;

/// <summary>E09-T02: deterministic relationship ids, ConversationIndex handling and the upstream relationship rules.</summary>
public class UpstreamRelationshipTests
{
    private static readonly Guid Workspace = Guid.Parse("0199a8a0-0000-7000-8000-00000000aaaa");
    private static readonly Guid OtherWorkspace = Guid.Parse("0199a8a0-0000-7000-8000-00000000bbbb");

    // A 22-byte header plus one 5-byte child block.
    private const string IndexHex = "01D9A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4" + "0011223344";

    [Fact]
    public void Name_based_ids_follow_RFC_9562_version_5()
    {
        // RFC 9562 Appendix A.4: UUIDv5 of "www.example.com" in the DNS namespace.
        RelationshipIds.NameBased(Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8"), "www.example.com")
            .Should().Be(Guid.Parse("2ed6657d-e927-568b-95e1-2665a8aea6a2"));
    }

    [Fact]
    public void Group_and_thread_ids_are_stable_per_workspace_and_distinct_per_kind()
    {
        var group = RelationshipIds.DuplicateGroup(Workspace, DuplicateHashKind.UpstreamGroup, "DG-0001");

        group.Should().Be(RelationshipIds.DuplicateGroup(Workspace, DuplicateHashKind.UpstreamGroup, "DG-0001"), "a re-import gives the same id");
        group.Version.Should().Be(5);
        group.Should().NotBe(RelationshipIds.DuplicateGroup(OtherWorkspace, DuplicateHashKind.UpstreamGroup, "DG-0001"));
        group.Should().NotBe(RelationshipIds.DuplicateGroup(Workspace, DuplicateHashKind.UpstreamGroup, "dg-0001"), "upstream values are verbatim");
        group.Should().NotBe(RelationshipIds.DuplicateGroup(Workspace, DuplicateHashKind.UpstreamDedupeHash, "DG-0001"));
        group.Should().NotBe(RelationshipIds.EmailThread(Workspace, EmailThreadSource.Upstream, "DG-0001"));
        RelationshipIds.EmailThread(Workspace, EmailThreadSource.Upstream, "T1")
            .Should().NotBe(RelationshipIds.EmailThread(Workspace, EmailThreadSource.ConversationIndex, "T1"));
    }

    [Theory]
    [InlineData(IndexHex, false)]
    [InlineData("0x" + IndexHex, false)]
    [InlineData("01d9a1b2-c3d4e5f6 0718293a4b5c6d7e8f90a1b2c3d40011223344", false)]
    [InlineData("AdmhssPU5fYHGCk6S1xtfo+QobLD1AARIjNE", true)]
    public void Conversation_index_is_normalized_to_upper_case_hex(string input, bool base64)
    {
        ConversationIndex.TryNormalize(input, out var hex, out var fromBase64).Should().BeTrue();

        hex.Should().Be(IndexHex);
        fromBase64.Should().Be(base64);
        ConversationIndex.ThreadRoot(hex).Should().Be(IndexHex[..44]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("01D9A1B2")]
    [InlineData("not an index")]
    [InlineData("01D9A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D")]
    public void Short_or_garbled_conversation_indexes_are_rejected(string input) =>
        ConversationIndex.TryNormalize(input, out _, out _).Should().BeFalse();

    [Fact]
    public void Upstream_group_hash_and_thread_are_applied_and_reported_for_the_writer()
    {
        var document = Document.Create(Workspace, "ABC0001", caseSensitive: false);

        var result = UpstreamRelationships.Apply(document, new UpstreamRelationshipValues
        {
            DuplicateGroup = "DG-0001",
            DedupeHash = "0a1b",
            EmailHash = "ffee",
            EmailThreadGroup = "THREAD-7",
        });

        result.DuplicateGroup.Should().Be(new DuplicateGroupKey(
            RelationshipIds.DuplicateGroup(Workspace, DuplicateHashKind.UpstreamGroup, "DG-0001"),
            DuplicateGroupSource.Upstream, DuplicateHashKind.UpstreamGroup, "DG-0001"));
        document.DuplicateGroupId.Should().Be(result.DuplicateGroup!.DuplicateGroupId);
        document.IsDuplicatePrimary.Should().BeFalse("the writer elects the primary once the group's members are known");
        document.UpstreamDedupeHash.Should().Be("0a1b", "the dedupe hash wins over the email hash (ADR-009 R14 order)");
        document.UpstreamDedupeHashKind.Should().Be(DuplicateHashKind.UpstreamDedupeHash);
        document.EmailThreadId.Should().Be(RelationshipIds.EmailThread(Workspace, EmailThreadSource.Upstream, "THREAD-7"));
        document.EmailThreadSource.Should().Be(EmailThreadSource.Upstream);
        result.EmailThread!.ThreadKey.Should().Be("THREAD-7");
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void An_email_hash_is_kept_when_no_dedupe_hash_is_mapped()
    {
        var document = Document.Create(Workspace, "ABC0001", caseSensitive: false);

        UpstreamRelationships.Apply(document, new UpstreamRelationshipValues { EmailHash = "ffee" }).DuplicateGroup.Should().BeNull();

        document.UpstreamDedupeHash.Should().Be("ffee");
        document.UpstreamDedupeHashKind.Should().Be(DuplicateHashKind.UpstreamEmailHash);
        document.DuplicateGroupId.Should().BeNull("hash-only grouping is the computed job (E09-T04), not import");
    }

    [Fact]
    public void Conversation_index_threading_is_opt_in_and_never_overrides_an_upstream_thread()
    {
        var values = new UpstreamRelationshipValues { ConversationIndex = IndexHex };

        var off = Document.Create(Workspace, "ABC0001", caseSensitive: false);
        UpstreamRelationships.Apply(off, values).EmailThread.Should().BeNull();
        off.EmailThreadId.Should().BeNull();

        var on = Document.Create(Workspace, "ABC0002", caseSensitive: false);
        var derived = UpstreamRelationships.Apply(on, values, new UpstreamRelationshipOptions(DeriveEmailThreadFromConversationIndex: true));
        derived.EmailThread!.Source.Should().Be(EmailThreadSource.ConversationIndex);
        derived.EmailThread.ThreadKey.Should().Be(IndexHex[..44]);
        on.EmailThreadSource.Should().Be(EmailThreadSource.ConversationIndex);

        var reply = Document.Create(Workspace, "ABC0003", caseSensitive: false);
        UpstreamRelationships.Apply(reply, values with { ConversationIndex = IndexHex + "5566778899" }, new(true));
        reply.EmailThreadId.Should().Be(on.EmailThreadId, "replies share the conversation header");

        var upstream = Document.Create(Workspace, "ABC0004", caseSensitive: false);
        UpstreamRelationships.Apply(upstream, values with { EmailThreadGroup = "T-1" }, new(true));
        upstream.EmailThreadSource.Should().Be(EmailThreadSource.Upstream);
    }

    [Fact]
    public void Attachments_carry_no_email_thread_but_keep_their_duplicate_group()
    {
        var attachment = Document.Create(Workspace, "ABC0002", caseSensitive: false);

        var result = UpstreamRelationships.Apply(attachment, new UpstreamRelationshipValues
        {
            DuplicateGroup = "DG-9",
            EmailThreadGroup = "THREAD-7",
            IsAttachment = true,
        });

        attachment.EmailThreadId.Should().BeNull();
        attachment.EmailThreadSource.Should().BeNull();
        result.EmailThread.Should().BeNull();
        result.Warnings.Should().ContainSingle();
        attachment.DuplicateGroupId.Should().NotBeNull();
    }
}
