using System.Text.RegularExpressions;

using AwesomeAssertions;

using Opportunity.Messaging;
using Opportunity.UnitTests.Security;

using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Opportunity.UnitTests.Operations;

/// <summary>
/// E19-T06: the hardened Lite profile (compose.yaml) and the Full profile (compose.full.yaml). Lite: no secret has a
/// default or a literal value (init generates every one), every port is on 127.0.0.1, memory limits fit the 8 GB minimum
/// (Q-39) and the web container offers a local TLS listener. Full: secrets only as Docker secrets, TLS on every store
/// link, one scalable container per worker type, and a distinct database login, broker user, OpenSearch user and
/// object-storage identity per component (ADR-015 D9.6, D10.1, D14). <c>docker compose config</c> itself runs in CI.
/// </summary>
public sealed partial class ComposeProfileTests
{
    private static readonly string ComposeDirectory = Path.Combine(PermissionMatrixTests.RepositoryRoot(), "deploy", "docker-compose");

    /// <summary>WorkerTypes.All (Opportunity.Hosting): every type runs in exactly one Full worker service.</summary>
    private static readonly string[] WorkerTypes = ["import", "indexing", "rendering", "export", "production", "dispatcher", "bulk-coding"];

    private static readonly string[] FullProfile = ["full"];

    private static readonly string[] AppServices =
        ["api", "worker-dispatcher", "worker-import", "worker-index", "worker-render", "worker-export", "worker-production", "worker-bulkcoding"];

    // --- Lite -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Lite_secrets_have_neither_defaults_nor_literal_values_and_init_generates_each()
    {
        var text = File.ReadAllText(Path.Combine(ComposeDirectory, "compose.yaml"));
        SecretDefault().Matches(text).Select(m => m.Value).Should().BeEmpty("a secret with a default would start with a known value");

        var env = File.ReadAllText(Path.Combine(ComposeDirectory, ".env.example"));
        var required = RequiredSecret().Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
        required.Should().NotBeEmpty();
        foreach (var name in required)
        {
            env.Should().Contain($"\n{name}=\n", "./opportunity.sh init generates {0} from an empty line in .env.example", name);
        }

        foreach (var (service, key, value) in Environments(Services("compose.yaml")))
        {
            if (SecretKey().IsMatch(key))
            {
                value.Should().MatchRegex(@"^\$\{[A-Z0-9_]+:\?", "{0}.{1} must come from .env, never a literal", service, key);
            }

            foreach (Match password in PasswordInValue().Matches(value))
            {
                password.Groups[1].Value.Should().StartWith("${", "{0}.{1} embeds a literal password", service, key);
            }
        }
    }

    [Fact]
    public void Lite_publishes_ports_on_localhost_only_and_fits_the_8_GB_minimum()
    {
        var services = Services("compose.yaml");
        foreach (var (name, service) in services)
        {
            if (service.TryGetValue("ports", out var ports))
            {
                foreach (var port in (List<object>)ports)
                {
                    ((string)port).Should().StartWith("${OPPORTUNITY_BIND:-127.0.0.1}:", "{0} publishes {1}", name, port);
                }
            }
        }

        // Every long-running service of the default stack has a memory limit, and together they fit the minimum (the
        // one-shot migrator exits before api and worker start).
        var resident = services.Where(s => !s.Value.ContainsKey("profiles") && !Equals(s.Value.GetValueOrDefault("restart"), "no")).ToList();
        resident.Should().NotBeEmpty();
        long total = 0;
        foreach (var (name, service) in resident)
        {
            service.Should().ContainKey("mem_limit", "{0} needs a memory limit (Q-39: 8 GB minimum with the combined worker)", name);
            total += Bytes((string)service["mem_limit"]);
        }

        total.Should().BeLessThanOrEqualTo(8L * 1024 * 1024 * 1024, "the default Lite stack must fit an 8 GB machine");
        Environment(services, "worker")["Workers__Enabled"].Should().Be("all", "Lite runs the combined worker");
    }

    [Fact]
    public void Lite_web_serves_local_TLS_from_the_init_generated_certificate()
    {
        var web = (Dictionary<object, object>)Services("compose.yaml")["web"];
        ((List<object>)web["volumes"]).Cast<string>().Should().Contain("./tls/web:/etc/nginx/tls:ro");
        ((List<object>)web["ports"]).Cast<string>().Should().Contain(p => p.EndsWith(":8443", StringComparison.Ordinal));
        File.ReadAllText(Path.Combine(ComposeDirectory, "web", "api-proxy.conf")).Should().Contain("include /etc/nginx/tls/*.conf;");
        var script = File.ReadAllText(Path.Combine(ComposeDirectory, "opportunity.sh"));
        script.Should().Contain("listen 8443 ssl;").And.Contain("make_cert Lite server localhost");
    }

