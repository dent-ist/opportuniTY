using System.Reflection;
using System.Text;

using AwesomeAssertions;

using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Opportunity.IntegrationTests.Api;

namespace Opportunity.IntegrationTests.Security;

/// <summary>
/// E05-T05 coverage report: every baseline §24 protected operation (and every other resource class of the attack
/// catalog) with its negative tests: the attacked routes and the scenario tests of this suite. The report is written to
/// the test output and attached to the test result; an operation without a negative test fails the build, unless no
/// route for it exists yet (production inclusion arrives with E12; its first route then needs a case here).
/// </summary>
public sealed class ProtectedOperationCoverageTests
{
    private static readonly Dictionary<string, string[]> Scenarios = new(StringComparer.Ordinal)
    {
        [ProtectedOperation.Open] = [nameof(StaleHitAuthorizationTests.Hits_that_became_restricted_or_walled_after_the_search_are_dropped_and_their_content_is_404_with_the_index_stale)],
        [ProtectedOperation.View] = [nameof(StaleHitAuthorizationTests.Hits_that_became_restricted_or_walled_after_the_search_are_dropped_and_their_content_is_404_with_the_index_stale)],
        [ProtectedOperation.NativeDownload] =
        [
            nameof(StaleHitAuthorizationTests.Hits_that_became_restricted_or_walled_after_the_search_are_dropped_and_their_content_is_404_with_the_index_stale),
            nameof(StaleSnapshotAuthorizationTests.Documents_hidden_after_the_freeze_are_skipped_by_bulk_coding_and_excluded_from_the_export_package),
        ],
        [ProtectedOperation.ImageRetrieval] = [nameof(StaleHitAuthorizationTests.Hits_that_became_restricted_or_walled_after_the_search_are_dropped_and_their_content_is_404_with_the_index_stale)],
        [ProtectedOperation.Export] =
        [
            nameof(StaleSnapshotAuthorizationTests.Documents_hidden_after_the_freeze_are_skipped_by_bulk_coding_and_excluded_from_the_export_package),
            nameof(CrossWorkspaceAttackTests.A_job_message_naming_another_workspace_is_rejected_and_runs_nothing),
        ],
        [ProtectedOperation.Coding] = [nameof(StaleSnapshotAuthorizationTests.Documents_hidden_after_the_freeze_are_skipped_by_bulk_coding_and_excluded_from_the_export_package)],
        [ProtectedOperation.Search] =
        [
            nameof(StaleHitAuthorizationTests.Hits_that_became_restricted_or_walled_after_the_search_are_dropped_and_their_content_is_404_with_the_index_stale),
            nameof(CrossWorkspaceAttackTests.Replayed_search_handles_and_cursors_are_404_and_audited_and_foreign_collection_cursors_reveal_nothing),
            nameof(RandomizedIsolationTests.Over_a_thousand_generated_queries_return_no_hits_counts_or_buckets_from_another_workspace),
        ],
        [ProtectedOperation.Snapshot] = [nameof(StaleSnapshotAuthorizationTests.Documents_hidden_after_the_freeze_are_skipped_by_bulk_coding_and_excluded_from_the_export_package)],
        [ProtectedOperation.Workspace] = [nameof(CrossWorkspaceAttackTests.Collections_outside_a_workspace_never_list_another_users_or_workspaces_data)],
    };

    [Fact]
    public async Task Every_protected_operation_has_a_negative_test_and_the_report_is_attached()
    {
        var tests = typeof(ProtectedOperationCoverageTests).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(ProtectedOperationCoverageTests).Namespace)
            .SelectMany(t => t.GetMethods())
            .Where(m => m.GetCustomAttribute<FactAttribute>() is not null)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);
        Scenarios.Values.SelectMany(v => v).Should().OnlyContain(name => tests.Contains(name), "the report names existing tests");

        await using var factory = new ApiFactory();
        using (factory.CreateClient())
        {
        }

        var patterns = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty).ToList();

        var report = new StringBuilder("# Cross-workspace attack suite: protected-operation coverage (E05-T05)\n\n")
            .Append("| Operation | §24 | Attacked routes (probes) | Scenario tests |\n|---|---|---|---|\n");
        var missing = new List<string>();
        var operations = ProtectedOperation.Section24.Concat(RouteAttackCatalog.Cases.Select(c => c.Operation)).Distinct();
        foreach (var operation in operations)
        {
            var routes = RouteAttackCatalog.Cases.Where(c => c.Operation == operation && c.Probes.Count > 0).ToList();
            var scenarios = Scenarios.GetValueOrDefault(operation) ?? [];
            var routesText = routes.Count == 0 ? "—" : string.Join("<br>", routes.Select(r => $"`{r.Key}` ({r.Probes.Count})"));
            var built = operation != ProtectedOperation.ProductionInclusion
                || patterns.Any(p => p.Contains("/productions", StringComparison.Ordinal));
            var scenariosText = scenarios.Length == 0 ? (built ? "—" : "not built yet (E12): no route to attack") : string.Join("<br>", scenarios);
            report.Append(CultureInfoInvariant($"| {operation} | {(ProtectedOperation.Section24.Contains(operation) ? "yes" : "")} | {routesText} | {scenariosText} |\n"));
            if (built && routes.Count + scenarios.Length == 0)
            {
                missing.Add(operation);
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
        TestContext.Current.AddAttachment("security-attack-coverage.md", report.ToString());
        missing.Should().BeEmpty("every protected operation has at least one negative test");
    }

    private static string CultureInfoInvariant(FormattableString value) => FormattableString.Invariant(value);
}
