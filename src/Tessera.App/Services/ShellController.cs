using System.Text;
using Avalonia.Threading;
using RoyalTerminal.Avalonia.Services;
using RoyalTerminal.Terminal;
using Tessera.Core;

namespace Tessera.Services;

/// <summary>Application state and session ownership. No visual-tree object owns process lifetime.</summary>
public sealed class ShellController : IDisposable
{
    private readonly WorkspaceStore _store;
    private readonly WorkspaceHistory _history = new();
    private readonly HashSet<Guid> _wiredSessions = [];
    private readonly DispatcherTimer _saveTimer;
    private bool _broadcasting;
    private bool _disposed;
    private bool _storageHealthy = true;
    public bool DesignMode { get; }
    public bool Initialized { get; private set; }
    public string DirectoryPath { get; }
    public ProfileRepository Profiles { get; }
    public SessionRegistry Sessions { get; }
    public CommandHistoryStore HistoryStore { get; }
    public ShaderStore Shaders { get; }
    public CaptureRecoveryService Recovery { get; }
    public AppDocument Data { get; private set; }
    public Workspace Active => Data.Workspaces.Single(w => w.Id == Data.ActiveWorkspace);
    public Guid? ActiveDocumentId { get; private set; }
    public Guid ActiveGroupId => Layout.Groups(Active).FirstOrDefault(g => g.Tabs.Contains(ActiveDocumentId ?? Guid.Empty))?.Id ?? Layout.Groups(Active.Root).First().Id;
    public TerminalDocument? ActiveDocument => ActiveDocumentId is {} id ? Active.Documents.GetValueOrDefault(id) : null;
    public SessionRuntime? ActiveSession
    {
        get
        {
            if(ActiveDocument is not {} document) return null;
            try { return GetSession(document); }
            catch(InvalidOperationException) { return null; }
        }
    }
    public IReadOnlyList<TerminalCommandHistoryEntry> CommandHistory => HistoryStore.Entries.Select(e => new TerminalCommandHistoryEntry
    { Id=e.Id,CommandLine=e.Command,StartedAtUtc=e.Started,CompletedAtUtc=e.Completed,ExitCode=e.ExitCode,ProfileId=e.Profile,Host=e.Host,WorkingDirectory=e.Directory }).ToArray();
    public List<string> Events { get; } = [];
    public string Status { get; private set; } = "Ready";
    public bool FocusMode { get; private set; }
    public bool SidebarVisible { get; private set; } = true;
    public bool ToolsVisible { get; private set; } = true;
    public bool BroadcastEnabled { get; private set; }
    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;
    public event Action<bool>? Changed;
    public event Action<SessionRuntime>? SessionCreated;
    public ShellController(bool designMode, string? directory = null)
    {
        DesignMode = designMode;
        DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tessera");
        Profiles = new ProfileRepository(DirectoryPath); Sessions = new SessionRegistry(Profiles, designMode);
        HistoryStore = new(Path.Combine(DirectoryPath,"command-history.json"));
        Shaders = new(Path.Combine(DirectoryPath,"shaders.json"));
        Recovery = new(DirectoryPath,Profiles.Vault);
        Recovery.Error += Report;
        _store = new WorkspaceStore(Path.Combine(DirectoryPath, "workspace.json"));
        var workspace = Workspace.Empty("Development") with { Description = "Build, test, and ship in one place." };
        Data = new AppDocument(1, workspace.Id, [workspace], [],
        [new(Guid.NewGuid(), "Repository status", "git status --short", "GIT"), new(Guid.NewGuid(), "Run the test suite", "dotnet test --no-restore", "BUILD"), new(Guid.NewGuid(), "Container overview", "docker ps --format 'table {{.Names}}\t{{.Status}}'", "CONTAINERS")], new(), []);
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); try { await FlushAsync(); } catch(Exception ex) { Report(ex.Message); } };
    }
    public async Task InitializeAsync()
    {
        if(Initialized) return;
        if(DesignMode) Profiles.AddDesignProfiles();
        else
        {
            try { await Profiles.LoadAsync(); Data = await _store.LoadAsync() ?? Data; }
            catch(Exception ex) { _storageHealthy = false; Report("Settings recovery required: " + ex.Message + ". Existing files will not be overwritten."); }
        }
        HistoryStore.Persist = Data.Preferences.PersistHistory; HistoryStore.RetentionDays = Data.Preferences.HistoryRetentionDays;
        if(!DesignMode)
        {
            try { if(HistoryStore.Persist) await HistoryStore.LoadAsync(); await Shaders.LoadAsync(); }
            catch(Exception ex) { Report("A supplemental settings file needs recovery: " + ex.Message); }
        }
        ThemeManager.Apply(Data.Preferences.Theme);
        if(Active.Documents.Count == 0)
        {
            var local = Profiles.Document.Profiles.First();
            var workspace = Layout.Add(Active, Active.Root.Id, new(Guid.NewGuid(), local.Id, DesignMode ? "api-service" : local.DisplayName));
            if(DesignMode)
            {
                foreach(var pair in new[] { ("local", "design-system"), ("local", "dev server"), ("staging", "edge-staging") }) workspace = Layout.Add(workspace, workspace.Root.Id, new(Guid.NewGuid(), pair.Item1, pair.Item2));
                // The designer's composition: two local tabs, a watcher, and a staging pane.
                var ids = workspace.Documents.Keys.ToArray();
                workspace = Layout.Move(workspace, ids[2], workspace.Root.Id, DockEdge.Right);
                var right = Layout.Groups(workspace.Root).Last(); workspace = Layout.Move(workspace, ids[3], right.Id, DockEdge.Bottom);
                workspace = Layout.Resize(workspace, workspace.Root.Id, .62);
                workspace = Layout.Activate(workspace, ids[0]);
                Data = Data with { Workspaces = [workspace, Workspace.Empty("Infrastructure") with { Description = "Operate with clarity and confidence." }, Workspace.Empty("Scratchpad") with { Description = "A little room to explore." }], Layouts = [new(Guid.NewGuid(), "Build & monitor", workspace.Root, workspace.Documents), new(Guid.NewGuid(), "Four-way inspection", Layout.Arrange(workspace, "Four-way inspection").Root, workspace.Documents)] };
            }
            else ReplaceWorkspace(workspace);
        }
        ActiveDocumentId = Layout.Groups(Active).FirstOrDefault(g => g.Active is not null)?.Active;
        Initialized = true; Changed?.Invoke(true);
        // Size controls before first output, rather than shrinking a populated 120x36 fixture.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        // Restoration rebuilds layout. Remote processes are never silently reconnected.
        if(DesignMode || Data.Preferences.RestoreLocalSessions)
            foreach(var document in Active.Documents.Values)
            {
                try
                {
                    var session = GetSession(document);
                    if(!document.IsReplay && (DesignMode || session.Profile.Transport.TransportId == TerminalTransportIds.Pty)) await session.EnsureStartedAsync();
                }
                catch(InvalidOperationException ex) { Report(ex.Message); }
            }
        Notify(false);
    }
    public SessionRuntime GetSession(TerminalDocument document)
    {
        var runtime = Sessions.Get(document);
        if(!_wiredSessions.Add(document.Id)) return runtime;
        runtime.Changed += () => Dispatcher.UIThread.Post(() => { if(!_disposed) Changed?.Invoke(false); });
        runtime.Bell += name => Dispatcher.UIThread.Post(() => Report("Bell · " + name));
        runtime.Diagnostic += text => Dispatcher.UIThread.Post(() => { if (!_disposed) Report(text); });
        ApplyTerminalPreferences(runtime);
        ApplySavedShaders(runtime);
        runtime.Terminal.PasteSafetyPolicy = TerminalPasteSafetyPolicy.BlockUnsafe;
        var history = new TerminalCommandHistoryCaptureService(new TerminalCommandHistoryCaptureContext(ProfileId: document.ProfileId, TransportId: runtime.Profile.Transport.TransportId));
        runtime.Terminal.ShellIntegrationEventReceived += (_, e) =>
        {
            var completed = history.Process(e.Value);
            Dispatcher.UIThread.Post(() =>
            {
                if(_disposed) return;
                if(e.Value.Kind == TerminalShellIntegrationEventKind.WorkingDirectoryChanged) runtime.WorkingDirectory = e.Value.WorkingDirectory;
                if(completed is not null && completed.CompletedAtUtc is {} finished)
                {
                    HistoryStore.Add(new(completed.Id,completed.CommandLine,completed.StartedAtUtc,finished,completed.ExitCode,document.ProfileId,completed.Host ?? (runtime.Profile.Transport.TransportId == TerminalTransportIds.Ssh ? runtime.Profile.Transport.Ssh.Host : null),completed.WorkingDirectory));
                    Notify(false);
                }
            });
        };
        runtime.Terminal.TerminalSessionService.InputSent += (_, e) =>
        {
            if(!BroadcastEnabled || _broadcasting || ActiveDocumentId != runtime.Id || !Eligible(runtime)) return;
            var bytes = e.Data.ToArray();
            void Relay()
            {
                if(_disposed || !BroadcastEnabled || _broadcasting || ActiveDocumentId != runtime.Id || !Eligible(runtime)) return;
                _broadcasting = true;
                try { foreach(var target in Sessions.All.Where(s => s.Id != runtime.Id && Active.Documents.ContainsKey(s.Id) && Eligible(s)).ToArray()) target.Terminal.SendInput(bytes.AsSpan()); }
                finally { _broadcasting = false; }
            }
            if(Dispatcher.UIThread.CheckAccess()) Relay(); else Dispatcher.UIThread.Post(Relay);
        };
        SessionCreated?.Invoke(runtime); return runtime;
    }
    private static bool Eligible(SessionRuntime session) => Safety.CanBroadcast(session.IsProduction, session.Locked, session.IsRunning, session.IsReplay, session.BroadcastTarget);
    public void Report(string text)
    {
        Status = text; Events.Insert(0, DateTimeOffset.Now.ToString("HH:mm:ss") + "  " + text);
        if(Events.Count > 1000) Events.RemoveAt(Events.Count - 1); Changed?.Invoke(false);
    }
    public void Apply(Workspace workspace, bool undoable = true, bool render = true)
    {
        Layout.Validate(workspace); if(undoable) _history.Push(Active);
        ReplaceWorkspace(workspace);
        if(ActiveDocumentId is not {} active || !workspace.Documents.ContainsKey(active)) ActiveDocumentId = Layout.Groups(workspace).FirstOrDefault(g => g.Active is not null)?.Active;
        Notify(render);
    }
    private void ReplaceWorkspace(Workspace workspace) => Data = Data with { Workspaces = Data.Workspaces.Select(w => w.Id == workspace.Id ? workspace : w).ToArray() };
    private void Notify(bool render)
    {
        Changed?.Invoke(render);
        if(!DesignMode && Initialized && _storageHealthy) { _saveTimer.Stop(); _saveTimer.Start(); }
    }
    public void Select(Guid documentId)
    {
        if(!Active.Documents.ContainsKey(documentId)) return;
        ActiveDocumentId = documentId; Apply(Layout.Activate(Active, documentId), false);
    }
    public async Task<TerminalDocument> NewTerminalAsync(string? profileId = null, bool start = true, Guid? target = null)
    {
        var profile = Profiles.Get(profileId ?? Profiles.Document.DefaultProfileId ?? Profiles.Document.Profiles[0].Id);
        var document = new TerminalDocument(Guid.NewGuid(), profile.Id, profile.DisplayName);
        Apply(Layout.Add(Active, target ?? ActiveGroupId, document), false); ActiveDocumentId = document.Id; _history.Clear(); Notify(true);
        var session = GetSession(document); if(start) await session.EnsureStartedAsync(); return document;
    }
    public async Task SplitAsync(DockEdge edge)
    {
        var target = ActiveGroupId; var profile = ActiveDocument?.ProfileId; var document = await NewTerminalAsync(profile, false, target);
        Apply(Layout.Move(Active, document.Id, target, edge));
        await GetSession(document).EnsureStartedAsync();
    }
    public void Move(Guid documentId, Guid target, DockEdge edge, int index = int.MaxValue) { Apply(Layout.Move(Active, documentId, target, edge, index)); ActiveDocumentId = documentId; Notify(true); }
    public void Resize(Guid splitId, double ratio) => Apply(Layout.Resize(Active, splitId, ratio), false, false);
    public void CloseDocument(Guid documentId)
    {
        var document = Active.Documents.GetValueOrDefault(documentId); if(document is null) return;
        Apply(Layout.Close(Active, documentId), false); Sessions.Close(documentId); _wiredSessions.Remove(documentId); _history.Clear(); Notify(true);
    }
    public void MarkReplay(Guid id) => Apply(Layout.Update(Active, Active.Documents[id] with { IsReplay = true }), false);
    public void RenameDocument(Guid id, string title) => Apply(Layout.Update(Active, Active.Documents[id] with { Title = Safety.CleanTitle(title) }));
    public void TogglePin(Guid id) => Apply(Layout.Update(Active, Active.Documents[id] with { Pinned = !Active.Documents[id].Pinned }));
    public void SwitchWorkspace(Guid workspaceId)
    {
        if(!Data.Workspaces.Any(w => w.Id == workspaceId)) return;
        BroadcastEnabled = false; foreach(var session in Sessions.All) session.BroadcastTarget = false;
        Data = Data with { ActiveWorkspace = workspaceId }; _history.Clear(); FocusMode = false; ActiveDocumentId = Layout.Groups(Active).FirstOrDefault(g => g.Active is not null)?.Active; Notify(true);
    }
    public void AddWorkspace(string name)
    {
        if(Data.Workspaces.Length >= 64) throw new InvalidOperationException("The workspace limit is 64.");
        var workspace = Workspace.Empty(name.Trim()); Layout.Validate(workspace);
        Data = Data with { Workspaces = [.. Data.Workspaces, workspace] }; SwitchWorkspace(workspace.Id);
    }
    public void RenameWorkspace(string name) => Apply(Active with { Name = name.Trim() });
    public void RemoveWorkspace()
    {
        if(Data.Workspaces.Length == 1) throw new InvalidOperationException("Keep at least one workspace.");
        var current = Active; foreach(var id in current.Documents.Keys) Sessions.Close(id);
        var remaining = Data.Workspaces.Where(w => w.Id != current.Id).ToArray(); Data = Data with { Workspaces = remaining, ActiveWorkspace = remaining[0].Id }; SwitchWorkspace(remaining[0].Id);
    }
    public void Arrange(string preset) { FocusMode = false; Apply(Layout.Arrange(Active, preset)); }
    public void SaveLayout(string name) { Data = Data with { Layouts = [.. Data.Layouts, new(Guid.NewGuid(), name.Trim(), Active.Root, new(Active.Documents)) { Floating = Active.Floating }] }; Notify(true); }
    public void RemoveLayout(Guid id) { Data = Data with { Layouts = Data.Layouts.Where(l => l.Id != id).ToArray() }; Notify(true); }
    public void RestoreLayout(SavedLayout layout)
    {
        var old = Active.Documents.Keys.ToArray(); Apply(Layout.RestoreLayout(Active, layout), false);
        foreach(var id in old) Sessions.Close(id); _history.Clear(); Report("Layout restored. Connect the new session slots explicitly.");
    }
    public void Undo() { if(CanUndo) Apply(_history.Undo(Active), false); }
    public void Redo() { if(CanRedo) Apply(_history.Redo(Active), false); }
    public void ToggleFocus() { FocusMode = !FocusMode; Notify(true); }
    public void ToggleSidebar() { SidebarVisible = !SidebarVisible; Notify(true); }
    public void ToggleTools() { ToolsVisible = !ToolsVisible; Notify(true); }
    public void SetPreferences(AppPreferences preferences)
    {
        Data = Data with { Preferences = preferences }; WorkspaceStore.Validate(Data);
        ThemeManager.Apply(preferences.Theme);
        HistoryStore.Persist = preferences.PersistHistory; HistoryStore.RetentionDays = preferences.HistoryRetentionDays;
        foreach(var session in Sessions.All) ApplyTerminalPreferences(session);
        Notify(true);
    }
    public void SetTheme(string theme) => SetPreferences(Data.Preferences with { Theme = theme });
    public void SetTool(string tool) { ToolsVisible = true; Data = Data with { Preferences = Data.Preferences with { Tool = tool } }; Notify(true); }
    public void SetNotes(string notes) { ReplaceWorkspace(Active with { Notes = notes }); Notify(false); }
    public void AddSnippet(Snippet snippet) { if(!Safety.IsSafeCommandInsertion(snippet.Command)) throw new InvalidOperationException("Snippets must be a single command line without control sequences."); Data = Data with { Snippets = [.. Data.Snippets, snippet] }; Notify(true); }
    public void RemoveSnippet(Guid id) { Data = Data with { Snippets = Data.Snippets.Where(s => s.Id != id).ToArray() }; Notify(true); }
    public void Bind(string command, string gesture) { var bindings = new Dictionary<string,string>(Data.Bindings) { [command] = gesture }; Data = Data with { Bindings = bindings }; Notify(true); }
    public void SetBroadcast(bool enabled)
    {
        if(enabled && (ActiveSession is not {} source || !Eligible(source) || Sessions.All.Count(s => Active.Documents.ContainsKey(s.Id) && Eligible(s)) < 2)) throw new InvalidOperationException("Select the active session and at least one other connected, unlocked, non-production target.");
        BroadcastEnabled = enabled; Report(enabled ? "BROADCAST ARMED · input is sent to selected sessions" : "Broadcast off"); Changed?.Invoke(true);
    }
    public void ApplyTerminalPreferences(SessionRuntime session)
    {
        var p = Data.Preferences;
        ThemeManager.ApplyTerminal(session.Terminal); session.Terminal.BackgroundOpacityEnabled = session.Profile.Appearance.BackgroundOpacityEnabled;
        session.Terminal.ShaderAnimationEnabled = !p.ReducedMotion;
        session.Terminal.TerminalFontSize = p.OverrideProfileFont ? p.FontSize * .75 : session.Profile.Appearance.FontSize;
        session.Terminal.FontSource = p.OverrideProfileFont ? TerminalFontSource.System : session.Profile.Appearance.FontSource;
        session.Terminal.FontFamilyName = p.OverrideProfileFont && !string.IsNullOrWhiteSpace(p.FontFamily) ? p.FontFamily : session.Profile.Appearance.FontFamilyName;
        session.Terminal.LineHeight = p.LineHeight; session.CursorShape = p.CursorStyle; session.CursorBlink = p.CursorBlink;
        session.Terminal.ApplyCursor(p.CursorStyle,p.CursorBlink);
    }
    private void ApplySavedShaders(SessionRuntime session)
    {
        var saved = Shaders.Get(session.Id);
        if(saved.Length == 0) return;
        try
        {
            var sources = saved.Select(d => new RoyalTerminal.Shaders.TerminalShaderSource(d.Name,d.Source,Enum.Parse<RoyalTerminal.Shaders.TerminalShaderLanguage>(d.Language),d.Animated)).ToArray();
            using var compiler = RoyalTerminal.Avalonia.Rendering.TerminalShaderPostProcessor.Create(sources);
            if(!string.IsNullOrWhiteSpace(compiler.CompileLog)) throw new InvalidDataException(compiler.CompileLog);
            session.Terminal.ShaderSources = sources;
        }
        catch(Exception ex) { Report("Saved shader disabled: " + ex.Message); }
    }
    public async Task FlushAsync()
    {
        if(DesignMode || !Initialized || !_storageHealthy) return;
        await _store.SaveAsync(Data); await HistoryStore.SaveAsync();
        Shaders.Retain(Data.Workspaces.SelectMany(w => w.Documents.Keys).ToHashSet()); await Shaders.SaveAsync();
    }
    public async Task PrepareShutdownAsync()
    {
        await Recovery.PrepareCloseAsync();
        await Sessions.DrainAsync();
        await FlushAsync();
    }
    public async Task RetainWorkspaceCapturesAsync()
    {
        foreach(var id in Active.Documents.Keys)
            if(Sessions.Find(id) is {} session) { await Recovery.RetainOnCloseAsync(session); }
    }
    public void Dispose() { if(_disposed) return; _disposed = true; _saveTimer.Stop(); BroadcastEnabled = false; Recovery.Dispose(); Sessions.Dispose(); }
}
