using System.Text.RegularExpressions;

using AwesomeAssertions;

using Opportunity.Messaging;
using Opportunity.UnitTests.Security;

using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Opportunity.UnitTests.Operations;

/// <summary>
/// E05-T07 (threat T-37): no shared or superuser credentials in the Compose profile. Each runtime component connects to
/// PostgreSQL and RabbitMQ with its own login; none uses the PostgreSQL superuser, the database owner or the broker's
/// administrator (those belong to the one-shot migrator); the broker users' permissions in rabbitmq/users.sh are
/// exactly <see cref="RabbitMqPermissions"/>; envelope signing is on with its key from .env.
/// </summary>
public sealed partial class ComposeCredentialTests
{
    private static readonly string ComposeDirectory = Path.Combine(PermissionMatrixTests.RepositoryRoot(), "deploy", "docker-compose");

    private static readonly string[] AdminUsers = ["postgres", "${OPPORTUNITY_DB_OWNER_USER", "${RABBITMQ_USER", "${POSTGRES_USER", "guest"];

    [Fact]
    public void Runtime_services_never_use_a_superuser_owner_or_broker_administrator_credential()
    {
        var services = Services();
        foreach (var name in new[] { "api", "worker", "web", "postgres-exporter" })
        {
            var environment = Environment(services, name);
            foreach (var (key, value) in environment)
            {
                foreach (var user in Usernames(value))
                {
                    AdminUsers.Should().NotContain(a => user.StartsWith(a, StringComparison.Ordinal), "{0}.{1} uses {2}", name, key, user);
                }

                value.Should().NotContain("RABBITMQ_PASSWORD}", "{0}.{1}: the broker administrator's password is the migrator's", name, key);
                value.Should().NotContain("POSTGRES_PASSWORD", "{0}.{1}", name, key);
                value.Should().NotContain("OPPORTUNITY_DB_OWNER_PASSWORD", "{0}.{1}", name, key);
            }
        }

        // The migrator is the only holder of the owner login and the broker administrator.
        Environment(services, "migrator")["ConnectionStrings__Migrator"].Should().Contain("Username=${OPPORTUNITY_DB_OWNER_USER");
        Environment(services, "migrator")["ConnectionStrings__RabbitMq"].Should().Contain("${RABBITMQ_USER");
    }

    [Fact]
    public void Api_worker_and_exporter_have_distinct_database_logins_and_the_api_no_broker_user()
    {
        var services = Services();
        var api = Environment(services, "api");
        var worker = Environment(services, "worker");
        var exporter = Environment(services, "postgres-exporter");

        var logins = new[]
        {
            DatabaseUser(api["ConnectionStrings__App"]),
            DatabaseUser(worker["ConnectionStrings__App"]),
            exporter["DATA_SOURCE_USER"],
        };
        logins.Should().OnlyHaveUniqueItems().And.Equal(
            "${OPPORTUNITY_DB_API_USER:-opportunity_api}", "${OPPORTUNITY_DB_WORKER_USER:-opportunity_worker}", "${OPPORTUNITY_DB_MONITOR_USER:-opportunity_monitor}");
        api.Keys.Should().NotContain(k => k.Contains("RabbitMq", StringComparison.Ordinal), "the API's work goes through the outbox");

        // Every postgres login the services use is created by the init script.
        var script = File.ReadAllText(Path.Combine(ComposeDirectory, "postgres", "init", "30-component-logins.sh"));
        script.Should().Contain("OPPORTUNITY_DB_API_USER:-opportunity_api").And.Contain("OPPORTUNITY_DB_WORKER_USER:-opportunity_worker")
            .And.Contain("OPPORTUNITY_DB_MONITOR_USER:-opportunity_monitor").And.Contain("NOSUPERUSER").And.Contain("NOBYPASSRLS");
    }

