using System.Text.RegularExpressions;

using AwesomeAssertions;

using Opportunity.Application.Audit;

namespace Opportunity.UnitTests.Audit;

/// <summary>E14-T01: the ADR-013 §4 envelope rules and the §5 closed taxonomy, checked before an event reaches the store.</summary>
public sealed partial class AuditEventRulesTests
{
    private static AuditEvent Valid() => new()
    {
        OccurredAt = new DateTimeOffset(2026, 10, 3, 8, 0, 0, TimeSpan.Zero),
        Category = AuditTaxonomy.Auth.Category,
        Action = AuditTaxonomy.Auth.SignIn,
        ActorType = AuditActorType.User,
        ActorId = "user-1",
        ActorDisplay = "Alice",
        Outcome = AuditOutcome.Success,
    };

    [Fact]
    public void A_complete_event_is_valid_and_gets_a_version7_id()
    {
        var e = Valid();

        AuditEventRules.Validate(e).Should().BeEmpty();
        e.EventId.Version.Should().Be(7);
        e.SchemaVersion.Should().Be(AuditEvent.CurrentSchemaVersion);
        e.WorkspaceId.Should().BeNull("installation-level events belong to the system chain");
    }

    public static TheoryData<string, AuditEvent> Invalid => new()
    {
        { "taxonomy", Valid() with { Action = "Teleported" } },
        { "taxonomy", Valid() with { Category = "Coding", Action = "SignIn" } },
        { "ReasonCode", Valid() with { Outcome = AuditOutcome.Denied } },
        { "ReasonCode", Valid() with { Outcome = AuditOutcome.Failure, ReasonCode = "" } },
        { "ReasonCode", Valid() with { Outcome = AuditOutcome.Denied, ReasonCode = new string('r', 129) } },
        { "WorkspaceId", Valid() with { WorkspaceId = Guid.Empty } },
        { "EventId", Valid() with { EventId = Guid.Empty } },
        { "ActorId", Valid() with { ActorId = "" } },
        { "ActorDisplay", Valid() with { ActorDisplay = new string('a', 513) } },
        { "SessionIdHash", Valid() with { SessionIdHash = new byte[16] } },
        { "ChunkSequence", Valid() with { ChunkSequence = 2 } },
        { "CorrelationId", Valid() with { CorrelationId = new string('c', 129) } },
        { "Details", Valid() with { Details = new Dictionary<string, string?> { ["Blob"] = new string('d', 8 * 1024) } } },
        { "RestrictedDetails", Valid() with { RestrictedDetails = new Dictionary<string, string?> { ["QueryText"] = "privileged" } } },
        {
            "RestrictedDetails",
            Valid() with
            {
                Category = "Search",
                Action = "Executed",
                RestrictedDetails = new Dictionary<string, string?> { ["QueryText"] = new string('q', 64 * 1024) },
            }
        },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Envelope_violations_are_reported(string field, AuditEvent e)
    {
        AuditEventRules.Validate(e).Should().ContainSingle().Which.Should().Contain(field);
        var act = () => AuditEventRules.EnsureValid(e);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Search_events_carry_the_full_query_text_in_restricted_details()
    {
        var search = Valid() with
        {
            Category = "Search",
            Action = "Executed",
            RestrictedDetails = new Dictionary<string, string?> { ["QueryText"] = "\"price fixing\" W/5 (smith OR jones)" + new string('x', 30_000) },
        };

        AuditEventRules.Validate(search).Should().BeEmpty("Q-16 keeps the executed text, up to 64 KB");
    }

    [Fact]
    public void Long_user_agents_are_truncated_not_rejected()
    {
        var e = Valid() with { UserAgent = new string('u', 2_000) };

        AuditEventRules.Normalize(e).UserAgent.Should().HaveLength(AuditEventRules.MaxUserAgentLength);
        AuditEventRules.Validate(AuditEventRules.Normalize(e)).Should().BeEmpty();
        var shortAgent = Valid() with { UserAgent = "Mozilla/5.0" };
        AuditEventRules.Normalize(shortAgent).Should().BeSameAs(shortAgent);
    }

    [Fact]
    public void Json_size_counts_like_postgresql_prints_jsonb()
    {
        // jsonb::text is {"a": "b", "c": null}: the separators carry a space each.
        AuditEventRules.JsonBytes(new Dictionary<string, string?> { ["a"] = "b", ["c"] = null })
            .Should().Be("{\"a\": \"b\", \"c\": null}".Length);
        AuditEventRules.JsonBytes(new Dictionary<string, string?> { ["k"] = "é" }).Should().Be("{\"k\": \"é\"}".Length + 1, "UTF-8 bytes");
    }

    [Fact]
    public void The_taxonomy_is_closed_unique_and_matches_adr_013()
    {
        AuditTaxonomy.All.Should().OnlyHaveUniqueItems();
        AuditTaxonomy.IsDefined("Auth", "SignIn").Should().BeTrue();
        AuditTaxonomy.IsDefined("Search", "SavedSearch.Created").Should().BeTrue();
        AuditTaxonomy.IsDefined("auth", "signin").Should().BeFalse("names are case-sensitive");

        // Every action listed in the ADR-013 §5 table is in the code list, and nothing else.
        var adr = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "adr", "0013-audit-architecture-and-event-taxonomy.md"));
        var section = adr[adr.IndexOf("### 5. Taxonomy", StringComparison.Ordinal)..adr.IndexOf("### 6.", StringComparison.Ordinal)];
        var documented = new HashSet<(string, string)>();
        var category = "";
        foreach (Match row in TaxonomyRow().Matches(section))
        {
            category = row.Groups["category"].Success ? row.Groups["category"].Value : category;
            foreach (Match action in Backticked().Matches(row.Groups["actions"].Value))
            {
                var name = action.Groups[1].Value;
                foreach (var expanded in name.Contains('/', StringComparison.Ordinal) ? ExpandSlash(name) : [name])
                {
                    documented.Add((category, expanded));
                }
            }
        }

        AuditTaxonomy.All.Should().BeEquivalentTo(documented);
    }

