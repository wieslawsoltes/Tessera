using System.Text;
using Avalonia.Headless.XUnit;
using RoyalTerminal.Terminal;
using Tessera.Services;
using Xunit;

namespace Tessera.NativeTests;

/// <summary>Explicitly skipped without an isolated OS vault or loopback OpenSSH fixture. Never uses saved user connections.</summary>
public sealed class ExternalAcceptanceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task OperatingSystemVaultRoundTripsReplacesAndDeletesUnicodeSecret()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TESSERA_VAULT_TESTS")=="1","Requires an explicitly isolated, unlocked OS credential vault.");
        var vault=new OsCredentialVault();string key="acceptance/"+Guid.NewGuid().ToString("N");
        try
        {
            Assert.Null(await vault.ReadAsync(key,Token));
            await vault.WriteAsync(key,"zażółć\n秘密\n",Token);
            Assert.Equal("zażółć\n秘密\n",await vault.ReadAsync(key,Token));
            await vault.WriteAsync(key,"replacement\r\nwith newline",Token);
            Assert.Equal("replacement\r\nwith newline",await vault.ReadAsync(key,Token));
            await vault.DeleteAsync(key,Token);Assert.Null(await vault.ReadAsync(key,Token));
            await vault.DeleteAsync(key,Token);
        }
        finally {await vault.DeleteAsync(key,Token);}
    }
    private static async Task<ProfileRepository> ProfilesAsync(string directory, Action trusted, Action passphrase, Action challenged)
    {
        var profiles=new ProfileRepository(directory,new MemoryVault());profiles.KnownHosts.SystemFilesOverride=[];
        profiles.KnownHosts.Prompt=(_,ct)=>{ct.ThrowIfCancellationRequested();trusted();return Task.FromResult(HostKeyDecision.TrustAndSave);};
        profiles.CredentialPrompt=(title,ct)=>
        {ct.ThrowIfCancellationRequested();Assert.StartsWith("Passphrase for",title);passphrase();return Task.FromResult<string?>(Environment.GetEnvironmentVariable("TESSERA_TEST_KEY_PASSPHRASE")!);};
        profiles.ChallengePrompt=(value,ct)=>
        {
            ct.ThrowIfCancellationRequested();Assert.Equal("127.0.0.1",value.Host);challenged();
            return Task.FromResult<string[]?>(value.Prompts.Select(_=>Environment.GetEnvironmentVariable("TESSERA_TEST_SSH_PASSWORD")!).ToArray());
        };
        var profile=profiles.Get("local") with
        {
            DisplayName="Isolated loopback integration",
            Transport=new() {TransportId=TerminalTransportIds.Ssh,Ssh=new()
            {
                Host="127.0.0.1",Port=23781,Username="tessera-integration",
                Authentication=new(){PrivateKeySecretIds=[Environment.GetEnvironmentVariable("TESSERA_TEST_SSH_KEY")!]},
                Policy=new(5,15)
            }}
        };
        await profiles.SaveAdvancedAsync(profile,Token);return profiles;
    }
    [AvaloniaFact]
    public async Task RealSftpUsesEncryptedKeyAndInteractiveChallengeThenTransfersAndDetectsConflicts()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TESSERA_SSH_TESTS")=="1","Requires the isolated loopback OpenSSH public-key plus keyboard-interactive fixture.");
        using var directory=new AcceptanceDirectory();int trust=0,passphrase=0,challenge=0;
        var profiles=await ProfilesAsync(directory.Path,()=>trust++,()=>passphrase++,()=>challenge++);
        await using var sftp=await SftpWorkspace.ConnectAsync(profiles,profiles.Get("local"),Token);
        Assert.True(sftp.Connected);Assert.Equal(1,trust);Assert.True(passphrase>=1);Assert.True(challenge>=1);
        string root=SftpWorkspace.ChildPath(sftp.HomeDirectory,"acceptance-"+Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>sftp.CreateDirectoryAsync(root,Token));
        sftp.ReadOnly=false;await sftp.CreateDirectoryAsync(root,Token);
        string remote=root+"/zażółć.txt";string local=directory.File("source.txt");
        byte[] original=Encoding.UTF8.GetBytes("terminal αβ\nsecond line\n");await File.WriteAllBytesAsync(local,original,Token);
        await sftp.UploadAsync(local,remote,false,token:Token);var entries=await sftp.ListAsync(root,Token);Assert.Single(entries);
        string download=directory.File("download.txt");await sftp.DownloadAsync(remote,download,false,token:Token);
        Assert.Equal(original,await File.ReadAllBytesAsync(download,Token));
        var opened=await sftp.ReadTextAsync(remote,Token);Assert.NotNull(opened.Version.Sha256);
        byte[] updated=Encoding.UTF8.GetBytes("edited αβ\n");var newVersion=await sftp.SaveTextAsync(remote,updated,opened.Version,Token);
        Assert.Equal(updated,(await sftp.ReadTextAsync(remote,Token)).Content);
        await Assert.ThrowsAsync<IOException>(()=>sftp.SaveTextAsync(remote,original,opened.Version,Token));
        await Assert.ThrowsAsync<IOException>(()=>sftp.UploadAsync(local,remote,false,token:Token));
        // Pre-cancelled transfer must not replace the committed destination or leave a staging file.
        using(var cancelled=new CancellationTokenSource())
        {cancelled.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>sftp.UploadAsync(local,remote,true,token:cancelled.Token));}
        Assert.Equal(updated,(await sftp.ReadTextAsync(remote,Token)).Content);
        Assert.DoesNotContain(await sftp.ListAsync(root,Token),f=>f.Name.EndsWith(".part",StringComparison.Ordinal));
        string renamed=root+"/renamed.txt";await sftp.RenameAsync(remote,renamed,Token);
        entries=await sftp.ListAsync(root,Token);Assert.Single(entries);Assert.Equal("renamed.txt",entries[0].Name);
        await sftp.DeleteAsync(entries[0],Token);Assert.Empty(await sftp.ListAsync(root,Token));
        var folder=(await sftp.ListAsync(sftp.HomeDirectory,Token)).Single(f=>f.Path==root);await sftp.DeleteAsync(folder,Token);
        Assert.DoesNotContain(await sftp.ListAsync(sftp.HomeDirectory,Token),f=>f.Path==root);
        Assert.Empty(((MemoryVault)profiles.Vault).Values); // Neither private-key passphrase nor MFA response is persisted.
    }
    [AvaloniaFact]
    public async Task RealSftpSymlinkOperationsDoNotDeleteOrOverwriteTheirTarget()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TESSERA_SSH_TESTS")=="1","Requires the isolated OpenSSH symlink fixture.");
        using var directory=new AcceptanceDirectory();var profiles=await ProfilesAsync(directory.Path,()=>{},()=>{},()=>{});
        await using var sftp=await SftpWorkspace.ConnectAsync(profiles,profiles.Get("local"),Token);sftp.ReadOnly=false;
        string fixture=sftp.HomeDirectory+"/symlink-fixture";
        var files=await sftp.ListAsync(fixture,Token);var link=files.Single(f=>f.Name=="link");Assert.True(link.SymbolicLink);
        string local=directory.File("source");await File.WriteAllTextAsync(local,"do not overwrite",Token);
        await Assert.ThrowsAsync<IOException>(()=>sftp.UploadAsync(local,link.Path,true,token:Token));
        await sftp.RenameAsync(link.Path,fixture+"/renamed-link",Token);
        var renamed=(await sftp.ListAsync(fixture,Token)).Single(f=>f.Name=="renamed-link");Assert.True(renamed.SymbolicLink);
        await sftp.DeleteAsync(renamed,Token);
        Assert.Equal("keep-target\n",Encoding.UTF8.GetString((await sftp.ReadTextAsync(fixture+"/target",Token)).Content));
        var dangling=(await sftp.ListAsync(fixture,Token)).Single(f=>f.Name=="dangling");Assert.True(dangling.SymbolicLink);
        await Assert.ThrowsAsync<IOException>(()=>sftp.UploadAsync(local,dangling.Path,true,token:Token));
        await sftp.DeleteAsync(dangling,Token);
        Assert.DoesNotContain(await sftp.ListAsync(fixture,Token),f=>f.Name=="missing");
    }
}
