using System.Security.Cryptography;
using System.Text;

using AwesomeAssertions;

using Microsoft.Extensions.Time.Testing;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Content;
using Opportunity.Application.Storage;
using Opportunity.Core.Security;
using Opportunity.Security.Authorization;

namespace Opportunity.UnitTests.Security;

/// <summary>
/// E05-T04 access service over the real PDP: one <c>Document.*</c> audit event per attempt (no extra
/// <c>AuthZ.Denied</c>), distinct permissions for view, print and native download, break-glass read-only, quarantine,
/// registry integrity checks and range resolution.
/// </summary>
public sealed class DocumentAccessServiceTests
{
    private static readonly Guid Workspace = Guid.CreateVersion7();
    private static readonly Guid Document = Guid.CreateVersion7();
    private static readonly Guid Wall = Guid.CreateVersion7();
    private static readonly SecurityPrincipal Alice = new() { UserId = Guid.CreateVersion7(), DisplayName = "Alice", CorrelationId = "corr-7" };

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryAuditEventWriter _audit = new();
    private readonly FakeReader _reader = new();
    private readonly FakeCatalog _catalog = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Allowed_view_is_audited_once_as_retrieved_before_the_grant_and_never_carries_the_key()
    {
        Arrange([WorkspaceRole.Reviewer]);
        _catalog.Location = Location(ObjectKeys.Text(Workspace, Document, Digest("text")), "text/plain; charset=utf-8");

        var result = await Service().OpenAsync(Alice, new ContentRequest(Workspace, Document, ContentRendition.Text, ContentPurpose.Display), ObjectDeliveryMode.Stream, Ct);

        result.Outcome.Should().Be(ContentAccessOutcome.Granted);
        var grant = result.Grant!;
        grant.DeliveryMode.Should().Be(ObjectDeliveryMode.Stream);
        grant.Key.Should().Be(ObjectKeys.Text(Workspace, Document, Digest("text")));
        grant.DownloadFileName.Should().BeNull();
        var e = _audit.Events.Should().ContainSingle().Subject;
        e.EventId.Should().Be(grant.AuditEventId);
        (e.Category, e.Action, e.Outcome, e.ResourceType, e.ResourceId).Should().Be(("Document", "Retrieved", AuditOutcome.Success, "Document", Document.ToString()));
        e.Details.Should().Contain(new Dictionary<string, string?>
        {
            ["rendition"] = "Text",
            ["purpose"] = "Display",
            ["permission"] = "Document.View",
            ["objectId"] = _catalog.Location.ObjectId.ToString(),
            ["deliveryMode"] = "Stream",
        });
        e.Details.Values.Should().NotContain(v => v != null && v.Contains("ws/", StringComparison.Ordinal), "object keys stay out of audit");
        e.CorrelationId.Should().Be("corr-7");
        e.AccessPath.Should().Be(AuditAccessPath.Normal);
    }

    [Fact]
    public async Task Prefetch_is_audited_with_its_own_purpose()
    {
        Arrange([WorkspaceRole.Reviewer]);
        _catalog.Location = Location(ObjectKeys.Rendition(Workspace, Document, Guid.NewGuid(), "p000001.thumb.webp"), "image/webp");

        await Service().OpenAsync(Alice, new ContentRequest(Workspace, Document, ContentRendition.Thumbnail, ContentPurpose.Prefetch, 1), ObjectDeliveryMode.Stream, Ct);

        var e = _audit.Events.Should().ContainSingle().Subject;
        e.Action.Should().Be("Retrieved");
        e.Details["purpose"].Should().Be("Prefetch");
        e.Details["page"].Should().Be("1");
    }

    [Theory]
    [InlineData(ContentRendition.Native, ContentPurpose.Download, "NativeDownloaded", "Document.DownloadNative")]
    [InlineData(ContentRendition.PageImage, ContentPurpose.Print, "Printed", "Document.Print")]
    public async Task Download_and_print_need_their_own_permission_which_a_reviewer_lacks(
        ContentRendition rendition, ContentPurpose purpose, string action, string permission)
    {
        Arrange([WorkspaceRole.Reviewer]);

        var result = await Service().OpenAsync(Alice, new ContentRequest(Workspace, Document, rendition, purpose, 1), ObjectDeliveryMode.Stream, Ct);

        result.Outcome.Should().Be(ContentAccessOutcome.Denied);
        result.Decision.Outcome.Should().Be(AuthorizationOutcome.Deny);
        _catalog.Calls.Should().Be(0, "nothing is looked up for a denied request");
        var e = _audit.Events.Should().ContainSingle("the denial is the action's own event, not an extra AuthZ.Denied").Subject;
        (e.Category, e.Action, e.Outcome, e.ReasonCode).Should().Be(("Document", action, AuditOutcome.Denied, AuthorizationReasons.PermissionNotGranted));
        e.Details["permission"].Should().Be(permission);
        e.ResourceId.Should().Be(Document.ToString());
    }

