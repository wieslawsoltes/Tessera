using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using Renci.SshNet;
using RoyalTerminal.Terminal;
using RoyalTerminal.Terminal.Transport.Ssh;
using RoyalTerminal.Terminal.Transport.Ssh.SshNet;
using RoyalTerminal.Terminal.Transport.Ssh.SshNet.Agent;
using Tessera.Core;

namespace Tessera.Services;

public enum HostKeyDecision { Reject, TrustOnce, TrustAndSave }
public sealed record HostKeyChallenge(string Host, int Port, string User, string Algorithm, string Fingerprint, SshKnownHostTrustStatus Status);
public sealed record SshPrompt(string Label, bool Echo);
public sealed record SshChallenge(string Host, string User, string Instructions, SshPrompt[] Prompts, int Round);

/// <summary>Explicit TOFU with native known_hosts parsing. Changed/revoked keys never receive an acceptance button.</summary>
public sealed class KnownHostRepository(string directory)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string FilePath { get; } = Path.Combine(directory, "known_hosts");
    public Func<HostKeyChallenge, CancellationToken, Task<HostKeyDecision>>? Prompt { get; set; }
    public IReadOnlyList<string>? SystemFilesOverride { get; set; }

    public async Task<bool> ValidateAsync(SshEndpointOptions endpoint, SshHostKeyInfo key, string? expected, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(key.HostKeyBase64) || key.HostKeyBase64.Length > 32768 ||
            string.IsNullOrWhiteSpace(endpoint.Host) || endpoint.Host.Any(c => char.IsWhiteSpace(c) || ",*?!|[]#".Contains(c)) ||
            string.IsNullOrWhiteSpace(key.HostKeyAlgorithm) || key.HostKeyAlgorithm.Length > 128 || key.HostKeyAlgorithm.Any(char.IsWhiteSpace)) return false;
        byte[] material;
        try { material = Convert.FromBase64String(key.HostKeyBase64); } catch (FormatException) { return false; }
        var fingerprint = Convert.ToBase64String(SHA256.HashData(material)).TrimEnd('=');
        if (fingerprint != Normalize(key.FingerprintSha256)) return false;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var files = (SystemFilesOverride ?? KnownHostsSshHostKeyValidator.GetDefaultKnownHostsFiles()).Append(FilePath).ToArray();
            foreach (var file in files)
                if (File.Exists(file) && new FileInfo(file).Length > 4 * 1024 * 1024) throw new InvalidDataException("A known_hosts file exceeds the 4 MiB safety limit.");
            var validator = new KnownHostsSshHostKeyValidator(files);
            var status = validator.GetTrustStatus(endpoint, key);
            if (status is SshKnownHostTrustStatus.Changed or SshKnownHostTrustStatus.Revoked or SshKnownHostTrustStatus.InvalidPresentedKey)
                throw new InvalidOperationException($"SSH host key {status} for {endpoint.Host}:{endpoint.Port}. Verify the new fingerprint out of band and repair known_hosts explicitly.");
            if (!string.IsNullOrWhiteSpace(expected)) return fingerprint == Normalize(expected);
            if (status == SshKnownHostTrustStatus.Trusted) return true;
            if (Prompt is null) return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var challenge = new HostKeyChallenge(endpoint.Host, endpoint.Port, endpoint.Username, key.HostKeyAlgorithm, "SHA256:" + fingerprint, status);
            var decision = await Prompt(challenge, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (decision is not (HostKeyDecision.TrustOnce or HostKeyDecision.TrustAndSave)) return false;
            if (decision == HostKeyDecision.TrustAndSave)
            {
                string previous = File.Exists(FilePath) ? await File.ReadAllTextAsync(FilePath, token).ConfigureAwait(false) : "";
                string host = endpoint.Port == 22 ? endpoint.Host : $"[{endpoint.Host}]:{endpoint.Port}";
                await AtomicFile.WriteAsync(FilePath, Encoding.UTF8.GetBytes(previous.TrimEnd() + "\n" + host + " " + key.HostKeyAlgorithm + " " + key.HostKeyBase64 + "\n"), token).ConfigureAwait(false);
            }
            return true;
        }
        finally { _gate.Release(); }
    }
    private static string Normalize(string? fingerprint) => (fingerprint ?? "").Trim().Replace("SHA256:", "", StringComparison.OrdinalIgnoreCase).TrimEnd('=');
}

