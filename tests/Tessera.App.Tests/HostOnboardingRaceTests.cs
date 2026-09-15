using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RoyalTerminal.Terminal;
using RoyalTerminal.Terminal.Transport.Ssh;
using Tessera.Services;
using Xunit;

namespace Tessera.NativeTests;

public sealed class HostOnboardingRaceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly SshEndpointOptions Endpoint = new("onboarding.invalid", 2222, "operator");
    private static SshHostKeyInfo Key(byte seed)
    {
        using var stream = new MemoryStream();
        foreach (byte[] part in new[] { Encoding.ASCII.GetBytes("ssh-ed25519"), Enumerable.Repeat(seed, 32).ToArray() })
        {
            byte[] size = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(size, part.Length);
            stream.Write(size); stream.Write(part);
        }
        byte[] bytes = stream.ToArray();
        return new("ssh-ed25519", "SHA256:" + Convert.ToBase64String(SHA256.HashData(bytes)).TrimEnd('='), "", 256, Convert.ToBase64String(bytes));
    }
    private static string Entry(SshHostKeyInfo key, bool revoked = false) =>
        (revoked ? "@revoked " : "") + $"[onboarding.invalid]:2222 ssh-ed25519 {key.HostKeyBase64}\n";

    [Theory]
    [InlineData(HostKeyDecision.TrustOnce)]
    [InlineData(HostKeyDecision.TrustAndSave)]
    public async Task RevocationWhileDialogIsOpenInvalidatesAcceptance(HostKeyDecision decision)
    {
        using var directory = new AcceptanceDirectory();
        string systemFile = directory.File("system_known_hosts");
        var repository = new KnownHostRepository(directory.Path) { SystemFilesOverride = [systemFile] };
        var key = Key(1);
        repository.Prompt = async (_, token) =>
        {
            await File.WriteAllTextAsync(systemFile, Entry(key, revoked: true), token);
            return decision;
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ValidateAsync(Endpoint, key, null, Token));
        Assert.False(File.Exists(repository.FilePath));
    }

    [Fact]
    public async Task ConcurrentRotationIsNotOverwrittenByTheOriginalFingerprint()
    {
        using var directory = new AcceptanceDirectory();
        var repository = new KnownHostRepository(directory.Path) { SystemFilesOverride = [] };
        string rotated = Entry(Key(2));
        repository.Prompt = async (_, token) =>
        {
            await File.WriteAllTextAsync(repository.FilePath, rotated, token);
            return HostKeyDecision.TrustAndSave;
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ValidateAsync(Endpoint, Key(1), null, Token));
        Assert.Equal(rotated, await File.ReadAllTextAsync(repository.FilePath, Token));
    }

    [Fact]
    public async Task ConcurrentMatchingTrustDoesNotCreateDuplicateEntries()
    {
        using var directory = new AcceptanceDirectory();
        var repository = new KnownHostRepository(directory.Path) { SystemFilesOverride = [] };
        var key = Key(1); string expected = Entry(key);
        repository.Prompt = async (_, token) =>
        {
            await File.WriteAllTextAsync(repository.FilePath, expected, token);
            return HostKeyDecision.TrustAndSave;
        };
        Assert.True(await repository.ValidateAsync(Endpoint, key, null, Token));
        Assert.Equal(expected, await File.ReadAllTextAsync(repository.FilePath, Token));
    }
}
