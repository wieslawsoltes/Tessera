using System.Text;
using Avalonia;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Avalonia.Capture;
using RoyalTerminal.Avalonia.Rendering;
using RoyalTerminal.Avalonia.Services;
using RoyalTerminal.Terminal;
using Tessera.Core;

namespace Tessera.Services;

/// <summary>One terminal, one session lifecycle, one capture runtime. Reparenting never starts or stops a session.</summary>
public sealed class SessionRuntime : IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly bool _design;
    private readonly StringBuilder _output = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly object _outputLock = new();
    private readonly ProfileRepository _profiles;
    private bool _disposed;
    private bool _replaySlot;
    private Task? _startup;
    private Task? _disposeTask;
    private SessionOutputLog? _log;
    public event Action<string>? Diagnostic;
    public async Task FlushLogAsync() { if(Interlocked.Exchange(ref _log, null) is {} log) await log.DisposeAsync(); }
    public string CursorShape { get; set; } = "Block";
    public bool CursorBlink { get; set; } = true;
    public string? WorkingDirectory { get; internal set; }
    public string DocumentTitle { get; set; } = "Terminal";
    public Guid Id { get; }
    public TerminalSessionProfile Profile { get; private set; }
    public GuardedTerminal Terminal { get; }
    public TerminalCaptureRuntime Capture { get; }
    private string _state = "Ready";
    public string State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            if (Profile.Logging.EventLogEnabled)
                Diagnostic?.Invoke($"{Safety.CleanTitle(DocumentTitle)} · {value}");
        }
    }
    public string? Error { get; private set; }
    public bool IsProduction { get; private set; }
    public bool Locked
    {
        get => Terminal.InputLocked;
        set { Terminal.InputLocked = value; Terminal.IsHitTestVisible = !value; if(value) BroadcastTarget = false; Changed?.Invoke(); }
    }
    public bool BroadcastTarget { get; set; }
    public bool IsReplay => _replaySlot || Capture.IsReplayEnabled;
    public bool IsRunning => Terminal.HasActiveSession && !_disposed;
    public event Action? Changed;
    public event Action<string>? Bell;

    public SessionRuntime(Guid id, TerminalSessionProfile profile, ProfileRepository profiles, bool design)
    {
        Id = id; Profile = profile; _design = design; _profiles = profiles; IsProduction = profiles.Production.Contains(profile.Id);
        Terminal = new GuardedTerminal(profiles)
        {
            Columns = 120, Rows = 36, Padding = new Thickness(14, 8, 8, 4),
            VtProcessorPreference = design ? VtProcessorPreference.Managed : VtProcessorPreference.Auto,
            PreserveScrollbackOnSessionStart = true, SixelGraphicsEnabled = true
        };
        Terminal.DataReceived += Receive;
        Terminal.ProcessExited += (_, exit) => { State = "Exited · " + exit; Changed?.Invoke(); };
        Terminal.Bell += (_, _) => { if(Profile.Behavior.EnableBellNotifications) Bell?.Invoke(Profile.DisplayName); };
        Terminal.SelectionFinalized += async (_, _) =>
        {
            if(Profile.Behavior.CopyOnSelectEnabled)
                try { await Terminal.CopySelectionAsync(); } catch(Exception ex) { Error = ex.Message; }
        };
        Capture = new TerminalCaptureRuntime(Terminal);
        Capture.StateChanged += (_, _) => Changed?.Invoke();
        Locked = IsProduction;
        ApplyProfile(profile);
    }
    public void MarkReplaySlot() { _replaySlot = true; Locked = true; State = "Replay · read-only"; }
    public void ApplyProductionPolicy(bool production)
    {
        if(IsProduction == production) return;
        IsProduction = production; BroadcastTarget = false;
        if(production) Locked = true;
        Changed?.Invoke();
    }
    public void ApplyProfile(TerminalSessionProfile profile)
    {
        Profile = profile; var a = profile.Appearance;
        Terminal.FontFamilyName = a.FontFamilyName; Terminal.FontSource = a.FontSource; Terminal.FontFilePath = a.FontFilePath ?? "";
        Terminal.TerminalFontSize = a.FontSize; Terminal.AutoScroll = a.AutoScroll; Terminal.ScrollbackLimit = profile.Layout.ScrollbackLimit;
        Terminal.BackspaceSendsControlH = profile.Behavior.BackspaceSendsControlH;
        Terminal.ReflowOnResize = profile.Behavior.ReflowOnResize; Terminal.SixelGraphicsEnabled = profile.Behavior.SixelGraphicsEnabled;
        Terminal.TextHighlightingMode = a.TextHighlightingMode;
        uint? Parse(string? value) => Avalonia.Media.Color.TryParse(value, out var c) ? c.ToUInt32() : null;
        Terminal.TextHighlightRules = a.TextHighlightRules.Where(r => r.IsEnabled).Select(r => new TerminalTextHighlightRule
        {
            Name = r.Name, Pattern = r.Pattern, Foreground = Parse(r.ForegroundColor), Background = Parse(r.BackgroundColor),
            DarkForeground = Parse(r.DarkForegroundColor), DarkBackground = Parse(r.DarkBackgroundColor)
        }).ToArray();
        var font = a.FontRendering;
        Terminal.FontSubpixelPositioning = font.SubpixelPositioning; Terminal.FontEdging = font.Edging; Terminal.FontHinting = font.Hinting;
        Terminal.FontBaselineSnap = font.BaselineSnap; Terminal.FontEmbeddedBitmaps = font.EmbeddedBitmaps; Terminal.FontEmbolden = font.Embolden;
        Terminal.FontForceAutoHinting = font.ForceAutoHinting; Terminal.FontLinearMetrics = font.LinearMetrics;
        if(Terminal.Renderer is {} renderer)
        {
            renderer.EnableTextShaping = profile.Behavior.EnableTextShaping;
            renderer.EnableLigatures = profile.Behavior.EnableLigatures;
        }
        ThemeManager.ApplyTerminal(Terminal);
        Terminal.BackgroundOpacityEnabled = a.BackgroundOpacityEnabled;
        Terminal.PasteSafetyPolicy = Enum.TryParse<TerminalPasteSafetyPolicy>(profile.Behavior.PasteSafetyPolicy, true, out var paste) && paste != TerminalPasteSafetyPolicy.None ? paste : TerminalPasteSafetyPolicy.ConfirmUnsafe;
    }
    public Task EnsureStartedAsync() => _startup ??= StartAsync(restart: false);
    public Task ReconnectAsync() => StartAsync(restart: true);
    public Task StartAsync() => StartAsync(restart: false);
    private async Task StartAsync(bool restart)
    {
        try { await _lifecycle.WaitAsync(_lifetime.Token); }
        catch(OperationCanceledException) { return; }
        try
        {
            if(_disposed || IsReplay) return;
            if(restart) { Terminal.StopPty(); await FlushLogAsync(); } else if(IsRunning) return;
            if(_design) { State = "Design fixture"; WriteFixture(); Changed?.Invoke(); return; }
            if(Profile.Logging.Enabled)
            {
                await FlushLogAsync();
                _log = new SessionOutputLog(Profile.Logging, message => Avalonia.Threading.Dispatcher.UIThread.Post(() => Diagnostic?.Invoke(message)));
            }
            State = "Connecting"; Error = null; Locked = IsProduction; Changed?.Invoke();
            var options = await _profiles.RuntimeOptionsAsync(Profile, _lifetime.Token);
            if (options is SshTransportOptions ssh)
            {
                Terminal.SecurityContext.Begin(ssh, _lifetime.Token);
                options = ssh with { ExpectedHostKeyFingerprintSha256 = null }; // The shared validator also enforces revoked-key policy.
            }
            await Terminal.StartSessionAsync(options, true, _lifetime.Token);
            Terminal.ApplyCursor(CursorShape, CursorBlink);
            if(_disposed) { Terminal.StopPty(); return; }
            State = "Connected"; Changed?.Invoke();
        }
        catch(OperationCanceledException) { State = "Cancelled"; Changed?.Invoke(); }
        catch(Exception ex)
        {
            Error = ex.Message; State = "Connection failed";
            if(!_disposed) Terminal.WriteOutput(Encoding.UTF8.GetBytes("\r\nTessera: " + Safety.CleanTitle(ex.Message) + "\r\nUse Session → Reconnect after updating this profile.\r\n"));
            Changed?.Invoke();
        }
        finally { _lifecycle.Release(); }
    }
    public void Send(string text)
    {
        if(_disposed || Locked || IsReplay || !IsRunning) throw new InvalidOperationException("This session is not accepting input.");
        if(text.Length > Safety.MaximumPasteLength) throw new InvalidOperationException("Input exceeds the 1 MiB safety limit.");
        Terminal.SendInput(text);
    }
    public string OutputSnapshot() { lock(_outputLock) return _output.ToString(); }
    private void Receive(object? sender, TerminalDataEventArgs e)
    {
        _log?.Receive(e.DataSpan);
        lock(_outputLock)
        {
            var chars = new char[Encoding.UTF8.GetMaxCharCount(e.Data.Length)];
            var count = _decoder.GetChars(e.DataSpan, chars, false);
            _output.Append(chars, 0, count);
            if(_output.Length > 1024 * 1024) _output.Remove(0, _output.Length - 1024 * 1024);
        }
    }
    private void WriteFixture()
    {
        var text = DocumentTitle == "dev server"
            ? "\u001b[90mDesign fixture · local watcher\u001b[0m\r\n\r\n  → Local:  \u001b[34mhttp://localhost:5173/\u001b[0m\r\n  → Mode:   visual acceptance fixture\r\n\r\n  \u001b[32m✓\u001b[0m app mounted\r\n  \u001b[32m✓\u001b[0m workspace shell ready\r\n  \u001b[32m✓\u001b[0m terminal surfaces attached\r\n\r\n\u001b[34m~/Developer/tessera\u001b[32m ❯ \u001b[0m"
            : Profile.Id == "staging"
            ? "\u001b[90mDesign fixture · no remote network connection\u001b[0m\r\n\r\nUbuntu 24.04 LTS   •   x86_64\r\nLoad  0.08        Memory  1.2 / 8 GB\r\n\r\n$ systemctl status tessera-api\r\n\u001b[32m● tessera-api.service — API service\r\n   Active: active (running)\u001b[0m\r\n\r\n\u001b[34m/srv/api\u001b[32m ❯ \u001b[0m"
            : "\u001b[32mtessera\u001b[90m  /  your command line, composed.\u001b[0m\r\nA considered workspace for the command line.\r\n\r\n\u001b[32m❯\u001b[0m git status\r\nOn branch \u001b[34mfeature/workspace-shell\u001b[0m\r\nYour branch is up to date with origin.\r\n\r\nChanges ready for review:\r\n  \u001b[32mmodified:\u001b[0m   src/Tessera.App/Views/MainWindow.axaml\r\n  \u001b[32mmodified:\u001b[0m   src/Tessera.Core/Workspace.cs\r\n  \u001b[32mnew file:\u001b[0m   tests/Tessera.Tests/LayoutTests.cs\r\n\r\n\u001b[32m❯\u001b[0m dotnet test\r\n\r\n  Layout persistence\r\n  Session lifecycle\r\n  Keyboard routing\r\n\r\n\u001b[90mVisual acceptance fixture, not a test result.\u001b[0m\r\n\r\n\u001b[34m~/Developer/tessera\u001b[32m ❯ \u001b[0m";
        Terminal.WriteOutput(Encoding.UTF8.GetBytes(text));
    }
    public void Dispose()
    {
        var task = DisposeAsync().AsTask();
        if (!task.IsCompletedSuccessfully)
            _ = task.ContinueWith(t => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                Diagnostic?.Invoke("Session cleanup failed: " + t.Exception!.GetBaseException().Message)),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }
    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        _disposed = true; _lifetime.Cancel(); BroadcastTarget = false;
        Terminal.SecurityContext.Cancel();
        // Join startup before freeing key material or stopping its newly-created transport.
        await _lifecycle.WaitAsync();
        try
        {
            Terminal.FlushPendingTransportOutput();
            Terminal.StopPty();
            Terminal.DataReceived -= Receive;
            Capture.Dispose();
            Terminal.SecurityContext.Dispose();
            await FlushLogAsync();
            State = "Disposed";
        }
        finally { _lifecycle.Release(); _lifetime.Dispose(); }
    }
}

