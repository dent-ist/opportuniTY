using System.Security.Cryptography;

using AwesomeAssertions;

using Opportunity.Application.Keys;
using Opportunity.Security.Keys;

namespace Opportunity.UnitTests.Keys;

public sealed class LocalKeyProviderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("opportunity-keys-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task A_kek_is_created_once_with_private_file_permissions_and_wraps_and_unwraps()
    {
        var provider = Kek();
        var dataKey = RandomNumberGenerator.GetBytes(32);

        var info = await provider.EnsureAsync(KeyEncryptionKeyIds.Installation, Ct);
        (await provider.EnsureAsync(KeyEncryptionKeyIds.Installation, Ct)).Versions.Should().Equal(1);
        var wrapped = await provider.WrapAsync(KeyEncryptionKeyIds.Installation, dataKey, Ct);

        info.CurrentVersion.Should().Be(1);
        wrapped.KekVersion.Should().Be(1);
        wrapped.Ciphertext.AsSpan().IndexOf(dataKey).Should().Be(-1, "a wrapped key never contains the plaintext");
        (await provider.UnwrapAsync(wrapped, Ct)).Should().Equal(dataKey);
        var file = Directory.EnumerateFiles(_dir, "*.key", SearchOption.AllDirectories).Single();
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(file).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task Rotation_wraps_new_keys_with_the_new_version_and_old_wraps_open_until_their_version_is_destroyed()
    {
        var provider = Kek();
        await provider.EnsureAsync("installation", Ct);
        var dataKey = RandomNumberGenerator.GetBytes(32);
        var old = await provider.WrapAsync("installation", dataKey, Ct);

        var rotated = await provider.RotateAsync("installation", Ct);
        var fresh = await provider.WrapAsync("installation", dataKey, Ct);

        rotated.Versions.Should().Equal(1, 2);
        fresh.KekVersion.Should().Be(2);
        (await provider.UnwrapAsync(old, Ct)).Should().Equal(dataKey);
        var destroyNewest = () => provider.DestroyVersionAsync("installation", 2, Ct);
        await destroyNewest.Should().ThrowAsync<InvalidOperationException>();

        await provider.DestroyVersionAsync("installation", 1, Ct);

        var unwrapOld = () => provider.UnwrapAsync(old, Ct);
        await unwrapOld.Should().ThrowAsync<KeyUnavailableException>();
        (await provider.UnwrapAsync(fresh, Ct)).Should().Equal(dataKey);
    }

    [Fact]
    public async Task Wrapped_keys_are_bound_to_their_kek_and_tampering_is_detected()
    {
        var provider = Kek();
        await provider.EnsureAsync("installation", Ct);
        await provider.EnsureAsync("ws-a", Ct);
        var wrapped = await provider.WrapAsync("installation", RandomNumberGenerator.GetBytes(32), Ct);

        var tampered = wrapped with { Ciphertext = [.. wrapped.Ciphertext] };
        tampered.Ciphertext[^1] ^= 1;
        var relabelled = wrapped with { KekId = "ws-a" };

        await FluentActions.Awaiting(() => provider.UnwrapAsync(tampered, Ct)).Should().ThrowAsync<KeyUnavailableException>();
        await FluentActions.Awaiting(() => provider.UnwrapAsync(relabelled, Ct)).Should().ThrowAsync<KeyUnavailableException>();
    }

    [Fact]
    public async Task Destroying_a_kek_removes_every_version()
    {
        var provider = Kek();
        await provider.EnsureAsync("ws-b", Ct);
        await provider.RotateAsync("ws-b", Ct);
        var wrapped = await provider.WrapAsync("ws-b", RandomNumberGenerator.GetBytes(32), Ct);

        await provider.DestroyAsync("ws-b", Ct);
        await provider.DestroyAsync("ws-b", Ct);

        (await provider.DescribeAsync("ws-b", Ct)).Should().BeNull();
        await FluentActions.Awaiting(() => provider.UnwrapAsync(wrapped, Ct)).Should().ThrowAsync<KeyUnavailableException>();
        Directory.Exists(Path.Combine(_dir, "kek", "ws-b")).Should().BeFalse();
    }

    [Fact]
    public async Task With_a_master_key_secret_the_key_files_alone_do_not_open()
    {
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var secrets = new EnvironmentSecretProvider(v => v == "OPPORTUNITY_SECRET_KEY_STORE_MASTER" ? masterKey : null);
        var options = new LocalKeyStoreOptions { KeyDirectory = _dir, MasterKeySecret = "key-store-master" };
        var sealedProvider = new LocalKeyEncryptionKeyProvider(options, secrets);
        await sealedProvider.EnsureAsync("installation", Ct);
        var dataKey = RandomNumberGenerator.GetBytes(32);
        var wrapped = await sealedProvider.WrapAsync("installation", dataKey, Ct);

        var withoutMaster = new LocalKeyEncryptionKeyProvider(new LocalKeyStoreOptions { KeyDirectory = _dir }, secrets);
        var wrongMaster = new LocalKeyEncryptionKeyProvider(
            options, new EnvironmentSecretProvider(v => v == "OPPORTUNITY_SECRET_KEY_STORE_MASTER" ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) : null));

        (await sealedProvider.UnwrapAsync(wrapped, Ct)).Should().Equal(dataKey);
        await FluentActions.Awaiting(() => withoutMaster.UnwrapAsync(wrapped, Ct)).Should().ThrowAsync<KeyUnavailableException>();
        await FluentActions.Awaiting(() => wrongMaster.UnwrapAsync(wrapped, Ct)).Should().ThrowAsync<KeyUnavailableException>();
    }

    [Fact]
    public async Task A_missing_key_directory_setting_fails_only_on_use_and_names_the_setting()
    {
        var provider = new LocalKeyEncryptionKeyProvider(new LocalKeyStoreOptions(), new EnvironmentSecretProvider(_ => null));

        await FluentActions.Awaiting(() => provider.EnsureAsync("installation", Ct))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*KeyManagement:Local:KeyDirectory*");
    }

    [Fact]
    public async Task Signing_keys_sign_verify_rotate_and_export_public_keys_for_exhibits()
    {
        var signing = new LocalSigningKeyProvider(new LocalKeyStoreOptions { KeyDirectory = _dir }, new EnvironmentSecretProvider(_ => null));
        var checkpoint = "checkpoint root 0011"u8.ToArray();

        var first = await signing.SignAsync(SigningKeyPurposes.AuditCheckpoint, checkpoint, Ct);
        var rotatedTo = await signing.RotateAsync(SigningKeyPurposes.AuditCheckpoint, Ct);
        var second = await signing.SignAsync(SigningKeyPurposes.AuditCheckpoint, checkpoint, Ct);

        first.KeyId.Should().Be("audit-checkpoint-v1");
        rotatedTo.Should().Be(2);
        second.Version.Should().Be(2);
        (await signing.VerifyAsync(first, checkpoint, Ct)).Should().BeTrue("older versions keep verifying after rotation");
        (await signing.VerifyAsync(second, checkpoint, Ct)).Should().BeTrue();
        (await signing.VerifyAsync(first, "checkpoint root 0012"u8.ToArray(), Ct)).Should().BeFalse();
        (await signing.VerifyAsync(first with { Version = 2 }, checkpoint, Ct)).Should().BeFalse();

        // An outside verifier needs only the exported public key.
        var exported = await signing.GetPublicKeyAsync(SigningKeyPurposes.AuditCheckpoint, 1, Ct);
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(exported.SubjectPublicKeyInfo, out _);
        verifier.VerifyData(checkpoint, first.Value, HashAlgorithmName.SHA256).Should().BeTrue();
    }

    private LocalKeyEncryptionKeyProvider Kek() =>
        new(new LocalKeyStoreOptions { KeyDirectory = _dir }, new EnvironmentSecretProvider(_ => null));
}
