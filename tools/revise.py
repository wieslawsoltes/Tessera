from pathlib import Path

# One-shot, asserted source migration. The workflow removes this helper after committing.
def edit(path, old, new, count=1):
    p = Path(path)
    source = p.read_text()
    actual = source.count(old)
    if actual != count:
        raise RuntimeError(f'{path}: expected {count} exact matches, found {actual}: {old[:100]}')
    p.write_text(source.replace(old, new))

edit('src/Tessera.App/Views/DockHost.cs', 'new(4),', 'new(new GridLength(4)),', 2)
edit('src/Tessera.App/Views/DockHost.cs', '(point - _dragOrigin).Length < 7', 'Math.Sqrt(Math.Pow(point.X - _dragOrigin.X, 2) + Math.Pow(point.Y - _dragOrigin.Y, 2)) < 7')
edit('src/Tessera.App/Views/DockHost.cs', '        var host = new ScrollViewer { Content = session.Terminal,', '''        if(_window.IsFloating(active))
        {
            var placeholder = Ui.Stack(Ui.Text("This terminal is in its own window.", 16), Ui.Button("Return to workspace", "layout", () => _window.ReturnFloating(active)));
            placeholder.HorizontalAlignment = HorizontalAlignment.Center;
            placeholder.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(placeholder, 2); grid.Children.Add(placeholder); return outer;
        }
        var host = new ScrollViewer { Content = session.Terminal,''')
edit('src/Tessera.App/Views/MainWindow.axaml.cs', '()=>Do(ShowPalette)', '()=>Do(()=>ShowPalette())')
edit('src/Tessera.App/Views/MainWindow.axaml.cs', 'b.Command=c;return b;', 'b.IsEnabled=c.CanExecute(null);return b;')
edit('src/Tessera.App/Views/MainWindow.axaml.cs', 'new(tools?4:0)', 'new(new GridLength(tools?4:0))', 2)
edit('src/Tessera.App/Views/MainWindow.axaml.cs', 'new(tools?Math.Clamp(Shell.Data.Preferences.ToolSize,280,600):0)', 'new(new GridLength(tools?Math.Clamp(Shell.Data.Preferences.ToolSize,280,600):0))')
edit('src/Tessera.App/Views/MainWindow.axaml.cs', 'new(tools?Math.Clamp(Shell.Data.Preferences.ToolSize,100,500):0)', 'new(new GridLength(tools?Math.Clamp(Shell.Data.Preferences.ToolSize,100,500):0))')
edit('src/Tessera.App/Views/MainWindow.axaml.cs', 'Ui.Button(c.Title,c.Icon,()=>c.Execute(null),style)', 'Ui.Button(id switch { "save-layout" => "Save layout", "layouts" => "Layouts", _ => c.Title },c.Icon,()=>c.Execute(null),style)')
edit('src/Tessera.App/Views/MainWindow.axaml.cs', '        Shell.SessionCreated += ConfigureSession;', '''        Shell.SessionCreated += ConfigureSession;
        Shell.Profiles.Saved += () =>
        {
            foreach(var session in Shell.Sessions.All)
            {
                session.ApplyProductionPolicy(Shell.Profiles.Production.Contains(session.Profile.Id));
                var profile = Shell.Profiles.Document.Profiles.FirstOrDefault(p => p.Id == session.Profile.Id);
                if(profile is not null) session.ApplyProfile(profile);
            }
            Shell.SetBroadcast(false);
        };''')
edit('src/Tessera.App/Views/MainWindow.axaml.cs', '        session.Terminal.TerminalFontSize = Shell.Data.Preferences.FontSize * .75;', '''        session.Terminal.CloseRequested += (_, _) => Dispatcher.UIThread.Post(() => Run(() => CloseTabAsync(session.Id)));
        session.Terminal.TerminalFontSize = Shell.Data.Preferences.FontSize * .75;''')
edit('src/Tessera.App/Views/MainWindow.axaml.cs', 'if(_closed||!Shell.Initialized)return;', 'if(_closed||!Shell.Initialized||_initializing)return;')
edit('src/Tessera.App/Views/MainWindow.axaml.cs', 'finally { _initializing = false; }', 'finally { _initializing = false; RefreshStatus(); }')
edit('src/Tessera.App/Views/MainWindow.Dialogs.cs', 'using Avalonia.Threading;', 'using Avalonia.Threading;\nusing Avalonia.Platform.Storage;')
edit('src/Tessera.App/Views/MainWindow.Dialogs.cs', 'new TerminalSettingsPanel{State=state}', 'new TerminalSettingsPanel{DataContext=state}')
edit('src/Tessera.App/Views/MainWindow.Dialogs.cs', '        async Task Save()\n', '''        state.PropertyChanged += (_, e) =>
        {
            if(e.PropertyName == nameof(state.SelectedProfile))
                production.IsChecked = state.SelectedProfile is {} selectedProfile && Shell.Profiles.Production.Contains(selectedProfile.Id);
        };
        async Task Save()
''')
edit('src/Tessera.App/Views/Ui.cs', 'Watermark = watermark', 'PlaceholderText = watermark')
edit('src/Tessera.App/Views/Ui.cs', '        button.Click += (_, _) => action(); return button;', '''        if(style == "primary")
        {
            foreach(var text in row.Children.OfType<TextBlock>()) { text.Foreground = ThemeManager.Brush("AccentInk"); text.FontSize = 11; text.FontWeight = FontWeight.SemiBold; }
            foreach(var path in row.Children.OfType<Path>()) path.Stroke = ThemeManager.Brush("AccentInk");
        }
        button.Click += (_, _) => action(); return button;''')