    [Fact]
    public void Auth_and_job_constants_are_part_of_the_taxonomy()
    {
        foreach (var (category, action) in new[]
        {
            (AuditTaxonomy.Auth.Category, AuditTaxonomy.Auth.SignOut),
            (AuditTaxonomy.Auth.Category, AuditTaxonomy.Auth.StepUp),
            (AuditTaxonomy.AuthZ.Category, AuditTaxonomy.AuthZ.Denied),
            (AuditTaxonomy.Coding.Category, AuditTaxonomy.Coding.BulkChunkApplied),
            (AuditTaxonomy.Job.Category, AuditTaxonomy.Job.CompletedWithErrors),
            (AuditTaxonomy.Audit.Category, AuditTaxonomy.Audit.Queried),
        })
        {
            AuditTaxonomy.IsDefined(category, action).Should().BeTrue($"{category}.{action}");
        }
    }

    // "SavedSearch.Created/Modified/Deleted" -> SavedSearch.Created, SavedSearch.Modified, SavedSearch.Deleted
    private static IEnumerable<string> ExpandSlash(string name)
    {
        var dot = name.LastIndexOf('.');
        var prefix = name[..(dot + 1)];
        return name[(dot + 1)..].Split('/').Select(a => prefix + a);
    }

    [GeneratedRegex(@"^\| (?:\*\*(?<category>\w+)\*\*)? *\| (?<actions>[^|]+) \|", RegexOptions.Multiline)]
    private static partial Regex TaxonomyRow();

    [GeneratedRegex(@"`([A-Za-z./]+)`")]
    private static partial Regex Backticked();

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Opportunity.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