    // --- Full -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Full_services_are_opt_in_and_secrets_never_travel_as_environment_values()
    {
        var text = File.ReadAllText(Path.Combine(ComposeDirectory, "compose.full.yaml"));
        RequiredSecret().Matches(text).Should().BeEmpty("Full secrets are Docker secrets, not .env values (ADR-015 D10.1)");
        SecretDefault().Matches(text).Should().BeEmpty();
        text.Should().NotContain("Password=").And.NotContain("DISABLE_SECURITY_PLUGIN");

        var services = Services("compose.full.yaml");
        var secrets = ((Dictionary<object, object>)Document("compose.full.yaml")["secrets"]).Keys.Cast<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var (name, service) in services)
        {
            ((List<object>)service["profiles"]).Cast<string>().Should().ContainSingle()
                .Which.Should().BeOneOf(["full", "observability"], "{0} must not start without the profile", name);
            if (service.TryGetValue("secrets", out var used))
            {
                secrets.Should().Contain(((List<object>)used).Cast<string>(), "{0} uses undeclared secrets", name);
            }
        }

        foreach (var (service, key, value) in Environments(services))
        {
            if (SecretKey().IsMatch(key) && !key.EndsWith("_FILE", StringComparison.Ordinal) && key != "Secrets__AllowEnvironment")
            {
                Assert.Fail($"{service}.{key} carries a secret as a plain variable; use {key}_FILE and a Docker secret");
            }

            if (key.EndsWith("_FILE", StringComparison.Ordinal) && SecretKey().IsMatch(key))
            {
                value.Should().StartWith("/run/secrets/", "{0}.{1}", service, key);
            }
        }

