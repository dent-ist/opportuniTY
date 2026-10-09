using System.Net;
using System.Text;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Keys;
using Opportunity.Application.Storage;
using Opportunity.Core.Security;
using Opportunity.Data.Keys;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Authorization;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Security.Keys;
using Opportunity.Storage;
using Opportunity.Storage.Encryption;
using Opportunity.Storage.FileSystem;

namespace Opportunity.IntegrationTests.Content;

/// <summary>
/// E05-T09 through the real API host: with <c>ObjectStorage:Encryption:Mode=Envelope</c> the protected-content gateway
/// streams decrypted natives (whole and by range) and text, while the store on disk holds ciphertext only.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class EnvelopeContentDeliveryTests(MigrationPostgresFixture postgres) : IDisposable
{
    private readonly InMemoryAuditEventWriter _audit = new();
    private readonly string _root = Directory.CreateTempSubdirectory("opportunity-envelope-api-").FullName;
    private readonly string _keys = Directory.CreateTempSubdirectory("opportunity-envelope-api-keys-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_keys, recursive: true);
    }

    [Fact]
    public async Task The_gateway_serves_decrypted_natives_ranges_and_text_from_an_envelope_encrypted_store()
    {
        await using var security = await AuthorizationDatabase.CreateAsync(postgres);
        var store = new EnvelopeObjectStore(
            new FileSystemObjectStore(new FileSystemObjectStoreOptions { RootPath = _root }),
            new WorkspaceKeyService(
                new WorkspaceDataKeyStore(security.Core.AppDataSource),
                new LocalKeyEncryptionKeyProvider(new LocalKeyStoreOptions { KeyDirectory = _keys }, new EnvironmentSecretProvider(_ => null)),
                new WorkspaceKeyOptions(),
                TimeProvider.System));
        var db = ContentDatabase.Over(security, _root, store);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var manager = await db.Security.CreateUserAsync();
        await db.Security.AssignAsync(ws, WorkspaceRole.ProductionManager, manager);
        var native = new byte[(3 * 1024 * 1024) + 17];
        new Random(54).NextBytes(native);
        var text = Encoding.UTF8.GetBytes("Synthetic extracted text for the envelope test. " + new string('a', 5000));
        var documentId = await db.ArtifactDocumentAsync(ws, text, native);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var path = $"/api/v1/workspaces/{ws}/documents/{documentId}";

        using var whole = await ProtectedContentGatewayTests.GetAsync(client, $"{path}/native", manager);
        using var range = await ProtectedContentGatewayTests.GetAsync(client, $"{path}/native", manager, range: "bytes=1000000-2100000");
        using var chunk = await ProtectedContentGatewayTests.GetAsync(client, $"{path}/text/chunks/0", manager);

        whole.StatusCode.Should().Be(HttpStatusCode.OK);
        (await whole.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(native);
        range.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await range.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(native[1_000_000..2_100_001]);
        range.Content.Headers.ContentRange!.ToString().Should().Be($"bytes 1000000-2100000/{native.Length}");
        chunk.StatusCode.Should().Be(HttpStatusCode.OK);
        (await chunk.Content.ReadAsStringAsync(Ct)).Should().Contain("Synthetic extracted text for the envelope test.");

        var onDisk = await File.ReadAllBytesAsync(Path.Combine(_root, "objects", ObjectKeys.Native(ws, documentId, Sha256Digest.Compute(native)).Value), Ct);
        onDisk.AsSpan().IndexOf(native.AsSpan(0, 64)).Should().Be(-1, "the provider holds ciphertext only");
        (await db.Security.Core.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.workspace_data_key WHERE workspace_id = @ws AND state = 1", ("ws", ws))).Should().Be(1);
    }

    private WebApplicationFactory<Program> Factory(ContentDatabase db) =>
        new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:App", db.Security.Core.AppConnectionString);
            builder.UseSetting("ObjectStorage:Provider", "FileSystem");
            builder.UseSetting("ObjectStorage:FileSystem:RootPath", _root);
            builder.UseSetting("ObjectStorage:Encryption:Mode", "Envelope");
            builder.UseSetting("KeyManagement:Local:KeyDirectory", _keys);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecurityStateReader>();
                services.AddSingleton<ISecurityStateReader, Data.Security.PostgresSecurityStateReader>();
                services.AddSingleton<IAuditEventWriter>(_audit);
            });
        });
}