    [Fact]
    public async Task Production_manager_downloads_a_native_with_a_sanitized_file_name_and_may_presign()
    {
        Arrange([WorkspaceRole.ProductionManager]);
        _catalog.ControlNumber = "ABC/0001\"x";
        _catalog.FileExtension = ".MSG";
        _catalog.Location = Location(ObjectKeys.Native(Workspace, Document, Digest("native")), "application/octet-stream");

        var result = await Service().OpenAsync(Alice, new ContentRequest(Workspace, Document, ContentRendition.Native, ContentPurpose.Download), ObjectDeliveryMode.Presign, Ct);

        result.Grant!.DeliveryMode.Should().Be(ObjectDeliveryMode.Presign);
        result.Grant.DownloadFileName.Should().Be("ABC_0001_x.msg");
        _audit.Events.Should().ContainSingle().Which.Details["deliveryMode"].Should().Be("Presign");
    }

    [Fact]
    public async Task Viewer_renditions_are_always_streamed_even_when_the_store_presigns()
    {
        Arrange([WorkspaceRole.Reviewer]);
        _catalog.Location = Location(ObjectKeys.Rendition(Workspace, Document, Guid.NewGuid(), "p000001.png"), "image/png");

        var result = await Service().OpenAsync(Alice, new ContentRequest(Workspace, Document, ContentRendition.PageImage, ContentPurpose.Display, 1), ObjectDeliveryMode.Presign, Ct);

        result.Grant!.DeliveryMode.Should().Be(ObjectDeliveryMode.Stream);
    }

    [Theory]
    [InlineData(false, AuthorizationReasons.EthicalWall)]
    [InlineData(true, AuthorizationReasons.RestrictionClass)]
    public async Task Walled_or_restricted_documents_are_not_found_and_audited_with_the_true_reason(bool restricted, string reason)
    {
        Arrange([WorkspaceRole.WorkspaceAdmin, WorkspaceRole.Reviewer], walls: restricted ? [] : [Wall]);
        _reader.Documents[Document] = restricted
            ? PolicyEvaluatorTests.Doc(classes: ["Undefined"])
            : PolicyEvaluatorTests.Doc(walls: [Wall]);

        var result = await Service().OpenAsync(Alice, new ContentRequest(Workspace, Document, ContentRendition.Text, ContentPurpose.Display), ObjectDeliveryMode.Stream, Ct);

        result.Decision.Outcome.Should().Be(AuthorizationOutcome.NotFound);
        _catalog.Calls.Should().Be(0);
        _audit.Events.Should().ContainSingle().Which.ReasonCode.Should().Be(reason);
    }

    [Fact]
    public async Task Break_glass_views_walled_documents_on_the_break_glass_path_but_never_downloads()
    {
        _reader.State = PolicyEvaluatorTests.State([WorkspaceRole.BreakGlass], walls: [Wall]) with { BreakGlassExpiresAt = _time.GetUtcNow().AddMinutes(60) };
        _reader.Documents[Document] = PolicyEvaluatorTests.Doc(walls: [Wall]);
        _catalog.Location = Location(ObjectKeys.Text(Workspace, Document, Digest("text")), "text/plain; charset=utf-8");
        var service = Service();

        var view = await service.OpenAsync(Alice, new ContentRequest(Workspace, Document, ContentRendition.Text, ContentPurpose.Display), ObjectDeliveryMode.Stream, Ct);
        _catalog.Location = Location(ObjectKeys.Native(Workspace, Document, Digest("native")), "application/octet-stream");
        var download = await service.OpenAsync(Alice, new ContentRequest(Workspace, Document, ContentRendition.Native, ContentPurpose.Download), ObjectDeliveryMode.Stream, Ct);

        view.Outcome.Should().Be(ContentAccessOutcome.Granted);
        view.Grant!.BreakGlass.Should().BeTrue();
        download.Outcome.Should().Be(ContentAccessOutcome.Denied);
        download.Decision.Outcome.Should().Be(AuthorizationOutcome.Deny);
        _audit.Events.Select(e => (e.Action, e.Outcome, e.AccessPath)).Should().Equal(
            ("Retrieved", AuditOutcome.Success, AuditAccessPath.BreakGlass),
            ("NativeDownloaded", AuditOutcome.Denied, AuditAccessPath.BreakGlass));
    }

