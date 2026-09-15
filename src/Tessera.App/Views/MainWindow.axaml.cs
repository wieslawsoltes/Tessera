using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using RoyalTerminal.Avalonia.Services;
using Tessera.Core;
using Tessera.Services;

namespace Tessera.Views;

public sealed partial class MainWindow : Window
{
    private readonly DockHost _dock;
    private readonly IWorkspaceFileDialogs _fileDialogs;
    private readonly CommandRegistry _commands = new();
    private readonly WindowMenuBinding _menuBinding;
    private readonly Dictionary<Guid, WindowMenuBinding> _floatingMenus = [];
    private bool _renderQueued;
    private bool _closing;
    private bool _closeRequested;
    private bool _closed;
    private bool _initializing;
    private Task? _initialization;
    private readonly Dictionary<Guid, Window> _floating = [];
    private readonly Dictionary<Guid, DockHost> _floatingHosts = [];
    public ShellController Shell { get; }
    public CommandRegistry Commands => _commands;
    public bool IsOverlayOpen => Q<Border>("OverlayShade").IsVisible;
    private T Q<T>(string name) where T : Control => this.FindControl<T>(name)!;
    public MainWindow() : this(false) { }
    public MainWindow(bool designMode, string? directory = null, IWorkspaceFileDialogs? fileDialogs = null)
    {
        _fileDialogs = fileDialogs ?? new NativeWorkspaceFileDialogs();
        AvaloniaXamlLoader.Load(this);
        // The main window already coordinates confirmation, SFTP edits, captures
        // and session disposal. Child floating windows otherwise veto owner-close
        // to implement their standalone "return tabs" action before this handler runs.
        ClosingBehavior = WindowClosingBehavior.OwnerWindowOnly;
        Shell = new ShellController(designMode, directory);
        _dock = new DockHost(this, Shell); Q<ContentControl>("DockSurface").Content = _dock;
        ConfigureCommands();
        RegisterAdvancedCommands();
        RegisterAcceptanceCommands();
        _menuBinding = new WindowMenuBinding(this, _commands, Q<Menu>("MainMenu"));
        Shell.SessionCreated += ConfigureSession;
        Shell.Profiles.Saved += () =>
        {
            foreach(var session in Shell.Sessions.All)
            {
                session.ApplyProductionPolicy(Shell.Profiles.Production.Contains(session.Profile.Id));
                var profile = Shell.Profiles.Document.Profiles.FirstOrDefault(p => p.Id == session.Profile.Id);
                if(profile is not null) { session.ApplyProfile(profile); Shell.ApplyTerminalPreferences(session); }
            }
            Shell.SetBroadcast(false);
        };
        Shell.Changed += render => { if(_closed) return; if(render) QueueRender(); else RefreshStatus(); };
        ConfigureSecurityWorkflows();
        Q<Button>("PaletteButton").Click += (_, _) => ShowPalette();
        Q<Button>("ThemeButton").Click += (_, _) => CycleTheme();
        Q<Button>("ExploreButton").Click += (_, _) => ShowAbout();
        var titleBand = Q<Grid>("TitleBand");
        if(OperatingSystem.IsMacOS()) titleBand.Margin = new Thickness(84,0,20,0);
        else titleBand.Margin = new Thickness(16,0,144,0);
        titleBand.PointerPressed += (_, e) => { if(e.Source is Grid && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { if(e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; else BeginMoveDrag(e); } };
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => Run(InitializeAsync);
        Closing += (_, e) =>
        {
            if(_closing) return;
            e.Cancel = true;
            if (_closeRequested) return;
            _closeRequested = true;
            Run(async () =>
            {
                try
                {
                    if(!Shell.DesignMode && Shell.Data.Preferences.ConfirmClose && Shell.Sessions.All.Any(s => s.IsRunning) && !await ConfirmAsync("Close Tessera?", "Running terminal processes will be stopped. Layouts and notes will be saved.", "Close application")) return;
                    if(!await TryCloseSftpAsync())return;
                    await Shell.PrepareShutdownAsync(); _closing = true; Close();
                }
                finally { _closeRequested = false; }
            });
        };
        Closed += (_, _) => { _closed = true; DismissOverlay(); _timelineTimer?.Stop(); _menuBinding.Dispose(); _windowLifetime.Cancel(); _dock.DetachAll(); foreach(var floating in _floating.Values.ToArray()) floating.Close(); Shell.Dispose(); };
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        Q<GridSplitter>("ToolsSplitter").AddHandler(PointerReleasedEvent, (_, _) =>
        {
            var right = Shell.Data.Preferences.ToolsOnRight; var grid = Q<Grid>("ContentGrid");
            double size = right ? grid.ColumnDefinitions[2].ActualWidth : grid.RowDefinitions[2].ActualHeight;
            if(size is > 80 and < 900) Shell.SetPreferences(Shell.Data.Preferences with { ToolSize = size });
        }, RoutingStrategies.Bubble, true);
    }
    public Task InitializeAsync() => _initialization ??= InitializeCoreAsync();
    private async Task InitializeCoreAsync()
    {
        _initializing = true;
        try { await Shell.InitializeAsync(); _commands.ApplyBindings(Shell.Data.Bindings); RenderShell(); if(!Shell.DesignMode && Shell.Recovery.Store.List().Length is > 0 and var recovered) Shell.Report($"{recovered} unsaved encrypted recordings · Tools → Recover unsaved recordings"); }
        finally { _initializing = false; RefreshStatus(); }
    }
    public async void Run(Func<Task> action)
    {
        try { await action(); }
        catch(OperationCanceledException) { Shell.Report("Cancelled"); }
        catch(Exception ex) { Shell.Report(ex.Message); ShowError(ex.Message); }
    }
    private void QueueRender()
    {
        if(_renderQueued || _closed) return; _renderQueued = true;
        Dispatcher.UIThread.Post(() => { _renderQueued = false; if(!_closed) RenderShell(); }, DispatcherPriority.Background);
    }
    private void ConfigureSession(SessionRuntime session)
    {
        session.Terminal.CloseRequested += (_, _) => Dispatcher.UIThread.Post(() => Run(() => CloseTabAsync(session.Id)));
        Shell.ApplyTerminalPreferences(session);
        session.Terminal.PasteSafetyPolicy = Enum.TryParse<TerminalPasteSafetyPolicy>(session.Profile.Behavior.PasteSafetyPolicy, true, out var paste) && paste != TerminalPasteSafetyPolicy.None ? paste : TerminalPasteSafetyPolicy.ConfirmUnsafe;
        session.Terminal.ShaderAnimationEnabled = !Shell.Data.Preferences.ReducedMotion;
        session.Terminal.UnsafePasteHandler = async context =>
        {
            if(session.Locked || session.IsReplay) return TerminalPasteSafetyDecision.Cancel;
            return await ConfirmAsync("Review this paste", "The clipboard contains multiple lines or terminal control sequences. Pasting may immediately execute commands. Paste only text you trust.", "Paste into active session") ? TerminalPasteSafetyDecision.Allow : TerminalPasteSafetyDecision.Cancel;
        };
        session.Terminal.AddHandler(KeyDownEvent, (_, e) =>
        {
            if(session.Locked && e.Key == Key.C && (e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))) { e.Handled = true; Run(session.Terminal.CopySelectionAsync); }
        }, RoutingStrategies.Tunnel);
    }
    private void ConfigureCommands()
    {
        var primary = OperatingSystem.IsMacOS() ? "Meta" : "Ctrl";
        void Add(string id, string title, string category, string icon, string gesture, Func<Task> action, Func<bool>? enabled = null) => _commands.Add(new(id,title,category,icon,gesture,action, ex => { Shell.Report(ex.Message); ShowError(ex.Message); },enabled));
        Task Do(Action action) { action(); return Task.CompletedTask; }
        bool HasSession() => Shell.Initialized && Shell.ActiveDocument is not null;
        Add("new", "New terminal", "File", "add", primary+"+Shift+T", async()=>{await Shell.NewTerminalAsync();});
        Add("new-workspace", "New workspace", "File", "layout", "", async()=> { var name=await PromptAsync("New workspace", "Give this group of sessions a purpose.", "Untitled workspace"); if(!string.IsNullOrWhiteSpace(name)) Shell.AddWorkspace(name); });
        Add("rename-workspace", "Rename workspace", "File", "note", "", async()=> {var name=await PromptAsync("Rename workspace", "Workspace name",Shell.Active.Name);if(!string.IsNullOrWhiteSpace(name))Shell.RenameWorkspace(name);});
        Add("remove-workspace", "Remove workspace", "File", "close", "", async()=>{if(await ConfirmAsync("Remove workspace?", "All sessions in this workspace will be stopped.","Remove workspace")){await Shell.RetainWorkspaceCapturesAsync();Shell.RemoveWorkspace();}});
        Add("profiles", "Connections & profiles", "File", "server", "", ()=>Do(()=>ShowProfiles()));
        Add("close", "Close active terminal", "File", "close", primary+"+Shift+W", ()=>Shell.ActiveDocumentId is {} id ? CloseTabAsync(id) : Task.CompletedTask,HasSession);
        Add("save", "Save workspace", "File", "save", primary+"+Shift+S", Shell.FlushAsync);
        Add("export", "Export terminal text", "File", "file", "", ExportOutputAsync,HasSession);
        Add("quit", "Quit Tessera", "File", "close", "", ()=>Do(Close));
        Add("undo", "Undo layout change", "Edit", "history", primary+"+Shift+Z", ()=>Do(Shell.Undo),()=>Shell.CanUndo);
        Add("redo", "Redo layout change", "Edit", "history", primary+"+Shift+Y", ()=>Do(Shell.Redo),()=>Shell.CanRedo);
        Add("copy", "Copy selection", "Edit", "file", OperatingSystem.IsMacOS()?"Meta+C":"Ctrl+Shift+C", ()=>Shell.ActiveSession?.Terminal.CopySelectionAsync()??Task.CompletedTask,HasSession);
        Add("paste", "Paste safely", "Edit", "file", OperatingSystem.IsMacOS()?"Meta+V":"Ctrl+Shift+V", async()=>{var session=Shell.ActiveSession;if(session is null||session.Locked||session.IsReplay)throw new InvalidOperationException("This terminal is read-only.");await session.Terminal.PasteAsync();},HasSession);
        Add("select-all", "Select terminal output", "Edit", "file", primary+"+Shift+A",()=>Do(()=>Shell.ActiveSession?.Terminal.SelectAll()),HasSession);
        Add("find", "Find in terminal", "Edit", "search", primary+"+Shift+F",()=>Do(ShowSearch),HasSession);
        Add("palette", "Command palette", "View", "search", primary+"+K",()=>Do(()=>ShowPalette()));
        Add("settings", "Preferences", "View", "settings", primary+"+OemComma",()=>Do(ShowSettings));
        Add("layouts", "Choose a layout", "View", "layout", primary+"+Shift+L",()=>Do(ShowLayouts));
        Add("save-layout", "Save current layout", "View", "save", "", async()=>{var name=await PromptAsync("Save this arrangement", "A saved layout stores connection slots, not running processes.","My layout");if(!string.IsNullOrWhiteSpace(name))Shell.SaveLayout(name);});
        Add("focus", "Toggle focus mode", "View", "focus", primary+"+Shift+Enter",()=>Do(Shell.ToggleFocus));
        Add("sidebar", "Toggle sidebar", "View", "layout", primary+"+Shift+B",()=>Do(Shell.ToggleSidebar));
        Add("tools", "Toggle tool panel", "View", "rows", primary+"+Shift+J",()=>Do(Shell.ToggleTools));
        Add("theme", "Cycle color theme", "View", "sun", "",()=>Do(CycleTheme));
        Add("zoom-in", "Increase terminal font", "View", "add", primary+"+OemPlus",()=>Do(()=>ChangeFont(1)));
        Add("zoom-out", "Decrease terminal font", "View", "close", primary+"+OemMinus",()=>Do(()=>ChangeFont(-1)));
        Add("split-right", "Split right", "Session", "split", primary+"+Shift+D",()=>Shell.SplitAsync(DockEdge.Right),HasSession);
        Add("split-down", "Split down", "Session", "rows", primary+"+Shift+E",()=>Shell.SplitAsync(DockEdge.Bottom),HasSession);
        Add("next-pane", "Focus next pane", "Session", "right", "Alt+PageDown",()=>Do(()=>_dock.FocusNext(1)));
        Add("previous-pane", "Focus previous pane", "Session", "right", "Alt+PageUp",()=>Do(()=>_dock.FocusNext(-1)));
        Add("grow-pane", "Grow pane horizontally", "Session", "split", "Alt+Shift+Right",()=>Do(()=>_dock.ResizeActive(SplitAxis.Columns,.05)));
        Add("shrink-pane", "Shrink pane horizontally", "Session", "split", "Alt+Shift+Left",()=>Do(()=>_dock.ResizeActive(SplitAxis.Columns,-.05)));
        Add("next-tab", "Next terminal tab", "Session", "terminal", "Ctrl+Tab",()=>Do(()=>SelectTab(1)),HasSession);
        Add("previous-tab", "Previous terminal tab", "Session", "terminal", "Ctrl+Shift+Tab",()=>Do(()=>SelectTab(-1)),HasSession);
        Add("duplicate", "Duplicate connection", "Session", "add", "",async()=>{if(Shell.ActiveDocument is {} d)await Shell.NewTerminalAsync(d.ProfileId);},HasSession);
        Add("rename", "Rename terminal", "Session", "note", "",()=>Shell.ActiveDocumentId is {} id?RenameTabAsync(id):Task.CompletedTask,HasSession);
        Add("pin", "Pin / unpin terminal", "Session", "save", "",()=>Do(()=>{if(Shell.ActiveDocumentId is {} id)Shell.TogglePin(id);}),HasSession);
        Add("float", "Float terminal in a window", "Session", "focus", "",()=>Do(()=>{if(Shell.ActiveDocumentId is {} id)FloatTerminal(id);}),HasSession);
        Add("reconnect", "Connect / reconnect", "Session", "right", "",()=>Shell.ActiveSession?.ReconnectAsync()??Task.CompletedTask,HasSession);
        Add("unlock", "Lock / unlock input", "Session", "lock", "",()=>Shell.ActiveSession is {} s?UnlockAsync(s):Task.CompletedTask,HasSession);
        Add("broadcast", "Configure broadcast", "Session", "broadcast", "",()=>Do(ShowBroadcast));
        Add("clear", "Clear scrollback", "Session", "close", "",()=>Do(()=>Shell.ActiveSession?.Terminal.ClearScrollback()),HasSession);
        Add("capture", "Start / stop capture", "Tools", "record", primary+"+Shift+R",CaptureAsync,HasSession);
        Add("save-capture", "Save capture", "Tools", "save", "",SaveCaptureAsync,HasSession);
        Add("load-replay", "Open recording", "Tools", "play", "",LoadReplayAsync);
        Add("commands", "Command library", "Tools", "code", "",()=>Do(()=>Shell.SetTool("Commands")));
        Add("history", "Shell command history", "Tools", "history", "",()=>Do(()=>Shell.SetTool("History")));
        Add("files", "Browse local files", "Tools", "folder", "",()=>Do(()=>Shell.SetTool("Files")));
        Add("notes", "Workspace notes", "Tools", "note", "",()=>Do(()=>Shell.SetTool("Notes")));
        Add("diagnostics", "Runtime diagnostics", "Help", "code", "",()=>Do(ShowDiagnostics));
        Add("about", "About Tessera", "Help", "help", "",()=>Do(ShowAbout));
    }
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if(e.Key == Key.Escape)
        {
            if(IsOverlayOpen) { DismissOverlay(); e.Handled = true; return; }
            _dock.CancelDrag(); if(Shell.BroadcastEnabled)Shell.SetBroadcast(false);
        }
        if(IsOverlayOpen || !Shell.Initialized) return;
        if(e.Key == Key.P && e.KeyModifiers == (KeyModifiers.Control|KeyModifiers.Shift)) { ShowPalette(); e.Handled = true; return; }
        _commands.Handle(e);
    }
    private void RenderShell()
    {
        if(!Shell.Initialized) return;
        _commands.ApplyBindings(Shell.Data.Bindings); BuildMenu(); BuildSidebar(); BuildRail(); BuildToolbar();
        Q<TextBlock>("WorkspaceTitle").Text = Shell.Active.Name; Q<TextBlock>("WorkspaceDescription").Text = Shell.Active.Description;
        Q<TextBlock>("WorkspaceSigil").Text = Shell.Active.Name[..1].ToUpperInvariant();
        Q<Button>("ThemeButton").Content = Ui.Icon("sun");
        if(Q<Button>("PaletteButton").Content is Grid paletteGrid)
            paletteGrid.Children.OfType<TextBlock>().Last().Text = OperatingSystem.IsMacOS() ? "⌘ K" : "Ctrl K";
        Q<TextBlock>("Avatar").Text = Shell.DesignMode ? "WS" : new string(Environment.UserName.Where(char.IsLetterOrDigit).Take(2).ToArray()).ToUpperInvariant();
        if(!DockDragActive) { _dock.DetachAll(); foreach(var host in _floatingHosts.Values) host.DetachAll(); ReconcileFloatingWindows(); _dock.Rebuild(); foreach(var host in _floatingHosts.Values) host.Rebuild(); } BuildTools(); UpdateResponsiveLayout(); RefreshStatus();
    }
    private void BuildMenu()
    {
        _menuBinding.Refresh();
        foreach (var menu in _floatingMenus.Values) menu.Refresh();
    }

