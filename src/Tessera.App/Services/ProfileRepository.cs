using System.Text.Json;
using Avalonia.Threading;
using RoyalTerminal.Avalonia.Settings;
using RoyalTerminal.Terminal;
using Tessera.Core;

namespace Tessera.Services;

/// <summary>Durable connection descriptions with strictly runtime-only password resolution.</summary>
public sealed class ProfileRepository(string directory) : ISshCredentialProvider
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Dictionary<string, string> _passwords = new(StringComparer.Ordinal);
    public TerminalSessionProfilesDocument Document { get; private set; } = new() { DefaultProfileId = "local", Profiles = [Local()] };
    public HashSet<string> Production { get; private set; } = [];
    public Func<string, Task<string?>>? PasswordPrompt { get; set; }
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
            Document = TerminalSessionProfileSerializer.FromJson(await File.ReadAllTextAsync(path));
            if (Document.Profiles.Count is < 1 or > 512 || Document.Profiles.Select(p => p.Id).Distinct().Count() != Document.Profiles.Count)
                throw new InvalidDataException("Invalid profile count or identities.");
        }
        var flags = Path.Combine(directory, "production-profiles.json");
        if (File.Exists(flags)) Production = JsonSerializer.Deserialize<HashSet<string>>(await File.ReadAllTextAsync(flags)) ?? [];
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
        if (selected is not null && !string.IsNullOrEmpty(editor.SshPassword))
            _passwords["tessera/" + selected + "/password"] = editor.SshPassword;
        var clean = doc.Profiles.Select(profile =>
        {
            var ssh = profile.Transport.Ssh;
            if (profile.Id == selected)
            {
                ssh = ssh with
                {
                    Authentication = ssh.Authentication with
                    {
                        PasswordSecretId = "tessera/" + profile.Id + "/password",
                        PrivateKeySecretIds = string.IsNullOrWhiteSpace(editor.SshPrivateKeyPath)
                            ? ssh.Authentication.PrivateKeySecretIds : [editor.SshPrivateKeyPath]
                    }
                };
            }
            return profile with { Transport = profile.Transport with { Ssh = ssh with { Proxy = ssh.Proxy is {} proxy ? proxy with { Password = null } : null } } };
        }).ToList();
        var candidate = TerminalSessionProfileSerializer.FromJson(TerminalSessionProfileSerializer.ToJson(doc with { Profiles = clean }));
        await _saveGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(directory);
            await SaveFileAsync(Path.Combine(directory, "profiles.json"), TerminalSessionProfileSerializer.ToJson(candidate));
            await SaveFileAsync(Path.Combine(directory, "production-profiles.json"), JsonSerializer.Serialize(Production));
            Document = candidate;
        }
        finally { _saveGate.Release(); }
        editor.SshPassword = ""; editor.SshProxyPassword = "";
        editor.MarkSaved("Saved. Passwords remain in memory for this application session only.");
        Saved?.Invoke();
    }

    private static async Task SaveFileAsync(string path, string content)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, content);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public async ValueTask<SshResolvedCredentials> ResolveAsync(SshCredentialRequest request, CancellationToken cancellationToken = default)
    {
        string? password = null;
        if (request.Authentication.UsePassword)
        {
            var id = request.Authentication.PasswordSecretId ?? "";
            if (!_passwords.TryGetValue(id, out password) && PasswordPrompt is {} prompt)
            {
                // Avalonia's async dispatcher overload unwraps the returned task.
                password = await Dispatcher.UIThread.InvokeAsync(() => prompt($"Password for {request.Endpoint.Username}@{request.Endpoint.Host}"));
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (password is null) throw new OperationCanceledException("SSH authentication cancelled.");
        }
        return new SshResolvedCredentials(password, request.Authentication.PrivateKeySecretIds.Where(File.Exists).ToArray(), request.Authentication.UseAgent);
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