    [Fact]
    public async Task Quarantined_natives_need_view_quarantined_and_quarantined_renditions_are_never_served()
    {
        _catalog.Location = Location(ObjectKeys.Native(Workspace, Document, Digest("native")), "application/octet-stream", quarantined: true);
        Arrange([WorkspaceRole.ProductionManager]);
        var manager = await Service().OpenAsync(Alice, new ContentRequest(Workspace, Document, ContentRendition.Native, ContentPurpose.Download), ObjectDeliveryMode.Stream, Ct);

        Arrange([WorkspaceRole.WorkspaceAdmin]);
        var admin = await Service().OpenAsync(Alice, new ContentRequest(Workspace, Document, ContentRendition.Native, ContentPurpose.Download), ObjectDeliveryMode.Stream, Ct);

        _catalog.Location = Location(ObjectKeys.Rendition(Workspace, Document, Guid.NewGuid(), "p000001.png"), "image/png", quarantined: true);
        var image = await Service().OpenAsync(Alice, new ContentRequest(Workspace, Document, ContentRendition.PageImage, ContentPurpose.Display, 1), ObjectDeliveryMode.Stream, Ct);

        manager.Outcome.Should().Be(ContentAccessOutcome.Denied);
        manager.Decision.Reason.Should().Be(AuthorizationReasons.PermissionNotGranted);
        admin.Outcome.Should().Be(ContentAccessOutcome.Granted);
        image.Outcome.Should().Be(ContentAccessOutcome.Unavailable);
        _audit.Events.Select(e => (e.Action, e.Outcome, e.ReasonCode, e.Details.GetValueOrDefault("permission"), e.Details.GetValueOrDefault("quarantined"))).Should().Equal(
            ("NativeDownloaded", AuditOutcome.Denied, AuthorizationReasons.PermissionNotGranted, "Document.ViewQuarantined", "true"),
            ("NativeDownloaded", AuditOutcome.Success, null, "Document.DownloadNative", "true"),
            ("Retrieved", AuditOutcome.Failure, DocumentAccessService.Reasons.Quarantined, "Document.View", "true"));
    }

    [Fact]
    public async Task A_registry_reference_outside_the_document_is_never_served()
    {
        Arrange([WorkspaceRole.Reviewer]);
        _catalog.Location = Location(ObjectKeys.Text(Workspace, Guid.CreateVersion7(), Digest("other")), "text/plain; charset=utf-8");

        var result = await Service().OpenAsync(Alice, new ContentRequest(Workspace, Document, ContentRendition.Text, ContentPurpose.Display), ObjectDeliveryMode.Stream, Ct);

        result.Outcome.Should().Be(ContentAccessOutcome.Unavailable);
        _audit.Events.Should().ContainSingle().Which.ReasonCode.Should().Be(DocumentAccessService.Reasons.ObjectKeyMismatch);
    }

    [Fact]
    public async Task A_registry_reference_to_another_area_is_never_served()
    {
        Arrange([WorkspaceRole.ProductionManager]);
        _catalog.Location = Location(ObjectKeys.Text(Workspace, Document, Digest("text")), "application/octet-stream");

        var result = await Service().OpenAsync(Alice, new ContentRequest(Workspace, Document, ContentRendition.Native, ContentPurpose.Download), ObjectDeliveryMode.Stream, Ct);

        result.Outcome.Should().Be(ContentAccessOutcome.Unavailable);
    }

