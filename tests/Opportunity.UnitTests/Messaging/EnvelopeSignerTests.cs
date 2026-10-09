using System.Security.Cryptography;
using System.Text;

using AwesomeAssertions;

using Opportunity.Application.Keys;
using Opportunity.Messaging;

namespace Opportunity.UnitTests.Messaging;

/// <summary>E05-T07: HMAC envelope signing (ADR-015 D9.5). Keys are random per test run, never literals.</summary>
public sealed class EnvelopeSignerTests
{
    private const string Queue = "export.chunks";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"messageId":"m","workspaceId":"w"}""");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_signed_envelope_verifies_on_its_own_queue_only()
    {
        var secrets = new RandomSecrets("k1");
        var signer = Signer(secrets, "k1");
        var headers = await SignAsync(signer, Queue, Body);

        (await signer.VerifyAsync(Queue, Body, headers, Ct)).Should().Be(SignatureCheck.Valid);
        headers[TransportHeaders.SignatureKeyId].Should().Be("k1");
        (await signer.VerifyAsync("render.chunks", Body, headers, Ct)).Should().Be(SignatureCheck.Invalid, "a signature binds the destination queue");
    }

    [Fact]
    public async Task Tampered_missing_and_foreign_signatures_are_refused()
    {
        var secrets = new RandomSecrets("k1", "k2");
        var signer = Signer(secrets, "k1");
        var headers = await SignAsync(signer, Queue, Body);

        var tampered = (byte[])Body.Clone();
        tampered[^2] ^= 0x01;
        (await signer.VerifyAsync(Queue, tampered, headers, Ct)).Should().Be(SignatureCheck.Invalid);
        (await signer.VerifyAsync(Queue, Body, new Dictionary<string, object?>(), Ct)).Should().Be(SignatureCheck.Missing);
        (await signer.VerifyAsync(Queue, Body, null, Ct)).Should().Be(SignatureCheck.Missing);

        var garbled = new Dictionary<string, object?>(headers) { [TransportHeaders.Signature] = "v1.not-base64" };
        (await signer.VerifyAsync(Queue, Body, garbled, Ct)).Should().Be(SignatureCheck.Invalid);

        // Signed with a key the consumer does not accept, and with an accepted id but another key's bytes.
        var other = await SignAsync(Signer(secrets, "k2"), Queue, Body);
        (await signer.VerifyAsync(Queue, Body, other, Ct)).Should().Be(SignatureCheck.UnknownKey);
        var impostor = await SignAsync(Signer(new RandomSecrets("k1"), "k1"), Queue, Body);
        (await signer.VerifyAsync(Queue, Body, impostor, Ct)).Should().Be(SignatureCheck.Invalid);
    }

    [Fact]
    public async Task Rotation_accepts_the_previous_key_while_signing_with_the_new_one()
    {
        var secrets = new RandomSecrets("k1", "k2");
        var before = await SignAsync(Signer(secrets, "k1"), Queue, Body);
        var rotated = Signer(secrets, "k2", accepted: "k1");
        var after = await SignAsync(rotated, Queue, Body);

        after[TransportHeaders.SignatureKeyId].Should().Be("k2");
        (await rotated.VerifyAsync(Queue, Body, before, Ct)).Should().Be(SignatureCheck.Valid);
        (await rotated.VerifyAsync(Queue, Body, after, Ct)).Should().Be(SignatureCheck.Valid);
        (await Signer(secrets, "k2").VerifyAsync(Queue, Body, before, Ct)).Should().Be(SignatureCheck.UnknownKey, "k1 was retired");
    }

    [Fact]
    public async Task Short_or_missing_keys_and_a_missing_secret_provider_are_configuration_errors()
    {
        var shortKey = new RandomSecrets(length: 16, "k1");
        var sign = () => SignAsync(Signer(shortKey, "k1"), Queue, Body);
        await sign.Should().ThrowAsync<InvalidOperationException>().WithMessage("*shorter than 32 bytes*");

        var none = () => SignAsync(Signer(new RandomSecrets(), "k1"), Queue, Body);
        await none.Should().ThrowAsync<InvalidOperationException>().WithMessage("*envelope-hmac-k1*");

        var noProvider = () => new EnvelopeSigner(new MessageSigningOptions { Enabled = true }, null);
        noProvider.Should().Throw<InvalidOperationException>();
        var badId = () => new EnvelopeSigner(new MessageSigningOptions { Enabled = true, KeyId = "../k" }, new RandomSecrets());
        badId.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task A_disabled_signer_adds_nothing()
    {
        var headers = await SignAsync(EnvelopeSigner.Disabled, Queue, Body);
        headers.Should().BeEmpty();
        EnvelopeSigner.Disabled.Enabled.Should().BeFalse();
    }

    private static EnvelopeSigner Signer(ISecretProvider secrets, string keyId, string? accepted = null) =>
        new(new MessageSigningOptions { Enabled = true, KeyId = keyId, AcceptedKeyIds = accepted }, secrets);

    private static async Task<Dictionary<string, object?>> SignAsync(EnvelopeSigner signer, string queue, byte[] body)
    {
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal);
        await signer.SignAsync(queue, body, headers, Ct);
        return headers;
    }

    /// <summary>Random key material per key id, generated at run time.</summary>
    private sealed class RandomSecrets(int length, params string[] keyIds) : ISecretProvider
    {
        private readonly Dictionary<string, byte[]> _values = keyIds.ToDictionary(
            id => MessageSigningOptions.SecretName(id), _ => RandomNumberGenerator.GetBytes(length), StringComparer.Ordinal);

        public RandomSecrets(params string[] keyIds)
            : this(32, keyIds)
        {
        }

        public ValueTask<SecretValue?> GetAsync(string name, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_values.TryGetValue(name, out var value) ? new SecretValue(value) : null);
    }
}
