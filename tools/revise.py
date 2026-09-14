from pathlib import Path

def edit(path, old, new, count=1):
    p = Path(path); value = p.read_text()
    actual = value.count(old)
    if actual != count: raise RuntimeError(f'{path}: expected {count}, found {actual}: {old[:90]}')
    p.write_text(value.replace(old, new))

edit('src/Tessera.App/Services/ShellController.cs', '        // Restoration rebuilds layout.', '        // Size controls before first output, rather than shrinking a populated 120x36 fixture.\n        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);\n        // Restoration rebuilds layout.')
edit('src/Tessera.App/Services/ShellController.cs', '''                var session = GetSession(document);
                if(!document.IsReplay && (DesignMode || session.Profile.Transport.TransportId == TerminalTransportIds.Pty)) await session.EnsureStartedAsync();''', '''                try
                {
                    var session = GetSession(document);
                    if(!document.IsReplay && (DesignMode || session.Profile.Transport.TransportId == TerminalTransportIds.Pty)) await session.EnsureStartedAsync();
                }
                catch(InvalidOperationException ex) { Report(ex.Message); }''')
edit('src/Tessera.App/Views/MainWindow.axaml.cs', '        ConfigureCommands();', '        ConfigureCommands();\n        RegisterAdvancedCommands();')
edit('src/Tessera.App/Views/MainWindow.axaml.cs', '        session.Terminal.PasteSafetyPolicy = TerminalPasteSafetyPolicy.ConfirmUnsafe;', '''        session.Terminal.PasteSafetyPolicy = Enum.TryParse<TerminalPasteSafetyPolicy>(session.Profile.Behavior.PasteSafetyPolicy, true, out var paste) && paste != TerminalPasteSafetyPolicy.None ? paste : TerminalPasteSafetyPolicy.ConfirmUnsafe;
        session.Terminal.ShaderAnimationEnabled = !Shell.Data.Preferences.ReducedMotion;''')
edit('src/Tessera.App/Views/MainWindow.axaml.cs', '        Q<Button>("ThemeButton").Content = Ui.Icon("sun");', '''        Q<Button>("ThemeButton").Content = Ui.Icon("sun");
        if(Q<Button>("PaletteButton").Content is Grid paletteGrid)
            paletteGrid.Children.OfType<TextBlock>().Last().Text = OperatingSystem.IsMacOS() ? "⌘ K" : "Ctrl K";''')
edit('src/Tessera.App/Views/MainWindow.axaml', 'FontSize="20" LineHeight="28" Classes="muted"', 'FontSize="18" LineHeight="25" TextWrapping="Wrap" Classes="muted"')
edit('src/Tessera.App/Views/MainWindow.axaml', '<Grid x:Name="TitleBand" ', '<Grid x:Name="TitleBand" WindowDecorationProperties.ElementRole="TitleBar" ')
for element in ('MainMenu', 'PaletteButton', 'ThemeButton'):
    edit('src/Tessera.App/Views/MainWindow.axaml', f'x:Name="{element}" ', f'x:Name="{element}" WindowDecorationProperties.ElementRole="User" ')
edit('src/Tessera.App/Views/Ui.cs', '        button.Click += (_, _) => action(); return button;', '''        foreach(var text in row.Children.OfType<TextBlock>())
        {
            text.Bind(TextBlock.FontSizeProperty, new Avalonia.Data.Binding("FontSize") { Source = button });
            text.Bind(TextBlock.ForegroundProperty, new Avalonia.Data.Binding("Foreground") { Source = button });
        }
        button.Click += (_, _) => action(); return button;''')
edit('src/Tessera.App/Views/Ui.cs', 'var b = Button("", icon, action, "icon"); ToolTip.SetTip(b, tooltip);', '''var b = Button("", icon, action, "icon");
        if(icon == "close") { b.Content = Icon(icon, 11); b.Width = 24; b.Height = 26; }
        ToolTip.SetTip(b, tooltip);''')