    [Fact]
    public async Task Missing_renditions_and_vanished_documents_are_audited()
    {
        Arrange([WorkspaceRole.Reviewer]);
        _catalog.Location = null;
        var missing = await Service().OpenAsync(Alice, new ContentRequest(Workspace, Document, ContentRendition.Text, ContentPurpose.Display), ObjectDeliveryMode.Stream, Ct);
        _catalog.Exists = false;
        var vanished = await Service().OpenAsync(Alice, new ContentRequest(Workspace, Document, ContentRendition.Text, ContentPurpose.Display), ObjectDeliveryMode.Stream, Ct);

        missing.Outcome.Should().Be(ContentAccessOutcome.Unavailable);
        vanished.Outcome.Should().Be(ContentAccessOutcome.Denied);
        vanished.Decision.Outcome.Should().Be(AuthorizationOutcome.NotFound);
        _audit.Events.Select(e => (e.Outcome, e.ReasonCode)).Should().Equal(
            (AuditOutcome.Failure, DocumentAccessService.Reasons.RenditionUnavailable),
            (AuditOutcome.Denied, AuthorizationReasons.DocumentNotFound));
    }

    [Fact]
    public async Task Ranges_are_resolved_against_the_registry_length_and_unsatisfiable_ones_are_refused()
    {
        Arrange([WorkspaceRole.ProductionManager]);
        _catalog.Location = Location(ObjectKeys.Native(Workspace, Document, Digest("native")), "application/octet-stream", length: 100);

        var partial = await Service().OpenAsync(Alice, new ContentRequest(Workspace, Document, ContentRendition.Native, ContentPurpose.Download, Range: new(10, 19)), ObjectDeliveryMode.Stream, Ct);
        var beyond = await Service().OpenAsync(Alice, new ContentRequest(Workspace, Document, ContentRendition.Native, ContentPurpose.Download, Range: new(100, null)), ObjectDeliveryMode.Stream, Ct);

        partial.Grant!.Range.Should().Be(new ByteRange(10, 10));
        beyond.Outcome.Should().Be(ContentAccessOutcome.RangeNotSatisfiable);
        beyond.Length.Should().Be(100);
        _audit.Events.Select(e => (e.Outcome, e.Details.GetValueOrDefault("range"))).Should().Equal(
            (AuditOutcome.Success, "bytes=10-19"), (AuditOutcome.Failure, "bytes=100-"));
    }

    [Theory]
    [InlineData(0L, 9L, 100L, 0L, 10L)]
    [InlineData(95L, null, 100L, 95L, 5L)]
    [InlineData(90L, 500L, 100L, 90L, 10L)]
    [InlineData(null, 30L, 100L, 70L, 30L)]
    [InlineData(null, 300L, 100L, 0L, 100L)]
    public void Range_resolution(long? start, long? end, long length, long offset, long count) =>
        new ContentRangeRequest(start, end).Resolve(length).Should().Be(new ByteRange(offset, count));

    [Theory]
    [InlineData(100L, null, 100L)]
    [InlineData(10L, 5L, 100L)]
    [InlineData(null, 0L, 100L)]
    [InlineData(0L, null, 0L)]
    public void Unsatisfiable_ranges(long? start, long? end, long length) =>
        new ContentRangeRequest(start, end).Resolve(length).Should().BeNull();