    private Button CommandButton(string id, bool iconOnly = false, string? style = null)
    {
        var c=_commands[id];var b=iconOnly?Ui.IconButton(c.Icon,c.Title+ (c.Gesture.Length>0?" · "+c.Gesture:""),()=>c.Execute(null)):Ui.Button(id switch { "save-layout" => "Save layout", "layouts" => "Layouts", _ => c.Title },c.Icon,()=>c.Execute(null),style);
        b.IsEnabled=c.CanExecute(null);return b;
    }
    private void BuildRail()
    {
        var top=Q<StackPanel>("RailTop");var bottom=Q<StackPanel>("RailBottom");top.Children.Clear();bottom.Children.Clear();
        foreach(var id in new[]{"new","profiles","layouts","commands","load-replay","find"})top.Children.Add(CommandButton(id,true));
        bottom.Children.Add(CommandButton("about",true));bottom.Children.Add(CommandButton("settings",true));
    }
    private void BuildSidebar()
    {
        var side=Q<StackPanel>("SideContent");side.Children.Clear();
        var eyebrow=Ui.Text("WORKSPACES",9,"Faint");eyebrow.LetterSpacing=1.4;
        side.Children.Add(Ui.Row("*,Auto",eyebrow,CommandButton("new-workspace",true)));
        foreach(var w in Shell.Data.Workspaces)
        {
            var button=Ui.Button("",null,()=>Shell.SwitchWorkspace(w.Id),"sideItem");
            button.Content=Ui.Row("24,*,Auto",Ui.Icon("layout",14),Ui.Text(w.Name,12,w.Id==Shell.Active.Id?"Accent":"Muted"),Ui.Text(w.Documents.Count.ToString(),10,"Faint"));
            if(w.Id==Shell.Active.Id)button.Classes.Add("selected");side.Children.Add(button);
        }
        var label=Ui.Text("PINNED CONNECTIONS",9,"Faint");label.Margin=new Thickness(8,26,0,8);label.LetterSpacing=1.1;side.Children.Add(Ui.Row("*,Auto",label,CommandButton("profiles",true)));
        foreach(var profile in Shell.Profiles.Document.Profiles.Take(30))
        {
            var button=Ui.Button("",null,()=>Run(async()=>{await Shell.NewTerminalAsync(profile.Id);}),"sideItem");
            button.Content=Ui.Row("24,*,Auto",Ui.Icon(profile.Transport.TransportId=="pty"?"terminal":"server",14),Ui.Text(profile.DisplayName,11,"Muted"),Ui.Text(Shell.Profiles.Production.Contains(profile.Id)?"PROD":"",8,"Warning"));
            button.ContextMenu=new ContextMenu{ItemsSource=new[]{new MenuItem{Header="Edit connection",Command=new AppCommand("edit", "Edit", "", "", "",()=>{ShowProfiles(profile.Id);return Task.CompletedTask;},ex=>ShowError(ex.Message))}}};side.Children.Add(button);
        }
        var saved=Ui.Text("SAVED LAYOUTS",9,"Faint");saved.LetterSpacing=1.2;saved.Margin=new Thickness(8,28,0,10);side.Children.Add(saved);
        if(Shell.Data.Layouts.Length==0)side.Children.Add(Ui.Button("Save this arrangement","save",()=>_commands["save-layout"].Execute(null),"sideItem"));
        foreach(var layout in Shell.Data.Layouts)
            side.Children.Add(Ui.Button(layout.Name,"layout",()=>Run(async()=>{if(!Shell.Active.Documents.Any()||await ConfirmAsync("Restore layout?","Current workspace sessions will be closed and replaced by disconnected slots.","Restore layout")){await Shell.RetainWorkspaceCapturesAsync();Shell.RestoreLayout(layout);}}),"sideItem"));
    }
    private void BuildToolbar()
    {
        var actions=Q<StackPanel>("WorkspaceActions");actions.Children.Clear();actions.Children.Add(CommandButton("save-layout"));actions.Children.Add(CommandButton("layouts"));actions.Children.Add(CommandButton("new",false,"primary"));
        var context=Q<StackPanel>("ContextActions");context.Children.Clear();var broadcast=Ui.Button(Shell.BroadcastEnabled?"Broadcast armed":"Broadcast off","broadcast",ShowBroadcast);if(Shell.BroadcastEnabled)broadcast.Classes.Add("selected");context.Children.Add(broadcast);
        foreach(var id in new[]{"find","split-right","split-down","focus"})context.Children.Add(CommandButton(id,true));
        var status=Q<StackPanel>("StatusActions");status.Children.Clear();
        status.Children.Add(Ui.Button(Shell.Data.Preferences.Theme,null,CycleTheme));status.Children.Add(Ui.Button("−",null,()=>ChangeFont(-1)));status.Children.Add(Ui.Text(Shell.Data.Preferences.FontSize.ToString("0")+" px",9,"Faint"));status.Children.Add(Ui.Button("+",null,()=>ChangeFont(1)));
    }
    private void RefreshStatus()
    {
        if(!Dispatcher.UIThread.CheckAccess()){Dispatcher.UIThread.Post(RefreshStatus);return;}
        if(_closed||!Shell.Initialized||_initializing)return;
        _dock.UpdateStatus();foreach(var host in _floatingHosts.Values)host.UpdateStatus();Q<TextBlock>("SessionCount").Text=$"{Shell.Active.Documents.Count} sessions";
        var session=Shell.ActiveSession;Q<TextBlock>("WorkingDirectoryText").Text=session?.WorkingDirectory??session?.Profile.Transport.Pty.WorkingDirectory??"~";
        Q<TextBlock>("Status").Text=(Shell.DesignMode?"DESIGN FIXTURE  ·  ":"")+Shell.Status+"  ·  RoyalTerminal "+(session?.Terminal.IsUsingNativeVtProcessor==true?"Ghostty VT":"Managed VT");
        foreach(var command in _commands.All)command.Refresh();
    }
    public void SetTransientStatus(string text)=>Q<TextBlock>("Status").Text=text;
    private void UpdateResponsiveLayout()
    {
        var narrow=Bounds.Width>0&&Bounds.Width<1120;var sidebar=Shell.SidebarVisible&&!Shell.FocusMode&&!narrow;
        Q<Border>("Sidebar").IsVisible=sidebar;Q<Grid>("ShellGrid").ColumnDefinitions[1].Width=new GridLength(sidebar?226:0);
        Q<Button>("PaletteButton").IsVisible=!narrow;Q<Menu>("MainMenu").IsVisible=Bounds.Width>=930||Bounds.Width==0;
        var grid=Q<Grid>("ContentGrid");var tools=Shell.ToolsVisible&&!Shell.FocusMode;var right=Shell.Data.Preferences.ToolsOnRight;
        Q<Border>("ToolsBorder").IsVisible=tools;var splitter=Q<GridSplitter>("ToolsSplitter");splitter.IsVisible=tools;
        if(right)
        {
            grid.RowDefinitions=new RowDefinitions("*");grid.ColumnDefinitions=new ColumnDefinitions{new(1,GridUnitType.Star),new(new GridLength(tools?4:0)),new(new GridLength(tools?Math.Clamp(Shell.Data.Preferences.ToolSize,280,600):0))};
            Grid.SetRow(splitter,0);Grid.SetColumn(splitter,1);splitter.Height=double.NaN;splitter.Width=4;splitter.ResizeDirection=GridResizeDirection.Columns;splitter.VerticalAlignment=VerticalAlignment.Stretch;
            Grid.SetRow(Q<Border>("ToolsBorder"),0);Grid.SetColumn(Q<Border>("ToolsBorder"),2);
        }
        else
        {
            grid.ColumnDefinitions=new ColumnDefinitions("*");grid.RowDefinitions=new RowDefinitions{new(1,GridUnitType.Star),new(new GridLength(tools?4:0)),new(new GridLength(tools?Math.Clamp(Shell.Data.Preferences.ToolSize,100,500):0))};
            Grid.SetRow(splitter,1);Grid.SetColumn(splitter,0);splitter.Height=4;splitter.Width=double.NaN;splitter.ResizeDirection=GridResizeDirection.Rows;splitter.HorizontalAlignment=HorizontalAlignment.Stretch;
            Grid.SetRow(Q<Border>("ToolsBorder"),2);Grid.SetColumn(Q<Border>("ToolsBorder"),0);
        }
        Q<Grid>("Root").RowDefinitions[0].Height=new GridLength(Shell.Data.Preferences.Compact?45:55);
        Q<Grid>("MainGrid").RowDefinitions[0].Height=new GridLength(Shell.Data.Preferences.Compact?64:81);
    }
    private void SelectTab(int delta)
    {
        var group=Layout.Groups(Shell.Active).Single(g=>g.Id==Shell.ActiveGroupId);if(group.Tabs.Length==0)return;
        var index=Array.IndexOf(group.Tabs,Shell.ActiveDocumentId??Guid.Empty);Shell.Select(group.Tabs[(index+delta+group.Tabs.Length)%group.Tabs.Length]);
    }
    private void CycleTheme()=>Shell.SetTheme(ThemeManager.Current switch{"Obsidian"=>"Porcelain","Porcelain"=>"Blueprint",_=>"Obsidian"});
    private void ChangeFont(double delta)=>Shell.SetPreferences(Shell.Data.Preferences with{FontSize=Math.Clamp(Shell.Data.Preferences.FontSize+delta,8,36),OverrideProfileFont=true});
    public async Task CloseTabAsync(Guid id)
    {
        if(!Shell.Active.Documents.TryGetValue(id,out var document))return;var session=Shell.Sessions.Find(id);
        if((document.Pinned||(Shell.Data.Preferences.ConfirmClose&&session?.IsRunning==true))&&!await ConfirmAsync("Close "+document.Title+"?","This stops its process. Other panes and sessions remain open.","Close terminal"))return;
        if(session is not null) { await Shell.Recovery.RetainOnCloseAsync(session);  }
        Shell.CloseDocument(id);
    }
    private async Task RenameTabAsync(Guid id)
    {
        var title=await PromptAsync("Rename terminal","A stable tab name stays readable even when the shell title changes.",Shell.Active.Documents[id].Title);if(!string.IsNullOrWhiteSpace(title))Shell.RenameDocument(id,title);
    }
    public async Task UnlockAsync(SessionRuntime session)
    {
        if(session.IsReplay)return;
        if(!session.Locked){session.Locked=true;session.BroadcastTarget=false;Shell.SetBroadcast(false);QueueRender();return;}
        if(await ConfirmAsync(session.IsProduction?"Unlock production input?":"Unlock terminal input?",session.IsProduction?"You are enabling writes to a production connection. Verify the host and the active pane before typing. Production is always excluded from broadcast.":"Enable input for this session.","Unlock input")){session.Locked=false;QueueRender();}
    }
    public ContextMenu TabContextMenu(Guid id)
    {
        MenuItem Item(string text,Func<Task> action)=>new(){Header=text,Command=new AppCommand(text,text,"","","",action,ex=>ShowError(ex.Message))};
        return new ContextMenu{ItemsSource=new[]{Item("Rename…",()=>RenameTabAsync(id)),Item("Pin / unpin",()=>{Shell.TogglePin(id);return Task.CompletedTask;}),Item("Duplicate connection",async()=>{await Shell.NewTerminalAsync(Shell.Active.Documents[id].ProfileId);}),Item("Float in window",()=>{FloatTerminal(id);return Task.CompletedTask;}),Item("Move right",()=>{var groups=Layout.Groups(Shell.Active.Root).ToArray();var target=groups.Last();Shell.Move(id,target.Id,DockEdge.Right);return Task.CompletedTask;}),Item("Close",()=>CloseTabAsync(id))}};
    }
    public void ShowSessionMenu(Guid id){Shell.Select(id);ShowPalette("Session");}
    public bool DockDragActive { get; set; }
    private bool _reconcilingWindows;
    public bool IsFloating(Guid id) => Shell.Active.Floating.Any(f => Layout.Groups(f.Root).Any(g => g.Tabs.Contains(id)));
    public void ReturnFloating(Guid id)
    {
        if(IsFloating(id)) Shell.Move(id, Layout.Groups(Shell.Active.Root).First().Id, DockEdge.Center);
    }
    public void RefreshDockWindows() => QueueRender();
    private void FloatTerminal(Guid id)
    {
        var existing = Shell.Active.Floating.FirstOrDefault(f => Layout.Groups(f.Root).Any(g => g.Tabs.Contains(id)));
        if(existing is not null && _floating.TryGetValue(existing.Id, out var view)) { view.Activate(); return; }
        Shell.Apply(Layout.Float(Shell.Active, id)); Shell.Select(id);
    }
    private void ReconcileFloatingWindows()
    {
        _reconcilingWindows = true;
        try
        {
            foreach(var id in _floating.Keys.Where(id => !Shell.Active.Floating.Any(f => f.Id == id)).ToArray())
            {
                _floatingHosts[id].DetachAll(); _floatingHosts.Remove(id);
                var removed = _floating[id]; _floating.Remove(id); removed.Close();
            }
            foreach(var model in Shell.Active.Floating)
            {
                if(_floating.TryGetValue(model.Id, out var current)) { current.Background = ThemeManager.Brush("Surface"); continue; }
                var id = model.Id; var workspaceId = Shell.Active.Id;
                var host = new DockHost(this, Shell, id);
                var caption = Ui.Row("*,Auto", Ui.Text(Shell.Active.Name + " · Tessera", 12, "Muted"),
                    Ui.Button("Return all tabs", "layout", () => Shell.Apply(Layout.ReturnWindow(Shell.Active, id))));
                caption.Margin = new Thickness(12,8); var content = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
                content.Children.Add(caption); Grid.SetRow(host,1); content.Children.Add(host);
                var window = new Window { Title = Shell.Active.Name + " — Tessera", Width = model.Width, Height = model.Height,
                    MinWidth=360, MinHeight=240, Background=ThemeManager.Brush("Surface"), Content=content };
                _floating[id]=window; _floatingHosts[id]=host;
                _floatingMenus[id] = new WindowMenuBinding(window, _commands);
                window.Closed += (_, _) =>
                {
                    if (_floatingMenus.Remove(id, out var menu)) menu.Dispose();
                };
                window.AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
                window.Closing += (_, e) =>
                {
                    if(_reconcilingWindows || _closed) { host.DetachAll(); return; }
                    e.Cancel = true;
                    if(Shell.Active.Id == workspaceId) Shell.Apply(Layout.ReturnWindow(Shell.Active, id));
                };
                window.SizeChanged += (_, _) =>
                {
                    if(_reconcilingWindows || _closed || Shell.Active.Id != workspaceId) return;
                    var item = Shell.Active.Floating.FirstOrDefault(f => f.Id == id); if(item is null) return;
                    var width = Math.Clamp(window.Width,360,16384); var height = Math.Clamp(window.Height,240,16384);
                    Shell.Apply(Shell.Active with { Floating = Shell.Active.Floating.Select(f => f.Id == id ? f with { Width=width,Height=height } : f).ToArray() },false,false);
                };
                window.Show(this);
            }
        }
        finally { _reconcilingWindows = false; }
    }
    public void CloseForTests(){_closing=true;Close();}
}
