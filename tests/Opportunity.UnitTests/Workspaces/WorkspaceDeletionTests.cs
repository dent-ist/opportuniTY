using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Workspaces.Deletion;
using Opportunity.Jobs.Lifecycle;

namespace Opportunity.UnitTests.Workspaces;

/// <summary>E20-T02: the purge order, the destruction certificate and the deletion rules that need no database.</summary>
public sealed class WorkspaceDeletionTests
{
    [Fact]
    public void The_purge_order_puts_referencing_tables_first_and_purges_documents_with_their_page_sets_and_objects()
    {
        string[] tables = ["page", "page_set", "document", "stored_object", "export_file", "coding_event", "choice", "field_definition", "email_thread"];
        PurgeTableReference[] references =
        [
            new("page", "page_set", false),
            new("page_set", "document", false),
            new("document", "page_set", true),
            new("document", "stored_object", false),
            new("stored_object", "document", true),
            new("document", "email_thread", true),
            new("export_file", "stored_object", false),
            new("coding_event", "document", false),
            new("coding_event", "field_definition", false),
            new("choice", "field_definition", false),
            new("document", "document", true),
            new("document", "workspace", false),
        ];

        var order = WorkspacePurgePlan.Order(tables, references).ToList();

        order.Should().BeEquivalentTo(tables).And.OnlyHaveUniqueItems();
        order.IndexOf("page").Should().BeLessThan(order.IndexOf("document"));
        order.IndexOf("export_file").Should().BeLessThan(order.IndexOf("document"), "objects go with their documents, after everything referencing them");
        order.IndexOf("coding_event").Should().BeLessThan(order.IndexOf("document"));
        order.IndexOf("choice").Should().BeLessThan(order.IndexOf("field_definition"));
        order.IndexOf("document").Should().BeLessThan(order.IndexOf("email_thread"));
        order.Skip(order.IndexOf("document")).Take(3).Should().Equal(WorkspacePurgePlan.DocumentCluster);
    }

    [Fact]
    public void An_unknown_reference_cycle_fails_the_plan_instead_of_breaking_a_foreign_key()
    {
        var plan = () => WorkspacePurgePlan.Order(["a", "b"], [new PurgeTableReference("a", "b", false), new PurgeTableReference("b", "a", true)]);

        plan.Should().Throw<InvalidOperationException>().WithMessage("*a, b*");
    }

    [Fact]
    public void The_certificate_is_canonical_carries_counts_and_residuals_and_no_document_data()
    {
        var deletion = Deletion(DeletionRetentionProfile.PurgeAll);
        var at = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        WorkspaceDeletionStepRecord[] steps =
        [
            Step(DeletionStep.Inventory, at, """
                {"postgres":{"document":{"total":120,"retained":0},"coding_event":{"total":30,"retained":0}},
                 "openSearch":{"documents":120,"dedicatedIndexes":0},"objects":{"objects":12,"bytes":4096},"keys":{"total":1,"active":1,"retired":0,"destroyed":0}}
                """),
            Step(DeletionStep.SearchPurge, at, """{"indexesDeleted":0,"documentsDeleted":120,"documentsAfter":0}"""),
            Step(DeletionStep.StoragePurge, at, """{"objects":12,"bytes":4096,"areas":["*"],"providerResiduals":[]}"""),
            Step(DeletionStep.KeyDestruction, at, """{"destroyed":1,"dedicatedKekDestroyed":false}"""),
            Step(DeletionStep.Verification, at, """
                {"postgres":{},"openSearch":{"documents":0,"dedicatedIndexes":0,"secondPassDocumentsDeleted":3,"secondPassIndexesDeleted":0},
                 "objects":{"objects":0,"retainedObjects":0},"keys":{"usable":0,"destroyed":1}}
                """),
        ];
        var options = new WorkspaceDeletionOptions { BackupRetention = TimeSpan.FromDays(35) };

        var built = DestructionCertificate.Build(deletion, steps, residuals: false, at, options);
        var again = DestructionCertificate.Build(deletion, [.. steps.Reverse()], residuals: false, at, options);

        again.Bytes.Should().Equal(built.Bytes, "a rebuilt certificate of the same run hashes the same");
        built.Sha256.Should().Equal(SHA256.HashData(built.Bytes));
        using var json = JsonDocument.Parse(built.Bytes);
        var root = json.RootElement;
        root.GetProperty("outcome").GetString().Should().Be("Completed");
        root.GetProperty("workspace").GetProperty("matterNumber").GetString().Should().Be("M-2026-001");
        root.GetProperty("request").GetProperty("externalReference").GetString().Should().Be("PO ¶ 14");
        var stores = root.GetProperty("stores").EnumerateArray().ToDictionary(s => s.GetProperty("store").GetString()!);
        stores["PostgreSQL"].GetProperty("before").GetInt64().Should().Be(150);
        stores["PostgreSQL"].GetProperty("after").GetInt64().Should().Be(0);
        stores["OpenSearch"].GetProperty("documentsDeleted").GetInt64().Should().Be(123, "both search passes count");
        stores["ObjectStorage"].GetProperty("bytesDeleted").GetInt64().Should().Be(4096);
        stores["Keys"].GetProperty("status").GetString().Should().Be("Destroyed");
        var backups = root.GetProperty("residuals").EnumerateArray().First(r => r.GetProperty("kind").GetString() == "Backups");
        backups.GetProperty("expiresBy").GetString().Should().Be("2026-11-13T12:00:00.0000000Z");
        backups.GetProperty("description").GetString().Should().Contain("unreadable");
        Encoding.UTF8.GetString(built.Bytes).Should().NotContain("\n");
    }