    [Fact]
    public async Task A_failed_audit_write_fails_the_request_so_nothing_is_delivered()
    {
        Arrange([WorkspaceRole.Reviewer]);
        _catalog.Location = Location(ObjectKeys.Text(Workspace, Document, Digest("text")), "text/plain; charset=utf-8");
        var service = new DocumentAccessService(new AuthorizationService(_reader, _audit, _time), _catalog, new FailingAuditWriter(), _time);

        var open = () => service.OpenAsync(Alice, new ContentRequest(Workspace, Document, ContentRendition.Text, ContentPurpose.Display), ObjectDeliveryMode.Stream, Ct);

        await open.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(ContentRendition.Native, ContentPurpose.Display)]
    [InlineData(ContentRendition.Text, ContentPurpose.Print)]
    [InlineData(ContentRendition.Thumbnail, ContentPurpose.Download)]
    public async Task Unsupported_combinations_are_rejected(ContentRendition rendition, ContentPurpose purpose)
    {
        Arrange([WorkspaceRole.WorkspaceAdmin]);
        var open = () => Service().OpenAsync(Alice, new ContentRequest(Workspace, Document, rendition, purpose), ObjectDeliveryMode.Stream, Ct);
        await open.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Viewed_beacon_rechecks_view_and_references_the_retrieval()
    {
        Arrange([WorkspaceRole.Reviewer]);
        var retrieval = Guid.CreateVersion7();

        (await Service().RecordViewedAsync(Alice, Workspace, Document, retrieval, Ct)).IsAllowed.Should().BeTrue();
        _reader.Documents[Document] = PolicyEvaluatorTests.Doc(classes: [RestrictionClasses.AttorneysEyesOnly]);
        (await Service().RecordViewedAsync(Alice, Workspace, Document, null, Ct)).Outcome.Should().Be(AuthorizationOutcome.NotFound);

        _audit.Events.Select(e => (e.Action, e.Outcome, e.Details.GetValueOrDefault("retrievedEventId"))).Should().Equal(
            ("Viewed", AuditOutcome.Success, retrieval.ToString()),
            ("Viewed", AuditOutcome.Denied, null));
    }

    [Fact]
    public async Task A_text_chunk_resolves_its_own_byte_range_and_is_audited_with_chunk_and_range()
    {
        Arrange([WorkspaceRole.Reviewer]);
        var length = (2L * TextChunks.ChunkBytes) + 10;
        _catalog.Location = Location(ObjectKeys.Text(Workspace, Document, Digest("text")), "text/plain; charset=utf-8", length: length);
        _catalog.TextTruncated = true;

        var result = await Service().OpenAsync(
            Alice, new ContentRequest(Workspace, Document, ContentRendition.Text, ContentPurpose.Prefetch, TextChunk: 1), ObjectDeliveryMode.Stream, Ct);

        result.Outcome.Should().Be(ContentAccessOutcome.Granted);
        result.Grant!.Range.Should().Be(new ByteRange(TextChunks.ChunkBytes, TextChunks.ChunkBytes + TextChunks.MaxContinuationBytes));
        result.Document!.TextTruncated.Should().BeTrue();
        var e = _audit.Events.Should().ContainSingle().Subject;
        e.Details["chunk"].Should().Be("1");
        e.Details["purpose"].Should().Be("Prefetch");
        e.Details["range"].Should().Be($"bytes={TextChunks.ChunkBytes}-{(2 * TextChunks.ChunkBytes) + 2}");

        var last = await Service().OpenAsync(
            Alice, new ContentRequest(Workspace, Document, ContentRendition.Text, ContentPurpose.Display, TextChunk: 2), ObjectDeliveryMode.Stream, Ct);
        last.Grant!.Range.Should().Be(new ByteRange(2L * TextChunks.ChunkBytes, 10));

        _audit.Clear();
        var beyond = await Service().OpenAsync(
            Alice, new ContentRequest(Workspace, Document, ContentRendition.Text, ContentPurpose.Display, TextChunk: 3), ObjectDeliveryMode.Stream, Ct);
        beyond.Outcome.Should().Be(ContentAccessOutcome.RangeNotSatisfiable);
        _audit.Events.Should().ContainSingle().Which.Should().Match<AuditEvent>(a => a.Outcome == AuditOutcome.Failure && a.ReasonCode == "RangeNotSatisfiable");
    }

    [Fact]
    public async Task The_single_chunk_of_an_empty_text_reads_nothing_and_missing_text_reports_its_reason()
    {
        Arrange([WorkspaceRole.Reviewer]);
        _catalog.Location = Location(ObjectKeys.Text(Workspace, Document, Digest(string.Empty)), "text/plain; charset=utf-8", length: 0);

        var empty = await Service().OpenAsync(
            Alice, new ContentRequest(Workspace, Document, ContentRendition.Text, ContentPurpose.Display, TextChunk: 0), ObjectDeliveryMode.Stream, Ct);
        empty.Outcome.Should().Be(ContentAccessOutcome.Granted);
        empty.Grant!.Range.Should().BeNull();

        _catalog.Location = null;
        var missing = await Service().OpenAsync(
            Alice, new ContentRequest(Workspace, Document, ContentRendition.Text, ContentPurpose.Display, TextChunk: 0), ObjectDeliveryMode.Stream, Ct);
        missing.Outcome.Should().Be(ContentAccessOutcome.Unavailable);
        missing.Reason.Should().Be(DocumentAccessService.Reasons.RenditionUnavailable);
        missing.Document.Should().NotBeNull();
    }

    [Fact]
    public async Task Json_views_are_decided_loaded_and_audited_in_that_order()
    {
        Arrange([WorkspaceRole.Reviewer]);
        var loads = 0;

        var result = await Service().ReadAsync(
            Alice,
            new ContentRequest(Workspace, Document, ContentRendition.Metadata, ContentPurpose.Display),
            _ =>
            {
                loads++;
                _audit.Events.Should().BeEmpty("the audit event is written after the read, before the response");
                return Task.FromResult<string?>("metadata");
            },
            Ct);

        (result.Value, loads).Should().Be(("metadata", 1));
        var e = _audit.Events.Should().ContainSingle().Subject;
        (e.Action, e.Outcome, e.Details["rendition"], e.Details["permission"]).Should().Be(("Retrieved", AuditOutcome.Success, "Metadata", "Document.View"));
        result.AuditEventId.Should().Be(e.EventId);

        _audit.Clear();
        var vanished = await Service().ReadAsync(
            Alice, new ContentRequest(Workspace, Document, ContentRendition.PageList, ContentPurpose.Prefetch), _ => Task.FromResult<string?>(null), Ct);
        vanished.Decision.Outcome.Should().Be(AuthorizationOutcome.NotFound);
        _audit.Events.Should().ContainSingle().Which.ReasonCode.Should().Be(AuthorizationReasons.DocumentNotFound);
    }

    [Fact]
    public async Task Json_views_of_a_walled_document_are_not_loaded()
    {
        Arrange([WorkspaceRole.Reviewer], walls: [Wall]);
        _reader.Documents[Document] = PolicyEvaluatorTests.Doc(walls: [Wall]);

        var result = await Service().ReadAsync<string>(
            Alice,
            new ContentRequest(Workspace, Document, ContentRendition.Metadata, ContentPurpose.Display),
            _ => throw new InvalidOperationException("must not load"),
            Ct);

        result.Decision.Outcome.Should().Be(AuthorizationOutcome.NotFound);
        result.Value.Should().BeNull();
        _audit.Events.Should().ContainSingle().Which.ReasonCode.Should().Be(AuthorizationReasons.EthicalWall);
    }

    private static Sha256Digest Digest(string content) => Sha256Digest.FromBytes(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static ContentLocation Location(ObjectKey key, string contentType, bool quarantined = false, long length = 42) =>
        new(Guid.CreateVersion7(), key.Value, SHA256.HashData(Encoding.UTF8.GetBytes(key.Value)), length, contentType, quarantined);

    private void Arrange(IEnumerable<WorkspaceRole> roles, IEnumerable<Guid>? walls = null)
    {
        _reader.State = PolicyEvaluatorTests.State(roles, walls);
        _reader.Documents.TryAdd(Document, DocumentSecurityAttributes.Unrestricted);
    }

    private DocumentAccessService Service() => new(new AuthorizationService(_reader, _audit, _time), _catalog, _audit, _time);

    private sealed class FakeReader : ISecurityStateReader
    {
        public PrincipalSecurityState? State { get; set; }

        public Dictionary<Guid, DocumentSecurityAttributes> Documents { get; } = [];

        public Task<SecurityStateRead> ReadAsync(
            Guid workspaceId, SecurityPrincipal principal, bool includePrincipal, IReadOnlyCollection<Guid>? documentIds, CancellationToken cancellationToken = default)
        {
            var documents = (documentIds ?? []).Where(Documents.ContainsKey).Distinct().ToDictionary(id => id, id => Documents[id]);
            return Task.FromResult(new SecurityStateRead(includePrincipal ? State : null, documents));
        }
    }

    private sealed class FakeCatalog : IDocumentContentCatalog
    {
        public bool Exists { get; set; } = true;

        public string ControlNumber { get; set; } = "ABC0001";

        public string? FileExtension { get; set; } = "pdf";

        public ContentLocation? Location { get; set; }

        public bool TextTruncated { get; set; }

        public int Calls { get; private set; }

        public Task<DocumentContent?> FindAsync(
            Guid workspaceId, Guid documentId, ContentRendition rendition, int? pageNumber, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Exists ? new DocumentContent(ControlNumber, FileExtension, Location, TextTruncated) : null);
        }
    }

    private sealed class FailingAuditWriter : IAuditEventWriter
    {
        public ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("audit store unavailable");
    }
}
