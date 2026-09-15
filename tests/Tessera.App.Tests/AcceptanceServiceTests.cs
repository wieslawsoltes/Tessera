// Failure-path fixtures deliberately supply independent cancellation sources or deterministic in-memory operations.
#pragma warning disable xUnit1051
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using RoyalTerminal.Terminal;
using RoyalTerminal.Terminal.Transport.Ssh;
using Tessera.Core;
using Tessera.Services;
using Xunit;

namespace Tessera.NativeTests;

internal sealed class MemoryVault : ICredentialVault
{
    public ConcurrentDictionary<string, string> Values { get; } = new();
    public bool Unavailable { get; set; }
    public string Name => "Isolated test vault";
    private void Check(CancellationToken token) { token.ThrowIfCancellationRequested(); if(Unavailable) throw new IOException("Vault deliberately unavailable in test."); }
    public Task<string?> ReadAsync(string key, CancellationToken token = default) { Check(token); return Task.FromResult(Values.GetValueOrDefault(key)); }
    public Task WriteAsync(string key, string value, CancellationToken token = default) { Check(token); Values[key] = value; return Task.CompletedTask; }
    public Task DeleteAsync(string key, CancellationToken token = default) { Check(token); Values.TryRemove(key, out _); return Task.CompletedTask; }
}
internal sealed class AcceptanceDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tessera-acceptance-" + Guid.NewGuid().ToString("N"));
    public AcceptanceDirectory() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch(IOException) { } }
}

public sealed class AcceptanceServiceTests
{
    private static readonly SshEndpointOptions Endpoint = new("acceptance.invalid", 2222, "test-user");
    private static SshHostKeyInfo Key(byte seed = 1)
    {
        using var bytes = new MemoryStream();
        void String(byte[] value) { Span<byte> size=stackalloc byte[4];System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(size,value.Length);bytes.Write(size);bytes.Write(value); }
        String("ssh-ed25519"u8.ToArray()); String(Enumerable.Repeat(seed,32).ToArray());
        var material=bytes.ToArray();
        return new("ssh-ed25519","SHA256:"+Convert.ToBase64String(SHA256.HashData(material)).TrimEnd('='),"",256,Convert.ToBase64String(material));
    }
    private static KnownHostRepository Hosts(string directory) => new(directory) { SystemFilesOverride = [] };

    [Theory]
    [InlineData(HostKeyDecision.Reject, false, false)]
    [InlineData(HostKeyDecision.TrustOnce, true, false)]
    [InlineData(HostKeyDecision.TrustAndSave, true, true)]
    [InlineData((HostKeyDecision)999, false, false)]
    public async Task UnknownHostsRequireAnExplicitValidDecision(HostKeyDecision decision, bool accepted, bool saved)
    {
        using var temp=new AcceptanceDirectory();var hosts=Hosts(temp.Path);int prompts=0;
        hosts.Prompt=(challenge,token)=> {Assert.Equal("acceptance.invalid",challenge.Host);Assert.StartsWith("SHA256:",challenge.Fingerprint);prompts++;return Task.FromResult(decision);};
        Assert.Equal(accepted,await hosts.ValidateAsync(Endpoint,Key(),null,default));
        Assert.Equal(1,prompts);Assert.Equal(saved,File.Exists(hosts.FilePath));
        if(saved) Assert.True(await Hosts(temp.Path).ValidateAsync(Endpoint,Key(),null,default));
        else Assert.False(await Hosts(temp.Path).ValidateAsync(Endpoint,Key(),null,default));
    }