edit('src/Tessera.App/Views/MainWindow.Tools.cs', 'Watermark=', 'PlaceholderText=')
edit('src/Tessera.App/Views/MainWindow.Tools.cs', 'var capture=session.Capture.GetCaptureSnapshot();', 'var capture=session.Capture.GetCaptureSnapshot() ?? throw new InvalidOperationException("The capture is empty.");')
edit('src/Tessera.Core/Workspace.cs', 'bool Pinned = false);', 'bool Pinned = false, bool IsReplay = false);')
edit('src/Tessera.App/Services/SessionRuntime.cs', '    private bool _disposed;', '    private bool _disposed;\n    private bool _replaySlot;\n    private readonly ProfileRepository _profiles;')
edit('src/Tessera.App/Services/SessionRuntime.cs', 'public bool IsReplay => Capture.IsReplayEnabled;', 'public bool IsReplay => _replaySlot || Capture.IsReplayEnabled;\n    public void MarkReplaySlot() { _replaySlot = true; Locked = true; State = "Replay · read-only"; }')
edit('src/Tessera.App/Services/SessionRuntime.cs', 'Id = id; Profile = profile; _design = design;', 'Id = id; Profile = profile; _design = design; _profiles = profiles;')
edit('src/Tessera.App/Services/SessionRuntime.cs', 'TerminalSessionProfileMapper.ToTransportOptions(Profile)', '_profiles.RuntimeOptions(Profile)')
edit('src/Tessera.App/Services/SessionRuntime.cs', 'if(_sessions.TryGetValue(document.Id, out var existing)) return existing;', 'if(_sessions.TryGetValue(document.Id, out var existing)) { if(document.IsReplay && !existing.IsReplay) existing.MarkReplaySlot(); return existing; }')
edit('src/Tessera.App/Services/SessionRuntime.cs', '_sessions.Add(document.Id, session); return session;', 'if(document.IsReplay) session.MarkReplaySlot();\n        _sessions.Add(document.Id, session); return session;')
edit('src/Tessera.App/Services/ShellController.cs', 'public SessionRuntime? ActiveSession => ActiveDocument is {} document ? GetSession(document) : null;', '''public SessionRuntime? ActiveSession
    {
        get
        {
            if(ActiveDocument is not {} document) return null;
            try { return GetSession(document); }
            catch(InvalidOperationException) { return null; }
        }
    }''')
edit('src/Tessera.App/Services/ShellController.cs', 'if(DesignMode || session.Profile.Transport.TransportId == TerminalTransportIds.Pty)', 'if(!document.IsReplay && (DesignMode || session.Profile.Transport.TransportId == TerminalTransportIds.Pty))')
edit('src/Tessera.App/Services/ShellController.cs', '    public void RenameDocument(Guid id, string title)', '    public void MarkReplay(Guid id) => Apply(Layout.Update(Active, Active.Documents[id] with { IsReplay = true }), false);\n    public void RenameDocument(Guid id, string title)')
edit('src/Tessera.App/Views/MainWindow.Tools.cs', 'var session=Shell.GetSession(document);session.Capture.LoadReplay', 'Shell.MarkReplay(document.Id);var session=Shell.GetSession(Shell.Active.Documents[document.Id]);session.Capture.LoadReplay')
edit('src/Tessera.App/Services/ProfileRepository.cs', '    public TerminalSessionProfilesDocument Document', '    private readonly Dictionary<string, string> _proxyPasswords = new(StringComparer.Ordinal);\n    public TerminalSessionProfilesDocument Document')
edit('src/Tessera.App/Services/ProfileRepository.cs', '        var clean = doc.Profiles.Select(profile =>', '        if(selected is not null && !string.IsNullOrEmpty(editor.SshProxyPassword)) _proxyPasswords[selected] = editor.SshProxyPassword;\n        var clean = doc.Profiles.Select(profile =>')
edit('src/Tessera.App/Services/ProfileRepository.cs', '() => prompt($"Password for {request.Endpoint.Username}@{request.Endpoint.Host}")', '() => prompt($"Password for {request.Endpoint.Username}@{request.Endpoint.Host}").WaitAsync(cancellationToken)')
edit('src/Tessera.App/Services/ProfileRepository.cs', '    public void AddDesignProfiles()', '''    public ITerminalTransportOptions RuntimeOptions(TerminalSessionProfile profile)
    {
        var options = TerminalSessionProfileMapper.ToTransportOptions(profile);
        if(options is SshTransportOptions ssh && ssh.Proxy is {} proxy && _proxyPasswords.TryGetValue(profile.Id, out var password))
            return ssh with { Proxy = proxy with { Password = password } };
        return options;
    }

    public void AddDesignProfiles()''')
edit('tests/Tessera.App.Tests/NativeUiTests.cs', 'Tessera.App.Tests', 'Tessera.NativeTests', 2)
edit('Tessera.slnx', '<Project Path="tests/Tessera.Tests/Tessera.Tests.csproj"/>', '<Project Path="tests/Tessera.Tests/Tessera.Tests.csproj"/><Project Path="tests/Tessera.App.Tests/Tessera.App.Tests.csproj"/>')
print('All exact-match source revisions applied.')