    [Fact]
    public void A_retain_records_certificate_says_what_was_kept_and_that_backups_stay_readable_until_they_expire()
    {
        var built = DestructionCertificate.Build(Deletion(DeletionRetentionProfile.RetainRecords), [], residuals: true, DateTimeOffset.UnixEpoch,
            new WorkspaceDeletionOptions());
        var root = JsonNode.Parse(built.Bytes)!;

        root["outcome"]!.GetValue<string>().Should().Be("CompletedWithResiduals");
        var kinds = root["residuals"]!.AsArray().Select(r => r!["kind"]!.GetValue<string>()).ToList();
        kinds.Should().Contain(["RetainedRecords", "VerificationResiduals", "Backups"]);
        root["residuals"]![0]!["description"]!.GetValue<string>().Should().Contain("readable");
        root["stores"]!.AsArray().Single(s => s!["store"]!.GetValue<string>() == "Keys")!["status"]!.GetValue<string>()
            .Should().Be("RetainedForRetainedRecords");
    }

    [Fact]
    public void The_two_person_rule_and_visibility_hold_for_the_requester_and_approvers()
    {
        var deletion = Deletion(DeletionRetentionProfile.PurgeAll) with { Status = WorkspaceDeletionStatus.Requested };
        var requester = new DeletionCaller(new Application.Authorization.SecurityPrincipal { UserId = deletion.RequestedBy, DisplayName = "r" }, true);
        var approver = new DeletionCaller(new Application.Authorization.SecurityPrincipal { UserId = Guid.NewGuid(), DisplayName = "a" }, true);
        var stranger = new DeletionCaller(new Application.Authorization.SecurityPrincipal { UserId = Guid.NewGuid(), DisplayName = "s" }, false);
        var now = deletion.RequestedAt.AddHours(1);

        WorkspaceDeletionService.CanApproveDeletion(requester, deletion, now).Should().BeFalse("Q-23: the requester never approves");
        WorkspaceDeletionService.CanApproveDeletion(approver, deletion, now).Should().BeTrue();
        WorkspaceDeletionService.CanApproveDeletion(approver, deletion, deletion.ExpiresAt).Should().BeFalse("an expired request cannot be approved");
        WorkspaceDeletionService.CanSee(stranger, deletion).Should().BeFalse();
        WorkspaceDeletionService.CanCancel(requester, deletion with { Status = WorkspaceDeletionStatus.Running }).Should().BeFalse();
        WorkspaceDeletionCoordinator.CertificateEventId(deletion.DeletionId).Should().NotBe(deletion.DeletionId)
            .And.Be(WorkspaceDeletionCoordinator.CertificateEventId(deletion.DeletionId));
    }

    [Theory]
    [InlineData("00:00:00", false, false)]
    [InlineData("00:00:00", true, true)]
    [InlineData("1.00:00:00", false, true)]
    [InlineData("90.00:00:00", false, true)]
    [InlineData("91.00:00:00", false, false)]
    public void The_waiting_period_is_one_to_ninety_days_or_shorter_only_when_allowed(string period, bool allowShort, bool valid)
    {
        var options = new WorkspaceDeletionOptions { WaitingPeriod = TimeSpan.Parse(period, System.Globalization.CultureInfo.InvariantCulture), AllowShortWaitingPeriod = allowShort };

        var validate = options.Validate;

        if (valid)
        {
            validate.Should().NotThrow();
        }
        else
        {
            validate.Should().Throw<InvalidOperationException>();
        }
    }

    private static WorkspaceDeletion Deletion(DeletionRetentionProfile profile)
    {
        var at = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        return new WorkspaceDeletion
        {
            DeletionId = Guid.Parse("01a1a1a1-0000-7000-8000-000000000167"),
            WorkspaceId = Guid.Parse("01a1a1a1-0000-7000-8000-0000000000aa"),
            WorkspaceName = "Synthetic Matter",
            MatterNumber = "M-2026-001",
            RetentionProfile = profile,
            Reason = "Matter closed",
            ExternalReference = "PO ¶ 14",
            Status = WorkspaceDeletionStatus.Running,
            RequestedBy = Guid.Parse("01a1a1a1-0000-7000-8000-000000000001"),
            RequestedByName = "Avery Admin",
            RequestedAt = at,
            ExpiresAt = at.AddDays(30),
            ApprovedBy = Guid.Parse("01a1a1a1-0000-7000-8000-000000000002"),
            ApprovedByName = "Riley Approver",
            ApprovedAt = at.AddDays(1),
            RunNotBefore = at.AddDays(8),
            StartedAt = at.AddDays(8),
            FenceEpoch = 2,
            Version = 4,
        };
    }

    private static WorkspaceDeletionStepRecord Step(DeletionStep step, DateTimeOffset at, string counts) =>
        new(step, 1, at, at, DeletionStepOutcomes.Success, JsonNode.Parse(counts)!.AsObject(), null);
}