/// <summary>One interaction lifetime per connection attempt, shared by the host-key validator and MFA contributor.</summary>
public sealed class SshConnectionContext(ProfileRepository profiles) : ISshHostKeyValidator, ISshNetAuthenticationMethodContributor, ISshCredentialProvider, IDisposable
{
    private CancellationTokenSource _attempt = new();
    private string? _expected;
    private int _rounds;
    private readonly List<PrivateKeyFile> _keys = [];
    public void Begin(SshTransportOptions options, CancellationToken token)
    {
        _attempt.Cancel(); _attempt.Dispose(); _attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
        _expected = options.ExpectedHostKeyFingerprintSha256; _rounds = 0;
        foreach (var key in _keys) key.Dispose(); _keys.Clear();
    }
    public bool IsTrusted(SshEndpointOptions endpoint, SshHostKeyInfo key)
    {
        if (Dispatcher.UIThread.CheckAccess()) throw new InvalidOperationException("SSH trust must run on the connection worker, never block the UI thread.");
        return profiles.KnownHosts.ValidateAsync(endpoint, key, _expected, _attempt.Token).GetAwaiter().GetResult();
    }
    public async ValueTask<SshResolvedCredentials> ResolveAsync(SshCredentialRequest request, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _attempt.Token);
        var credentials = await profiles.ResolveAsync(request, linked.Token).ConfigureAwait(false);
        foreach (var path in credentials.PrivateKeyPemOrPath)
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Private key file exceeds 1 MiB.");
            try { _keys.Add(new PrivateKeyFile(path)); }
            catch (Renci.SshNet.Common.SshPassPhraseNullOrEmptyException)
            {
                if (profiles.PasswordPrompt is null && profiles.CredentialPrompt is null) throw new InvalidOperationException("The private key requires a passphrase.");
                string? passphrase = await profiles.PromptPasswordAsync("Passphrase for " + Path.GetFileName(path), linked.Token).ConfigureAwait(false);
                if (passphrase is null) throw new OperationCanceledException(linked.Token);
                _keys.Add(new PrivateKeyFile(path, passphrase));
            }
        }
        // Key objects are already decrypted; the contributor supplies them without persisting passphrases.
        return credentials with { PrivateKeyPemOrPath = [] };
    }
    public IReadOnlyList<AuthenticationMethod> CreateAuthenticationMethods(SshTransportOptions options, SshResolvedCredentials credentials)
    {
        var methods = new List<AuthenticationMethod>();
        if (_keys.Count > 0) methods.Add(new PrivateKeyAuthenticationMethod(options.Endpoint.Username, _keys.Cast<IPrivateKeySource>().ToArray()));
        methods.AddRange(new SshNetAgentAuthenticationMethodContributor().CreateAuthenticationMethods(options, credentials));
        if (profiles.KeyboardInteractiveEnabled)
        {
            var interactive = new KeyboardInteractiveAuthenticationMethod(options.Endpoint.Username);
            interactive.AuthenticationPrompt += (_, args) =>
            {
                if (++_rounds > 8 || args.Prompts.Count > 16) throw new InvalidOperationException("SSH authentication exceeded its challenge limit.");
                if (profiles.ChallengePrompt is null) throw new OperationCanceledException("No MFA prompt handler is available.");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_attempt.Token);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                var challenge = new SshChallenge(options.Endpoint.Host, options.Endpoint.Username, SafeLabel(args.Instruction),
                    args.Prompts.Select(p => new SshPrompt(SafeLabel(p.Request), p.IsEchoed)).ToArray(), _rounds);
                var answers = profiles.ChallengePrompt(challenge, timeout.Token).GetAwaiter().GetResult();
                timeout.Token.ThrowIfCancellationRequested();
                if (answers is null || answers.Length != args.Prompts.Count) throw new OperationCanceledException(timeout.Token);
                for (int i = 0; i < answers.Length; i++)
                {
                    if (answers[i].Length > 4096) throw new InvalidOperationException("SSH challenge response exceeds 4,096 characters.");
                    args.Prompts[i].Response = answers[i];
                }
                Array.Clear(answers); // No MFA response is stored in history, profile documents, or the credential vault.
            };
            methods.Add(interactive);
        }
        return methods;
    }
    private static string SafeLabel(string text) => new(text.Where(c => !char.IsControl(c) || c is '\n' or '\t').Take(4096).ToArray());
    public void Cancel() => _attempt.Cancel();
    public void Dispose() { _attempt.Cancel(); _attempt.Dispose(); foreach (var key in _keys) key.Dispose(); _keys.Clear(); }
}

public static class SshConnectionFactory
{
    public static ConnectionInfo Create(SshTransportOptions options, IEnumerable<AuthenticationMethod> methods)
    {
        var auth = methods.ToArray();
        if (auth.Length == 0) throw new InvalidOperationException("Choose an SSH authentication method.");
        var proxy = options.Proxy;
        var info = proxy is null || proxy.Type == SshProxyType.None
            ? new ConnectionInfo(options.Endpoint.Host, options.Endpoint.Port, options.Endpoint.Username, auth)
            : new ConnectionInfo(options.Endpoint.Host, options.Endpoint.Port, options.Endpoint.Username,
                proxy.Type switch { SshProxyType.Http => ProxyTypes.Http, SshProxyType.Socks4 => ProxyTypes.Socks4, SshProxyType.Socks5 => ProxyTypes.Socks5, _ => throw new NotSupportedException("Unsupported SSH proxy.") },
                proxy.Host, proxy.Port, proxy.Username ?? "", proxy.Password ?? "", auth);
        info.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.Policy.ConnectTimeoutSeconds, 1, 300));
        return info;
    }
}