        foreach (var name in AppServices)
        {
            Environment(services, name)["Secrets__AllowEnvironment"].Should().Be("false", name);
        }
    }

    [Fact]
    public void Full_runs_one_scalable_service_per_worker_type_and_the_api_behind_the_web_edge()
    {
        var services = Services("compose.full.yaml");
        var workers = services.Where(s => s.Key.StartsWith("worker-", StringComparison.Ordinal)).ToList();
        workers.Select(w => Environment(services, w.Key)["Workers__Enabled"]).Should().BeEquivalentTo(WorkerTypes);
        services.Should().ContainKey("worker-index", "`--scale worker-index=N` is the documented way to add index consumers");

        foreach (var (name, service) in workers.Append(services.Single(s => s.Key == "api")))
        {
            service.Should().NotContainKey("container_name", "{0} must be scalable", name);
            service.Should().NotContainKey("ports", "{0} is reached through the edge or not at all", name);
            service.Should().NotContainKey("hostname", name);
        }

        // Only the web edge publishes a port to the network; observability UIs stay on loopback.
        foreach (var (name, service) in services.Where(s => s.Value.ContainsKey("ports")))
        {
            foreach (var port in ((List<object>)service["ports"]).Cast<string>())
            {
                if (name != "web")
                {
                    port.Should().StartWith("127.0.0.1:", "{0}", name);
                }
            }
        }

        Environment(services, "api")["ASPNETCORE_URLS"].Should().Contain("https://+:8443");
        File.ReadAllText(Path.Combine(ComposeDirectory, "full", "web", "api-proxy.conf"))
            .Should().Contain("set $opportunity_api api:8443;").And.Contain("proxy_ssl_verify on;");
    }

    [Fact]
    public void Full_gives_every_component_its_own_least_privilege_credentials()
    {
        var services = Services("compose.full.yaml");
        var logins = new List<string>();
        var brokers = new List<string>();
        var searchUsers = new List<string>();
        var storage = new List<string>();
        foreach (var name in AppServices)
        {
            var env = Environment(services, name);
            var user = DatabaseUser().Match(env["ConnectionStrings__App"]).Groups[1].Value;
            user.Should().MatchRegex("^opportunity_(api|worker_[a-z]+)$", name);
            env["ConnectionStrings__App"].Should().Contain($"Passfile=/run/secrets/pgpass-{user.Replace("opportunity_", string.Empty, StringComparison.Ordinal).Replace('_', '-')};");
            logins.Add(user);
            if (env.TryGetValue("ConnectionStrings__RabbitMq_FILE", out var broker))
            {
                brokers.Add(broker);
            }

            if (env.TryGetValue("OpenSearch__Username", out var searchUser))
            {
                searchUsers.Add(searchUser);
            }

            if (env.TryGetValue("ObjectStorage__S3__AccessKey", out var key))
            {
                storage.Add(key);
            }
        }

        logins.Should().OnlyHaveUniqueItems().And.HaveCount(AppServices.Length);
        brokers.Should().OnlyHaveUniqueItems().And.HaveCount(AppServices.Length - 1, "every worker has its own broker user; the API has none");
        Environment(services, "api").Keys.Should().NotContain(k => k.Contains("RabbitMq", StringComparison.Ordinal), "the API's work goes through the outbox");
        searchUsers.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(["opportunity-api", "opportunity-dispatcher", "opportunity-indexer"],
            "only the api, the dispatcher and the index workers talk to OpenSearch");
        storage.Should().OnlyHaveUniqueItems();
        Environment(services, "worker-dispatcher").Keys.Should().NotContain(k => k.StartsWith("ObjectStorage__", StringComparison.Ordinal));
        Environment(services, "worker-bulkcoding").Keys.Should().NotContain(k => k.StartsWith("ObjectStorage__", StringComparison.Ordinal));

        // The owner login and the broker administrator belong to the one-shot migrator only.
        Environment(services, "migrator")["ConnectionStrings__Migrator"].Should().Contain("Username=opportunity_owner;");
        Environment(services, "migrator")["ConnectionStrings__RabbitMq_FILE"].Should().Be("/run/secrets/amqp-admin");

        // Every login is created by the init script, every broker user by users.sh, every OpenSearch user is mapped to
        // a role, and every S3 identity is written by the gateway entrypoint.
        var logins30 = File.ReadAllText(Path.Combine(ComposeDirectory, "postgres", "init", "30-component-logins.sh"));
        logins30.Should().Contain("for type in DISPATCHER IMPORT INDEX RENDER EXPORT PRODUCTION BULKCODING; do");
        foreach (var login in logins.Where(l => l.StartsWith("opportunity_worker_", StringComparison.Ordinal)))
        {
            logins30.Should().Contain(login["opportunity_worker_".Length..].ToUpperInvariant(), login);
        }

        var script = File.ReadAllText(Path.Combine(ComposeDirectory, "opportunity.sh"));
        foreach (var area in RabbitMqPermissions.Areas.Append("dispatcher"))
        {
            brokers.Should().Contain($"/run/secrets/amqp-{area}");
        }

        script.Should().Contain("\"amqps://opportunity-$type:").And.Contain("@rabbitmq:5671/\"");
        var mapping = File.ReadAllText(Path.Combine(ComposeDirectory, "full", "opensearch", "roles_mapping.yml"));
        foreach (var user in searchUsers.Append("opportunity-migrator").Append("opportunity-monitor"))
        {
            mapping.Should().Contain($"users: [\"{user}\"]", user);
        }

        var gateway = File.ReadAllText(Path.Combine(ComposeDirectory, "full", "seaweedfs", "run.sh"));
        foreach (var key in storage)
        {
            gateway.Should().Contain(key["opportunity-".Length..], key);
        }
    }

    [Fact]
    public void Full_uses_TLS_on_every_store_link_and_runs_clustered_stores()
    {
        var services = Services("compose.full.yaml");
        foreach (var (service, key, value) in Environments(services))
        {
            if (key.StartsWith("ConnectionStrings__", StringComparison.Ordinal) && value.StartsWith("Host=", StringComparison.Ordinal))
            {
                value.Should().Contain("SSL Mode=VerifyFull;").And.Contain("Root Certificate=", "{0}.{1}", service, key);
            }

            if (key is "ConnectionStrings__OpenSearch" or "ObjectStorage__S3__ServiceUrl")
            {
                value.Should().StartWith("https://", "{0}.{1}", service, key);
            }
        }

        var hba = File.ReadAllLines(Path.Combine(ComposeDirectory, "full", "postgres", "pg_hba.conf")).Where(l => !l.StartsWith('#') && l.Trim().Length > 0);
        hba.Should().OnlyContain(l => l.StartsWith("local", StringComparison.Ordinal) || l.StartsWith("hostssl", StringComparison.Ordinal),
            "PostgreSQL accepts TCP clients over TLS only");
        File.ReadAllText(Path.Combine(ComposeDirectory, "full", "rabbitmq", "rabbitmq.conf"))
            .Should().Contain("listeners.tcp = none").And.Contain("listeners.ssl.default = 5671");
        var openSearch = File.ReadAllText(Path.Combine(ComposeDirectory, "full", "opensearch", "opensearch.yml"));
        openSearch.Should().Contain("plugins.security.ssl.http.enabled: true")
            .And.Contain("plugins.security.ssl.transport.enforce_hostname_verification: true")
            .And.Contain("cluster.initial_cluster_manager_nodes: [\"opensearch-1\", \"opensearch-2\", \"opensearch-3\"]")
            .And.NotContain("allow_unsafe_democertificates: true");

        services.Keys.Should().Contain(["opensearch-1", "opensearch-2", "opensearch-3", "rabbitmq-1", "rabbitmq-2", "rabbitmq-3",
            "postgres", "postgres-replica", "postgres-exporter-replica", "filer-postgres", "seaweedfs-master-1", "seaweedfs-master-2",
            "seaweedfs-master-3", "seaweedfs-volume-1", "seaweedfs-volume-2", "seaweedfs-filer", "seaweedfs-s3", "data-lb"]);
        Environment(services, "api")["OpenSearch__Replicas"].Should().Be("1", "losing one of three nodes must lose no shard");
        ((List<object>)services["postgres-exporter-replica"]["profiles"]).Cast<string>().Should().Equal(
            FullProfile, "replication lag is exported whenever Full runs (pg_replication_lag_seconds)");
        File.ReadAllText(Path.Combine(ComposeDirectory, "full", "data-lb", "nginx.conf"))
            .Should().Contain("server opensearch-1:9200 resolve").And.Contain("server rabbitmq-3:5671 resolve");
    }

    // --- Helpers ----------------------------------------------------------------------------------------------------

    private static Dictionary<object, object> Document(string file)
    {
        using var reader = new StringReader(File.ReadAllText(Path.Combine(ComposeDirectory, file)));
        return new DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(new MergingParser(new Parser(reader)));
    }

    private static Dictionary<string, Dictionary<object, object>> Services(string file) =>
        ((Dictionary<object, object>)Document(file)["services"])
            .ToDictionary(s => (string)s.Key, s => (Dictionary<object, object>)s.Value, StringComparer.Ordinal);

    private static Dictionary<string, string> Environment(Dictionary<string, Dictionary<object, object>> services, string service) =>
        services[service].TryGetValue("environment", out var environment)
            ? ((Dictionary<object, object>)environment).ToDictionary(e => (string)e.Key, e => ((string?)e.Value ?? string.Empty).Trim(), StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

    private static IEnumerable<(string Service, string Key, string Value)> Environments(Dictionary<string, Dictionary<object, object>> services) =>
        services.Keys.SelectMany(name => Environment(services, name).Select(e => (name, e.Key, e.Value)));

    private static long Bytes(string value)
    {
        var literal = value.StartsWith("${", StringComparison.Ordinal) ? value[(value.IndexOf(":-", StringComparison.Ordinal) + 2)..^1] : value;
        var unit = char.ToLowerInvariant(literal[^1]);
        var number = long.Parse(literal[..^1], System.Globalization.CultureInfo.InvariantCulture);
        return unit switch
        {
            'g' => number * 1024 * 1024 * 1024,
            'm' => number * 1024 * 1024,
            _ => throw new InvalidOperationException($"unexpected memory value {value}"),
        };
    }

    /// <summary>An interpolation of a secret variable with a default, e.g. <c>${S3_SECRET_KEY:-}</c>.</summary>
    [GeneratedRegex(@"\$\{[A-Z0-9_]*(PASSWORD|SECRET)[A-Z0-9_]*:?-[^}]*\}")]
    private static partial Regex SecretDefault();

    /// <summary>A required secret variable, e.g. <c>${POSTGRES_PASSWORD:?…}</c>.</summary>
    [GeneratedRegex(@"\$\{([A-Z0-9_]*(?:PASSWORD|SECRET_KEY))\:\?")]
    private static partial Regex RequiredSecret();

    /// <summary>Environment keys that hold a secret (access keys, key ids and key directories are not secrets).</summary>
    [GeneratedRegex(@"(?i)(password|secret(?!s__allowenvironment)|_pass$|hashkey|hmac)")]
    private static partial Regex SecretKey();

    [GeneratedRegex(@"(?:Password=|://(?:\$\{[^}]+\}|[^:/@$]+):)(\$\{[^}]+\}|[^;@]+)[;@]")]
    private static partial Regex PasswordInValue();

    [GeneratedRegex(@"Username=([^;]+)")]
    private static partial Regex DatabaseUser();
}