    [Fact]
    public async Task ChangedKeysAreRejectedEvenWhenANewFingerprintIsPinned()
    {
        using var temp=new AcceptanceDirectory();var hosts=Hosts(temp.Path);
        hosts.Prompt=(_,_)=>Task.FromResult(HostKeyDecision.TrustAndSave);
        Assert.True(await hosts.ValidateAsync(Endpoint,Key(),null,default));
        hosts.Prompt=(_,_)=>throw new Xunit.Sdk.XunitException("Changed keys must not offer a bypass.");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>hosts.ValidateAsync(Endpoint,Key(2),Key(2).FingerprintSha256,default));
    }

    [Fact]
    public async Task RevokedKeysAreRejectedEvenWithAMatchingPin()
    {
        using var temp=new AcceptanceDirectory();var hosts=Hosts(temp.Path);var key=Key();
        await File.WriteAllTextAsync(hosts.FilePath,$"@revoked [acceptance.invalid]:2222 ssh-ed25519 {key.HostKeyBase64}\n");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>hosts.ValidateAsync(Endpoint,key,key.FingerprintSha256,default));
    }

    [Fact]
    public async Task PinsDoNotBecomeAutomaticTrustOrLeakAcrossPorts()
    {
        using var temp=new AcceptanceDirectory();var hosts=Hosts(temp.Path);
        Assert.False(await hosts.ValidateAsync(Endpoint,Key(),Key(2).FingerprintSha256,default));
        Assert.True(await hosts.ValidateAsync(Endpoint,Key(),Key().FingerprintSha256,default));
        Assert.False(File.Exists(hosts.FilePath));
        hosts.Prompt=(_,_)=>Task.FromResult(HostKeyDecision.TrustAndSave);await hosts.ValidateAsync(Endpoint,Key(),null,default);
        hosts.Prompt=null;
        Assert.False(await hosts.ValidateAsync(Endpoint with {Port=2223},Key(),null,default));
    }

    [Fact]
    public async Task CancelledHostOnboardingDoesNotPersistTrust()
    {
        using var temp=new AcceptanceDirectory();var hosts=Hosts(temp.Path);using var cts=new CancellationTokenSource();
        hosts.Prompt=async(_,token)=>{cts.Cancel();await Task.Delay(Timeout.Infinite,token);return HostKeyDecision.TrustAndSave;};
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>hosts.ValidateAsync(Endpoint,Key(),null,cts.Token));
        Assert.False(File.Exists(hosts.FilePath));
    }

    [Fact]
    public async Task MalformedPresentedKeyIsNotAcceptedByThePrompt()
    {
        using var temp=new AcceptanceDirectory();var hosts=Hosts(temp.Path);
        hosts.Prompt=(_,_)=>throw new Xunit.Sdk.XunitException("Malformed keys must not be offered.");
        Assert.False(await hosts.ValidateAsync(Endpoint,Key() with {FingerprintSha256=Key(2).FingerprintSha256},null,default));
        Assert.False(await hosts.ValidateAsync(Endpoint with {Host="server\nattacker"},Key(),null,default));
    }

    private static TerminalCaptureSession Capture() => new()
    {
        InitialColumns=100,InitialRows=30,TransportId="pty",Events=
        [new(){Kind=TerminalCaptureEventKind.Output,Data="Visible terminal output\r\n"u8.ToArray(),OffsetMilliseconds=0},
         new(){Kind=TerminalCaptureEventKind.Input,Data="NEVER_PERSIST_THIS_RAW_PASSWORD"u8.ToArray(),OffsetMilliseconds=10},
         new(){Kind=TerminalCaptureEventKind.Resize,Columns=80,Rows=24,OffsetMilliseconds=20},
         new(){Kind=TerminalCaptureEventKind.Exit,ExitCode=0,OffsetMilliseconds=40}]
    };

    [Fact]
    public async Task EncryptedRecoveryRoundTripsThroughANewServiceWithoutRawInput()
    {
        using var temp=new AcceptanceDirectory();var vault=new MemoryVault();var id=Guid.NewGuid();
        using(var keys=new RecoveryKeyStore(temp.Path,vault))
        {
            var store=new CaptureRecoveryStore(temp.Path,keys);await store.SaveAsync(id,new("Sensitive title","profile",Capture()));
            var data=await File.ReadAllBytesAsync(Directory.GetFiles(temp.File("recovery")).Single());
            Assert.DoesNotContain("Sensitive title",Encoding.UTF8.GetString(data));Assert.DoesNotContain("Visible terminal output",Encoding.UTF8.GetString(data));
            Assert.Single(vault.Values);Assert.Single(store.List());
        }
        using(var keys=new RecoveryKeyStore(temp.Path,vault))
        {
            var store=new CaptureRecoveryStore(temp.Path,keys);var capture=await store.LoadAsync(id);
            Assert.Equal("Sensitive title",capture.Title);Assert.Equal(3,capture.Session.Events.Count);
            Assert.DoesNotContain(capture.Session.Events,e=>e.Kind==TerminalCaptureEventKind.Input);
            Assert.Equal("Visible terminal output\r\n",Encoding.UTF8.GetString(capture.Session.Events[0].Data!));
            await store.DeleteAsync(id);Assert.Empty(store.List());
        }
    }

    [Fact]
    public async Task RecoveryRejectsTamperingAndIdentitySubstitution()
    {
        using var temp=new AcceptanceDirectory();using var keys=new RecoveryKeyStore(temp.Path,new MemoryVault());var store=new CaptureRecoveryStore(temp.Path,keys);
        var id=Guid.NewGuid();await store.SaveAsync(id,new("test","test",Capture()));
        string file=Directory.GetFiles(temp.File("recovery")).Single();var bytes=await File.ReadAllBytesAsync(file);
        var other=Guid.NewGuid();await File.WriteAllBytesAsync(temp.File($"recovery/{other:N}.tsrec"),bytes);
        await Assert.ThrowsAnyAsync<CryptographicException>(()=>store.LoadAsync(other));
        bytes[^1]^=1;await File.WriteAllBytesAsync(file,bytes);
        await Assert.ThrowsAnyAsync<CryptographicException>(()=>store.LoadAsync(id));
    }

    [Fact]
    public async Task ExplicitPassphraseFallbackSurvivesRestartAndRejectsWrongPassphrases()
    {
        using var temp=new AcceptanceDirectory();var vault=new MemoryVault {Unavailable=true};var id=Guid.NewGuid();
        using(var keys=new RecoveryKeyStore(temp.Path,vault){PassphrasePrompt=(create,_)=>{Assert.True(create);return Task.FromResult<string?>("a sufficiently long test passphrase");}})
            await new CaptureRecoveryStore(temp.Path,keys).SaveAsync(id,new("test","test",Capture()));
        Assert.DoesNotContain("a sufficiently long test passphrase",await File.ReadAllTextAsync(temp.File("recovery-key.json")));
        using(var wrong=new RecoveryKeyStore(temp.Path,vault){PassphrasePrompt=(_,_)=>Task.FromResult<string?>("a completely wrong test passphrase")})
            await Assert.ThrowsAnyAsync<CryptographicException>(()=>new CaptureRecoveryStore(temp.Path,wrong).LoadAsync(id));
        using(var keys=new RecoveryKeyStore(temp.Path,vault){PassphrasePrompt=(create,_)=>{Assert.False(create);return Task.FromResult<string?>("a sufficiently long test passphrase");}})
            Assert.Equal("test",(await new CaptureRecoveryStore(temp.Path,keys).LoadAsync(id)).Title);
    }

    [Fact]
    public async Task UnavailableVaultWithoutExplicitFallbackDoesNotCreateAKeyFile()
    {
        using var temp=new AcceptanceDirectory();using var keys=new RecoveryKeyStore(temp.Path,new MemoryVault{Unavailable=true});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>keys.UnlockAsync());Assert.False(File.Exists(temp.File("recovery-key.json")));
    }

    [Theory]
    [InlineData(TerminalSessionLogFormat.RawBytes)]
    [InlineData(TerminalSessionLogFormat.PlainText)]
    public async Task SessionLogsFlushCompleteUtf8AndStripControlSequencesOnlyInPlainMode(TerminalSessionLogFormat format)
    {
        using var temp=new AcceptanceDirectory();var reports=new ConcurrentQueue<string>();
        var bytes=Encoding.UTF8.GetBytes("\x1b[31mZażółć 🍃\x1b[0m\r\n\x1b]0;private-title\aafter\n");
        await using(var log=new SessionOutputLog(new(){Enabled=true,FilePath=temp.File("session.log"),Format=format,FlushFrequently=true},reports.Enqueue))
            foreach(var b in bytes)log.Receive(new[]{b});
        Assert.Empty(reports);var actual=await File.ReadAllBytesAsync(temp.File("session.log"));
        Assert.Equal(format==TerminalSessionLogFormat.RawBytes?bytes:Encoding.UTF8.GetBytes("Zażółć 🍃\r\nafter\n"),actual);
        if(!OperatingSystem.IsWindows())Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite,File.GetUnixFileMode(temp.File("session.log")));
    }

    [Fact]
    public async Task SessionLogRotatesAtItsBoundWithoutReplacingTheLiveTerminal()
    {
        using var temp=new AcceptanceDirectory();var reports=new ConcurrentQueue<string>();
        await using(var file=File.Create(temp.File("session.log")))file.SetLength(64L*1024*1024);
        await using(var log=new SessionOutputLog(new(){Enabled=true,FilePath=temp.File("session.log"),Format=TerminalSessionLogFormat.RawBytes},reports.Enqueue))log.Receive("new output"u8);
        Assert.Empty(reports);Assert.Equal("new output",await File.ReadAllTextAsync(temp.File("session.log")));
        Assert.Equal(64L*1024*1024,new FileInfo(temp.File("session.log.1")).Length);
    }

    [Fact]
    public async Task UnwritableLogReportsFailureWithoutThrowingIntoTerminalOutput()
    {
        using var temp=new AcceptanceDirectory();var reports=new ConcurrentQueue<string>();
        await using(var log=new SessionOutputLog(new(){Enabled=true,FilePath=temp.Path},reports.Enqueue))log.Receive("output"u8);
        Assert.Single(reports);Assert.Contains("Session log failure",reports.Single());
    }

    [AvaloniaFact]
    public async Task ProfileSecretsAreVaultScopedAndNeverSerialized()
    {
        using var temp=new AcceptanceDirectory();var vault=new MemoryVault();var repo=new ProfileRepository(temp.Path,vault);repo.AddDesignProfiles();
        var editor=repo.CreateEditor("staging");editor.SshPassword="not-a-real-test-password";editor.SshProxyPassword="test-proxy-secret";
        repo.RememberCredentials.Add("staging");await repo.SaveAsync(editor);
        string json=await File.ReadAllTextAsync(temp.File("profiles.json"));Assert.DoesNotContain("not-a-real-test-password",json);Assert.DoesNotContain("test-proxy-secret",json);
        Assert.Equal("not-a-real-test-password",vault.Values[ProfileRepository.CredentialId(repo.Get("staging"))]);
        var changed=repo.Get("staging") with {Transport=repo.Get("staging").Transport with {Ssh=repo.Get("staging").Transport.Ssh with {Host="different.invalid"}}};
        Assert.NotEqual(ProfileRepository.CredentialId(repo.Get("staging")),ProfileRepository.CredentialId(changed));
        await repo.ForgetCredentialsAsync("staging");Assert.Empty(vault.Values);Assert.DoesNotContain("staging",repo.RememberCredentials);
    }

    [AvaloniaFact]
    public async Task CompactProfileEditingRetainsAdvancedForwardingsAndMultipleKeysExactlyOnce()
    {
        using var temp=new AcceptanceDirectory();var repo=new ProfileRepository(temp.Path,new MemoryVault());repo.AddDesignProfiles();
        var profile=repo.Get("staging");var ssh=profile.Transport.Ssh with
        {
            Authentication=new(){UseAgent=true,PrivateKeySecretIds=["/test/key-one","/test/key-two"]},
            PortForwardings=[new(SshPortForwardMode.Local,"127.0.0.1",12000,"localhost",80),new(SshPortForwardMode.Remote,"127.0.0.1",13000,"localhost",90),new(SshPortForwardMode.Dynamic,"127.0.0.1",14000,"",0)]
        };
        await repo.SaveAdvancedAsync(profile with {Transport=profile.Transport with {Ssh=ssh}});
        for(int i=0;i<3;i++)
        {
            var editor=repo.CreateEditor(i==1?"production":"staging");editor.FontSize=11+i;await repo.SaveAsync(editor);
            var saved=repo.Get("staging").Transport.Ssh;Assert.Equal(3,saved.PortForwardings.Count);
            Assert.Equal(2,saved.Authentication.PrivateKeySecretIds.Count);
            Assert.Equal(3,saved.PortForwardings.Distinct().Count());
        }
    }

    [Fact]
    public void GeneralProxySettingsAndExclusionsAreMappedExplicitly()
    {
        using var temp=new AcceptanceDirectory();var repo=new ProfileRepository(temp.Path,new MemoryVault());repo.AddDesignProfiles();var profile=repo.Get("staging");
        profile=profile with {Proxy=new(){Enabled=true,Type=TerminalSessionProxyType.Socks5,Host="proxy.invalid",Port=1080,Username="proxy-user"}};
        var options=(SshTransportOptions)repo.RuntimeOptions(profile);Assert.Equal(SshProxyType.Socks5,options.Proxy!.Type);Assert.Equal(1080,options.Proxy.Port);
        profile=profile with {Proxy=profile.Proxy with {ExcludedHosts=["*.example"]}};
        Assert.Null(((SshTransportOptions)repo.RuntimeOptions(profile)).Proxy);
        profile=profile with {Proxy=profile.Proxy with {Type=TerminalSessionProxyType.Command,ExcludedHosts=[]}};
        Assert.Throws<NotSupportedException>(()=>repo.RuntimeOptions(profile));
    }

    [Theory]
    [InlineData("../secret")][InlineData("a/b")][InlineData("a\\b")][InlineData(".")][InlineData("..")] [InlineData("bad\nname")]
    public void RemoteNamesCannotEscapeTheSelectedDirectory(string name) => Assert.Throws<ArgumentException>(()=>SftpWorkspace.ChildPath("/home/user",name));

    [Fact]
    public void OutputOnlyExportRetainsReplayTimingWithoutKeystrokes()
    {
        var result=CapturePrivacy.OutputOnly(Capture());Assert.Equal(40,result.DurationMilliseconds);Assert.Equal(3,result.Events.Count);
        Assert.DoesNotContain("NEVER_PERSIST",JsonSerializer.Serialize(result));
    }
    [AvaloniaFact]
    public async Task VisitingAnotherCompactProfileDoesNotDiscardPreviouslyEditedAdvancedRules()
    {
        using var directory=new AcceptanceDirectory();var profiles=new ProfileRepository(directory.Path,new MemoryVault());profiles.AddDesignProfiles();
        var original=profiles.Get("staging") with {Transport=profiles.Get("staging").Transport with {Ssh=profiles.Get("staging").Transport.Ssh with
        {PortForwardings=[new(SshPortForwardMode.Local,"127.0.0.1",19001,"localhost",80),new(SshPortForwardMode.Remote,"127.0.0.1",19002,"localhost",81)],
         Authentication=new(){UseAgent=true,PrivateKeySecretIds=["/key/a","/key/b"]}}}};
        await profiles.SaveAdvancedAsync(original);
        var editor=profiles.CreateEditor("staging");editor.SessionName="renamed staging";
        editor.SelectedProfile=editor.Profiles.Single(p=>p.Id=="production");
        await profiles.SaveAsync(editor);
        Assert.Equal(2,profiles.Get("staging").Transport.Ssh.PortForwardings.Count);
        Assert.Equal(new[]{"/key/a","/key/b"},profiles.Get("staging").Transport.Ssh.Authentication.PrivateKeySecretIds);
        Assert.Equal("renamed staging",profiles.Get("staging").DisplayName);
    }

}