edit('src/Tessera.App/Views/MainWindow.Tools.cs', '''        foreach(var (name,icon) in new[]{("Commands","code"),("History","history"),("Files","folder"),("Timeline","record"),("Notes","note")})''', '''        if(Shell.Data.Preferences.ToolsOnRight)
        {
            var select = new ComboBox { ItemsSource = new[] { "Commands", "History", "Files", "Timeline", "Notes" }, SelectedItem = Shell.Data.Preferences.Tool, Width = 145, MinHeight = 28 };
            select.SelectionChanged += (_, _) => { if(select.SelectedItem is string name && name != Shell.Data.Preferences.Tool) Shell.SetTool(name); };
            tabs.Children.Add(select);
        }
        else foreach(var (name,icon) in new[]{("Commands","code"),("History","history"),("Files","folder"),("Timeline","record"),("Notes","note")})''')
edit('src/Tessera.App/Views/MainWindow.Tools.cs', 'var grid=new UniformGrid{Columns=Shell.Data.Preferences.ToolsOnRight?1:3};', 'var grid=new UniformGrid{Columns=Shell.Data.Preferences.ToolsOnRight?1:3,VerticalAlignment=VerticalAlignment.Top};')
edit('src/Tessera.App/Views/MainWindow.Tools.cs', 'card.Margin=new Thickness(0,0,12,10);', 'card.Margin=new Thickness(0,0,12,10);card.Height=108;')
edit('src/Tessera.App/Views/MainWindow.Tools.cs', 'list.Children.Add(Ui.Text("Your completed commands will appear here.",14));', 'list.Children.Add(Ui.Text("Your completed commands will appear here.",14));\n            list.Children.Add(CommandButton("shell-integration"));')
edit('src/Tessera.App/Views/MainWindow.Dialogs.cs', 'var panel=new TerminalSettingsPanel{DataContext=state};', '''var panel=new TerminalSettingsPanel{DataContext=state};
        foreach(var pair in new[] { ("BackgroundBrush", "Surface"), ("ContentBackgroundBrush", "TerminalBg"), ("BorderBrush", "Line"), ("DividerBrush", "Line"), ("SecondaryTextBrush", "Muted"), ("SubtleTextBrush", "Faint"), ("CardBorderBrush", "Line") })
            panel.Resources["TerminalSettings.Panel." + pair.Item1] = ThemeManager.Brush(pair.Item2);''')
edit('src/Tessera.App/Services/SessionRuntime.cs', '        ThemeManager.ApplyTerminal(Terminal);', '''        ThemeManager.ApplyTerminal(Terminal);
        Terminal.BackgroundOpacityEnabled = a.BackgroundOpacityEnabled;
        Terminal.PasteSafetyPolicy = Enum.TryParse<TerminalPasteSafetyPolicy>(profile.Behavior.PasteSafetyPolicy, true, out var paste) && paste != TerminalPasteSafetyPolicy.None ? paste : TerminalPasteSafetyPolicy.ConfirmUnsafe;''')
edit('src/Tessera.App/Services/ShellController.cs', 'ThemeManager.ApplyTerminal(session.Terminal); session.Terminal.TerminalFontSize', 'ThemeManager.ApplyTerminal(session.Terminal); session.Terminal.BackgroundOpacityEnabled = session.Profile.Appearance.BackgroundOpacityEnabled; session.Terminal.ShaderAnimationEnabled = !preferences.ReducedMotion; session.Terminal.TerminalFontSize')
edit('src/Tessera.App/Views/DockHost.cs', 'var split = Find(_shell.Active.Root); if(split is null) return; _shell.Apply(Layout.Resize(_shell.Active, split.Id, split.Ratio + delta), false);', 'var split = Find(_shell.Active.Root); if(split is null) return; var first = Layout.Groups(split.First).Any(g => g.Id == _shell.ActiveGroupId); _shell.Apply(Layout.Resize(_shell.Active, split.Id, split.Ratio + (first ? delta : -delta)), false);')
print('Applied native visual, keyboard, accessibility, and profile behavior refinements.')