    [Fact]
    public void The_worker_uses_the_dispatcher_user_for_work_and_one_user_per_queue_area_with_signing_on()
    {
        var worker = Environment(Services(), "worker");
        Users(worker["ConnectionStrings__RabbitMq"]).Should().Equal("opportunity-dispatcher");
        foreach (var area in RabbitMqPermissions.Areas)
        {
            worker.Should().ContainKey($"Messaging__RabbitMq__AreaConnectionStrings__{area}");
            Users(worker[$"Messaging__RabbitMq__AreaConnectionStrings__{area}"]).Should().Equal($"opportunity-{area}");
        }

        worker.Keys.Where(k => k.StartsWith("Messaging__RabbitMq__AreaConnectionStrings__", StringComparison.Ordinal))
            .Should().HaveCount(RabbitMqPermissions.Areas.Count);
        worker["Messaging__RabbitMq__Signing__Enabled"].Should().Be("${OPPORTUNITY_ENVELOPE_SIGNING:-true}");
        worker["OPPORTUNITY_SECRET_ENVELOPE_HMAC_K1"].Should().StartWith("${ENVELOPE_HMAC_SECRET_KEY:?", "the key comes from .env, never a literal");
        File.ReadAllText(Path.Combine(ComposeDirectory, ".env.example")).Should().Contain("\nENVELOPE_HMAC_SECRET_KEY=\n");
    }

    [Fact]
    public void The_api_hashes_audit_sessions_with_a_key_from_env_that_init_generates_long_enough()
    {
        // E14-T02: without the key audit events carry no session hash (ADR-013 §4).
        Environment(Services(), "api")["Authentication__Session__AuditHashKey"]
            .Should().StartWith("${AUDIT_SESSION_HASH_SECRET_KEY:?", "the key comes from .env, never a literal");
        File.ReadAllText(Path.Combine(ComposeDirectory, ".env.example")).Should().Contain("\nAUDIT_SESSION_HASH_SECRET_KEY=\n");
        File.ReadAllText(Path.Combine(ComposeDirectory, "opportunity.sh")).Should().Contain("*_HASH_SECRET_KEY ]] && length=44",
            "44 base64 digits decode to the 32 bytes the options validator requires");
    }

    [Fact]
    public void The_broker_user_script_grants_exactly_the_code_permissions()
    {
        var lines = File.ReadAllLines(Path.Combine(ComposeDirectory, "rabbitmq", "users.sh"))
            .Select(l => UserLine().Match(l))
            .Where(m => m.Success)
            .ToDictionary(m => m.Groups["name"].Value, m => new RabbitMqPermissions(m.Groups["c"].Value, m.Groups["w"].Value, m.Groups["r"].Value), StringComparer.Ordinal);

        lines.Keys.Should().BeEquivalentTo(RabbitMqPermissions.Areas.Append("dispatcher"));
        lines["dispatcher"].Should().Be(RabbitMqPermissions.Dispatcher);
        foreach (var area in RabbitMqPermissions.Areas)
        {
            lines[area].Should().Be(RabbitMqPermissions.ForArea(area), "users.sh and RabbitMqPermissions disagree for {0}", area);
        }

        var env = File.ReadAllText(Path.Combine(ComposeDirectory, ".env.example"));
        foreach (var name in lines.Keys)
        {
            env.Should().Contain($"\nRABBITMQ_{name.ToUpperInvariant()}_PASSWORD=\n", "init generates the {0} password", name);
        }
    }

    private static Dictionary<object, object> Services()
    {
        using var reader = new StringReader(File.ReadAllText(Path.Combine(ComposeDirectory, "compose.yaml")));
        var document = new DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(new MergingParser(new Parser(reader)));
        return (Dictionary<object, object>)document["services"];
    }

    private static Dictionary<string, string> Environment(Dictionary<object, object> services, string service) =>
        ((Dictionary<object, object>)services[service]).TryGetValue("environment", out var environment)
            ? ((Dictionary<object, object>)environment).ToDictionary(e => (string)e.Key, e => ((string?)e.Value ?? string.Empty).Trim(), StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

    private static string DatabaseUser(string connectionString) => DatabaseUserPattern().Match(connectionString).Groups[1].Value;

    private static IEnumerable<string> Usernames(string value) =>
        DatabaseUserPattern().Matches(value).Select(m => m.Groups[1].Value).Concat(Users(value));

    private static List<string> Users(string value) => [.. AmqpUserPattern().Matches(value).Select(m => m.Groups[1].Value)];

    [GeneratedRegex(@"Username=([^;]+)")]
    private static partial Regex DatabaseUserPattern();

    [GeneratedRegex(@"amqps?://([^:@]+):")]
    private static partial Regex AmqpUserPattern();

    [GeneratedRegex(@"^user (?<name>[a-z]+) ""\$\{RABBITMQ_[A-Z]+_PASSWORD:-\}"" '(?<c>[^']*)' '(?<w>[^']*)' '(?<r>[^']*)'$")]
    private static partial Regex UserLine();
}