public sealed class SessionRegistry(ProfileRepository profiles, bool design) : IDisposable
{
    private readonly Dictionary<Guid, SessionRuntime> _sessions = [];
    private readonly List<Task> _closing = [];
    public IReadOnlyCollection<SessionRuntime> All => _sessions.Values;
    public SessionRuntime Get(TerminalDocument document)
    {
        if(_sessions.TryGetValue(document.Id, out var existing)) { if(document.IsReplay && !existing.IsReplay) existing.MarkReplaySlot(); return existing; }
        var session = new SessionRuntime(document.Id, profiles.Get(document.ProfileId), profiles, design) { DocumentTitle = document.Title };
        if(document.IsReplay) session.MarkReplaySlot();
        _sessions.Add(document.Id, session); return session;
    }
    public SessionRuntime? Find(Guid id) => _sessions.GetValueOrDefault(id);
    public void Close(Guid id)
    {
        if (!_sessions.Remove(id, out var session)) return;
        _closing.RemoveAll(t => t.IsCompletedSuccessfully);
        _closing.Add(session.DisposeAsync().AsTask());
    }
    public async Task DrainAsync()
    {
        foreach (var id in _sessions.Keys.ToArray()) Close(id);
        await Task.WhenAll(_closing); _closing.Clear();
    }
    public void Dispose()
    {
        foreach(var session in _sessions.Values) session.Dispose(); _sessions.Clear();
        foreach (var task in _closing) _ = task.ContinueWith(t => System.Diagnostics.Trace.TraceError(t.Exception!.ToString()),
            CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted,TaskScheduler.Default);
    }
}
