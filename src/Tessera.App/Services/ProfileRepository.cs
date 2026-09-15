using System.Text.Json;
using System.Collections.Concurrent;
using Avalonia.Threading;
using RoyalTerminal.Avalonia.Settings;
using RoyalTerminal.Terminal;
using Tessera.Core;

namespace Tessera.Services;

/// <summary>Durable connection descriptions; passwords use runtime memory or explicit OS-vault opt-in.</summary>
public sealed class ProfileRepository(string directory, ICredentialVault? vault = null) : ISshCredentialProvider
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _passwords = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _proxyPasswords = new(StringComparer.Ordinal);
    public TerminalSessionProfilesDocument Document { get; private set; } = new() { DefaultProfileId = "local", Profiles = [Local()] };
    public ICredentialVault Vault { get; } = vault ?? new OsCredentialVault();
    public KnownHostRepository KnownHosts { get; } = new(directory);
    public HashSet<string> RememberCredentials { get; private set; } = [];
    public bool KeyboardInteractiveEnabled { get; set; } = true;
    public Func<SshChallenge, CancellationToken, Task<string[]?>>? ChallengePrompt { get; set; }
    public HashSet<string> Production { get; private set; } = [];
    public Func<string, Task<string?>>? PasswordPrompt { get; set; }
    public Func<string, CancellationToken, Task<string?>>? CredentialPrompt { get; set; }
    public event Action? Saved;

    public static TerminalSessionProfile Local() => new()
    {
        Id = "local", DisplayName = "Local shell",
        Transport = new() { Pty = new() { WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) } },
        Appearance = new() { FontSize = 10 }
    };

    public TerminalSessionProfile Get(string id) => Document.Profiles.FirstOrDefault(p => p.Id == id)
        ?? throw new InvalidOperationException($"Profile '{id}' no longer exists. Choose a connection profile before reconnecting.");

    public async Task LoadAsync()
    {
        var path = Path.Combine(directory, "profiles.json");
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Profile file is too large.");
            Document = TerminalSessionProfileSerializer.FromJson(System.Text.Encoding.UTF8.GetString(await AtomicFile.ReadBytesAsync(path,4*1024*1024)));
            if (Document.Profiles.Count is < 1 or > 512 || Document.Profiles.Select(p => p.Id).Distinct().Count() != Document.Profiles.Count)
                throw new InvalidDataException("Invalid profile count or identities.");
        }
        RememberCredentials = await AtomicFile.ReadJsonAsync<HashSet<string>>(Path.Combine(directory, "vault-profiles.json"), 65536) ?? [];
        var flags = Path.Combine(directory, "production-profiles.json");
        if (File.Exists(flags)) Production = await AtomicFile.ReadJsonAsync<HashSet<string>>(flags,65536) ?? [];
    }

    public TerminalSettingsPanelState CreateEditor(string? profileId = null)
    {
        var editor = new TerminalSettingsPanelState();
        editor.LoadDocument(Document);
        if (profileId is not null)
            editor.SelectedProfile = editor.Profiles.FirstOrDefault(p => p.Id == profileId) ?? editor.SelectedProfile;
        return editor;
    }

    public async Task SaveAsync(TerminalSettingsPanelState editor)
    {
        var doc = editor.BuildDocument();
        var selected = editor.SelectedProfile?.Id;

        var clean = doc.Profiles.Select(profile =>
        {
            var ssh = profile.Transport.Ssh;
            var original = Document.Profiles.FirstOrDefault(p => p.Id == profile.Id);
            if(original is not null)
            {
                // The upstream compact editor owns only the first local forward and first key field.
                // Retain the additional rules/keys exposed by the complete advanced editor.
                var skippedFirstLocal=false;
                var additional=original.Transport.Ssh.PortForwardings.Where(f =>
                {
                    if(f.Mode==SshPortForwardMode.Local&&!skippedFirstLocal){skippedFirstLocal=true;return false;}return true;
                });
                if(ssh.PortForwardings.Count <= 1)
                    ssh=ssh with {PortForwardings=ssh.PortForwardings.Concat(additional).Distinct().ToList()};
                if(ssh.Authentication.PrivateKeySecretIds.Count==1)
                    ssh=ssh with {Authentication=ssh.Authentication with {PrivateKeySecretIds=ssh.Authentication.PrivateKeySecretIds.Concat(original.Transport.Ssh.Authentication.PrivateKeySecretIds.Skip(1)).Distinct(StringComparer.Ordinal).ToList()}};
                else if(ssh.Authentication.UseAgent == original.Transport.Ssh.Authentication.UseAgent && ssh.Authentication.UsePassword == original.Transport.Ssh.Authentication.UsePassword)
                    ssh=ssh with {Authentication=ssh.Authentication with {PrivateKeySecretIds=original.Transport.Ssh.Authentication.PrivateKeySecretIds.ToList()}};
            }
            if (profile.Id == selected)
            {
                ssh = ssh with
                {
                    Authentication = ssh.Authentication with
                    {
                        PasswordSecretId = "tessera/" + profile.Id + "/password",
                        PrivateKeySecretIds = string.IsNullOrWhiteSpace(editor.SshPrivateKeyPath)
                            ? ssh.Authentication.PrivateKeySecretIds : new[]{editor.SshPrivateKeyPath}.Concat(ssh.Authentication.PrivateKeySecretIds.Skip(1)).ToList()
                    }
                };
            }
            return profile with { Transport = profile.Transport with { Ssh = ssh with { Proxy = ssh.Proxy is {} proxy ? proxy with { Password = null } : null } } };
        }).ToList();
        var candidate = TerminalSessionProfileSerializer.FromJson(TerminalSessionProfileSerializer.ToJson(doc with { Profiles = clean }));
        foreach(var profile in candidate.Profiles) ValidateProfile(profile);
        if(selected is not null)
        {
            var selectedProfile=candidate.Profiles.Single(p=>p.Id==selected);
            if(!string.IsNullOrEmpty(editor.SshPassword)) _passwords[CredentialId(selectedProfile)]=editor.SshPassword;
            if(!string.IsNullOrEmpty(editor.SshProxyPassword)) _proxyPasswords[ProxyCredentialId(selectedProfile)]=editor.SshProxyPassword;
        }
        if (selected is not null && RememberCredentials.Contains(selected))
        {
            var selectedProfile = candidate.Profiles.Single(p => p.Id == selected);
            if (!string.IsNullOrEmpty(editor.SshPassword)) await Vault.WriteAsync(CredentialId(selectedProfile), editor.SshPassword);
            if (!string.IsNullOrEmpty(editor.SshProxyPassword)) await Vault.WriteAsync(ProxyCredentialId(selectedProfile), editor.SshProxyPassword);
        }
        await _saveGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(directory);
            await SaveFileAsync(Path.Combine(directory, "profiles.json"), TerminalSessionProfileSerializer.ToJson(candidate));
            await SaveFileAsync(Path.Combine(directory, "production-profiles.json"), JsonSerializer.Serialize(Production));
            await AtomicFile.WriteJsonAsync(Path.Combine(directory, "vault-profiles.json"), RememberCredentials);
            Document = candidate;
        }
        finally { _saveGate.Release(); }
        editor.SshPassword = ""; editor.SshProxyPassword = "";
        editor.MarkSaved("Saved. Passwords use the OS vault only for profiles with Remember enabled; all other credentials are session-only.");
        Saved?.Invoke();
    }

    private static Task SaveFileAsync(string path, string content)
    {
        var bytes=System.Text.Encoding.UTF8.GetBytes(content);
        if(bytes.Length>4*1024*1024)throw new InvalidDataException("Profile document exceeds its 4 MiB storage limit.");
        return AtomicFile.WriteAsync(path,bytes);
    }

    public static string CredentialId(TerminalSessionProfile profile)
    {
        var ssh = profile.Transport.Ssh;
        var scope = profile.Id + "|" + ssh.Host.ToLowerInvariant() + "|" + ssh.Port + "|" + ssh.Username;
        return "ssh/" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(scope)));
    }
    public async Task<string?> PromptPasswordAsync(string title, CancellationToken token)
    {
        if (CredentialPrompt is {} cancellable) return await Dispatcher.UIThread.InvokeAsync(() => cancellable(title, token).WaitAsync(token));
        if (PasswordPrompt is not {} prompt) return null;
        return await Dispatcher.UIThread.InvokeAsync(() => prompt(title).WaitAsync(token));
    }
    public async ValueTask<SshResolvedCredentials> ResolveAsync(SshCredentialRequest request, CancellationToken cancellationToken = default)
    {
        string? password = null;
        if (request.Authentication.UsePassword)
        {
            var id = request.Authentication.PasswordSecretId ?? "";
            var profile = Document.Profiles.FirstOrDefault(p => p.Transport.Ssh.Authentication.PasswordSecretId == id &&
                p.Transport.Ssh.Host.Equals(request.Endpoint.Host,StringComparison.OrdinalIgnoreCase) && p.Transport.Ssh.Port == request.Endpoint.Port && p.Transport.Ssh.Username == request.Endpoint.Username);
            if(profile is not null)_passwords.TryGetValue(CredentialId(profile),out password);
            if (password is null && profile is not null && RememberCredentials.Contains(profile.Id))
                password = await Vault.ReadAsync(CredentialId(profile), cancellationToken).ConfigureAwait(false);
            if (password is null) password = await PromptPasswordAsync($"Password for {request.Endpoint.Username}@{request.Endpoint.Host}", cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (password is null) throw new OperationCanceledException("SSH authentication cancelled.");
        }
        var keys = request.Authentication.PrivateKeySecretIds.ToArray();
        foreach (var key in keys) if (!File.Exists(key)) throw new FileNotFoundException("SSH private key was not found.", key);
        return new SshResolvedCredentials(password, keys, request.Authentication.UseAgent);
    }
    public async Task<ITerminalTransportOptions> RuntimeOptionsAsync(TerminalSessionProfile profile, CancellationToken token)
    {
        var options = RuntimeOptions(profile);
        if (options is SshTransportOptions ssh && ssh.Proxy is {} proxy && string.IsNullOrEmpty(proxy.Password) && RememberCredentials.Contains(profile.Id))
            return ssh with { Proxy = proxy with { Password = await Vault.ReadAsync(ProxyCredentialId(profile), token) } };
        return options;
    }
    public async Task ForgetCredentialsAsync(string profileId, CancellationToken token = default)
    {
        var profile = Get(profileId);
        await Vault.DeleteAsync(CredentialId(profile), token);
        await Vault.DeleteAsync(ProxyCredentialId(profile), token);
        _passwords.TryRemove(CredentialId(profile),out _); _proxyPasswords.TryRemove(ProxyCredentialId(profile),out _);
        RememberCredentials.Remove(profileId);
        await AtomicFile.WriteJsonAsync(Path.Combine(directory, "vault-profiles.json"), RememberCredentials, token);
    }

    public ITerminalTransportOptions RuntimeOptions(TerminalSessionProfile profile)
    {
        ValidateProfile(profile);
        var options = TerminalSessionProfileMapper.ToTransportOptions(profile);
        if(options is SshTransportOptions noProxy && noProxy.Proxy is null && profile.Proxy.Enabled && !IsProxyExcluded(profile.Proxy.ExcludedHosts, noProxy.Endpoint.Host))
        {
            var envelopeProxy=profile.Proxy;
            options=noProxy with {Proxy=new SshProxyOptions(envelopeProxy.Type switch
            {TerminalSessionProxyType.Http=>SshProxyType.Http,TerminalSessionProxyType.Socks4=>SshProxyType.Socks4,TerminalSessionProxyType.Socks5=>SshProxyType.Socks5,_=>throw new NotSupportedException("Command proxies are not implemented by RoyalTerminal's SSH.NET transport.")},envelopeProxy.Host??"",envelopeProxy.Port,envelopeProxy.Username,null)};
        }
        if(options is SshTransportOptions ssh && ssh.Proxy is {} proxy && _proxyPasswords.TryGetValue(ProxyCredentialId(profile), out var password))
            return ssh with { Proxy = proxy with { Password = password } };
        return options;
    }

    private static bool IsProxyExcluded(IEnumerable<string> exclusions, string host)
    {
        foreach (var pattern in exclusions.Take(256))
        {
            if (pattern.Length > 512) throw new InvalidDataException("Proxy exclusion is too long.");
            var expression = "\\A" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "\\z";
            if (System.Text.RegularExpressions.Regex.IsMatch(host, expression, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))) return true;
        }
        return false;
    }

    public static string ProxyCredentialId(TerminalSessionProfile profile)
    {
        var proxy=profile.Transport.Ssh.Proxy;
        var scope=CredentialId(profile)+"|"+(proxy?.Host??profile.Proxy.Host)+"|"+(proxy?.Port??profile.Proxy.Port)+"|"+(proxy?.Username??profile.Proxy.Username);
        return "proxy/"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(scope)));
    }
    public static void ValidateProfile(TerminalSessionProfile profile)
    {
        if(profile.Layout.ScrollbackLimit is <0 or >1000000 || profile.Layout.Columns is <1 or >4096 || profile.Layout.Rows is <1 or >4096)
            throw new InvalidDataException("Invalid terminal dimensions or scrollback limit (maximum 1,000,000 rows).");
        if(profile.Transport.Ssh.PortForwardings.Count>128 || profile.Transport.Ssh.Authentication.PrivateKeySecretIds.Count>32)
            throw new InvalidDataException("At most 128 port forwards and 32 private keys are supported per profile.");
        if(profile.Logging.Enabled&&string.IsNullOrWhiteSpace(profile.Logging.FilePath))throw new InvalidDataException("Enabled logging requires a file path.");
        _=TerminalSessionProfileMapper.ToTransportOptions(profile);
    }
    public async Task SaveAdvancedAsync(TerminalSessionProfile replacement,CancellationToken token=default)
    {
        if(!Document.Profiles.Any(p=>p.Id==replacement.Id))throw new InvalidDataException("Keep the original profile identity.");
        if(!string.IsNullOrEmpty(replacement.Transport.Ssh.Proxy?.Password))throw new InvalidDataException("Proxy passwords must use the secure credential fields, not the JSON document.");
        ValidateProfile(replacement);
        var candidate=TerminalSessionProfileSerializer.FromJson(TerminalSessionProfileSerializer.ToJson(Document with {Profiles=Document.Profiles.Select(p=>p.Id==replacement.Id?replacement:p).ToList()}));
        await _saveGate.WaitAsync(token);
        try {await SaveFileAsync(Path.Combine(directory,"profiles.json"),TerminalSessionProfileSerializer.ToJson(candidate));Document=candidate;}
        finally {_saveGate.Release();}
        Saved?.Invoke();
    }

    public void AddDesignProfiles()
    {
        Document = Document with
        {
            Profiles = [Local(),
                new() { Id = "staging", DisplayName = "edge-staging", Transport = new() { TransportId = "ssh", Ssh = new() { Host = "edge-staging.example", Username = "deploy", Authentication = new() { UseAgent = true } } } },
                new() { Id = "production", DisplayName = "production-eu", Transport = new() { TransportId = "ssh", Ssh = new() { Host = "production-eu.example", Username = "deploy", Authentication = new() { UseAgent = true } } } },
                new() { Id = "serial", DisplayName = "Lab · serial console", Transport = new() { TransportId = "serial", Serial = new() { PortName = "/dev/ttyUSB0", BaudRate = 115200 } } }]
        };
        Production.Add("production");
    }
}
