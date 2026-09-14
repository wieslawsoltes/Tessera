using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using RoyalTerminal.Avalonia.Settings;
using Tessera.Core;
using Tessera.Services;

namespace Tessera.Views;

public sealed partial class MainWindow
{
    private Action? _onDismiss;
    private IInputElement? _previousFocus;
    private void ShowOverlay(string title, string description, Control body, double width = 800)
    {
        DismissOverlay();
        _previousFocus = FocusManager?.GetFocusedElement();
        var titleText = Ui.Text(title,24);titleText.FontWeight=FontWeight.SemiBold;
        var subtitle=Ui.Text(description,12,"Muted");subtitle.TextWrapping=TextWrapping.Wrap;
        var header=Ui.Row("*,Auto",Ui.Stack(titleText,subtitle),Ui.IconButton("close","Close dialog",DismissOverlay));
        var grid=new Grid{RowDefinitions=new RowDefinitions("Auto,*"),Margin=new Thickness(26)};
        header.Margin=new Thickness(0,0,0,22);grid.Children.Add(header);
        var scroll=new ScrollViewer{Content=body,MaxHeight=555,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};Grid.SetRow(scroll,1);grid.Children.Add(scroll);
        Q<Border>("OverlayPanel").Width=width;Q<ContentControl>("OverlayContent").Content=grid;Q<Border>("OverlayShade").IsVisible=true;
        KeyboardNavigation.SetTabNavigation(grid,KeyboardNavigationMode.Cycle);
        Dispatcher.UIThread.Post(()=>body.Focus(),DispatcherPriority.Input);
    }
    private void DismissOverlay()
    {
        var callback=_onDismiss;_onDismiss=null;
        Q<Border>("OverlayShade").IsVisible=false;Q<ContentControl>("OverlayContent").Content=null;
        callback?.Invoke();_previousFocus?.Focus();_previousFocus=null;
    }
    private Task<string?> PromptAsync(string title,string description,string initial,bool password=false)
    {
        var input=Ui.Input(initial);input.MaxLength=password?4096:120;if(password)input.PasswordChar='●';
        var completion=new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Submit(){var value=input.Text??"";completion.TrySetResult(value);DismissOverlay();}
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10,HorizontalAlignment=HorizontalAlignment.Right};actions.Children.Add(Ui.Button("Cancel",null,DismissOverlay));actions.Children.Add(Ui.Button(password?"Authenticate":"Save",null,Submit,"primary"));
        ShowOverlay(title,description,Ui.Stack(input,actions),560);_onDismiss=()=>completion.TrySetResult(null);
        input.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){e.Handled=true;Submit();}};
        Dispatcher.UIThread.Post(()=>{input.Focus();input.SelectAll();},DispatcherPriority.Input);return completion.Task;
    }
    private Task<bool> ConfirmAsync(string title,string description,string accept)
    {
        var completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10,HorizontalAlignment=HorizontalAlignment.Right};
        actions.Children.Add(Ui.Button("Cancel",null,DismissOverlay));var yes=Ui.Button(accept,null,()=>{completion.TrySetResult(true);DismissOverlay();},"primary");actions.Children.Add(yes);
        ShowOverlay(title,description,actions,590);_onDismiss=()=>completion.TrySetResult(false);return completion.Task;
    }
    private void ShowError(string message)
    {
        ShowOverlay("This action needs attention",message,Ui.Button("Dismiss",null,DismissOverlay,"primary"),600);
    }
    private sealed record PaletteEntry(string Title,string Detail,string Icon,string Shortcut,Action Execute);
    public void ShowPalette(string initial="")
    {
        var search=Ui.Input(initial,"What would you like to do?");
        var list=new ListBox{MaxHeight=420,Background=Brushes.Transparent,BorderThickness=new Thickness(0)};
        list.ItemTemplate=new FuncDataTemplate<PaletteEntry>((entry,_)=>
        {
            var details=Ui.Stack(Ui.Text(entry!.Title,12),Ui.Text(entry.Detail,10,"Faint"));details.Spacing=4;
            var row=Ui.Row("30,*,Auto",Ui.Icon(entry.Icon),details,Ui.Text(entry.Shortcut,10,"Faint"));row.Margin=new Thickness(4,6);return row;
        });
        var entries=_commands.All.Select(c=>new PaletteEntry(c.Title,c.Category,c.Icon,c.Gesture,()=>c.Execute(null))).Concat(Shell.Data.Workspaces.Select(w=>new PaletteEntry(w.Name,"Switch workspace","layout","",()=>Shell.SwitchWorkspace(w.Id)))).Concat(Shell.Profiles.Document.Profiles.Select(p=>new PaletteEntry(p.DisplayName,"Connect · "+p.Transport.TransportId,"server","",()=>Run(async()=>{await Shell.NewTerminalAsync(p.Id);})))) .ToArray();
        void Filter(){var query=search.Text??"";list.ItemsSource=entries.Where(x=>(x.Title+" "+x.Detail).Contains(query,StringComparison.OrdinalIgnoreCase)).OrderByDescending(x=>x.Title.StartsWith(query,StringComparison.OrdinalIgnoreCase)).ToArray();list.SelectedIndex=0;}
        void Execute(){if(list.SelectedItem is not PaletteEntry entry)return;DismissOverlay();entry.Execute();}
        search.TextChanged+=(_,_)=>Filter();search.KeyDown+=(_,e)=>{if(e.Key==Key.Down){list.SelectedIndex=Math.Min(list.ItemCount-1,list.SelectedIndex+1);e.Handled=true;}if(e.Key==Key.Up){list.SelectedIndex=Math.Max(0,list.SelectedIndex-1);e.Handled=true;}if(e.Key==Key.Enter){e.Handled=true;Execute();}};
        list.DoubleTapped+=(_,_)=>Execute();list.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){e.Handled=true;Execute();}};
        var hint=Ui.Text("↑ ↓ navigate     ↵ run command     Esc close",10,"Faint");
        ShowOverlay("A place for every command.","Commands, connections, and workspaces — without leaving the keyboard.",Ui.Stack(search,list,hint),720);Filter();Dispatcher.UIThread.Post(()=>{search.Focus();search.CaretIndex=search.Text?.Length??0;});
    }
    public void ShowLayouts()
    {
        var grid=new UniformGrid{Columns=3,Rows=2};
        foreach(var name in new[]{"Focus","Two columns","Two rows","Build & monitor","Three columns","Four-way inspection"})
        {
            var preview=new Grid{Height=84,Margin=new Thickness(0,0,0,12)};
            var columns=name=="Three columns"?3:name is "Focus" or "Two rows"?1:2;
            var rows=name is "Two rows" or "Four-way inspection"?2:1;
            preview.ColumnDefinitions=new ColumnDefinitions(string.Join(",",Enumerable.Repeat("*",columns)));preview.RowDefinitions=new RowDefinitions(string.Join(",",Enumerable.Repeat("*",rows)));
            for(int c=0;c<columns;c++)for(int r=0;r<rows;r++){var rectangle=new Border{Background=ThemeManager.Brush(c==0&&r==0?"AccentWash":"Raised"),BorderBrush=ThemeManager.Brush("Line"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(4),Margin=new Thickness(2)};Grid.SetColumn(rectangle,c);Grid.SetRow(rectangle,r);preview.Children.Add(rectangle);}
            if(name=="Build & monitor")preview.Children.Add(new Border{Background=ThemeManager.Brush("Line"),Height=3,HorizontalAlignment=HorizontalAlignment.Right,Width=82});
            var card=Ui.Button("",null,()=>{Shell.Arrange(name);DismissOverlay();});card.Content=Ui.Stack(preview,Ui.Text(name,12));card.Margin=new Thickness(5);card.Padding=new Thickness(14);card.BorderBrush=ThemeManager.Brush("Line");card.BorderThickness=new Thickness(1);card.HorizontalContentAlignment=HorizontalAlignment.Stretch;grid.Children.Add(card);
        }
        ShowOverlay("A layout for the task at hand.","Rearrange your open terminals without restarting a single session.",Ui.Stack(grid,Ui.Text("Drag a tab to a pane edge to split, or to its center to join the tab group.",11,"Muted")),790);
    }
    public void ShowSettings()
    {
        var appearance=new StackPanel{Spacing=18};
        var themes=new UniformGrid{Columns=3};
        foreach(var name in new[]{"Obsidian","Porcelain","Blueprint"})
        {
            var colors=ThemeManager.Palettes[name];var strip=new StackPanel{Orientation=Orientation.Horizontal,Spacing=5};
            foreach(var color in new[]{colors[4],colors[1],colors[10],colors[13]})strip.Children.Add(new Border{Width=30,Height=30,Background=new SolidColorBrush(Color.Parse(color)),CornerRadius=new CornerRadius(6)});
            var choice=Ui.Button("",null,()=>{Shell.SetTheme(name);ShowSettings();});choice.Content=Ui.Stack(strip,Ui.Text(name));choice.Padding=new Thickness(14);choice.Margin=new Thickness(4);choice.BorderBrush=ThemeManager.Brush(Shell.Data.Preferences.Theme==name?"Accent":"Line");choice.BorderThickness=new Thickness(1);themes.Children.Add(choice);
        }
        appearance.Children.Add(Ui.Text("COLOR & TYPE",10,"Faint"));appearance.Children.Add(themes);
        var font=Ui.Input(Shell.Data.Preferences.FontFamily,"System monospace default");var size=new NumericUpDown{Minimum=8,Maximum=36,Increment=1,Value=(decimal)Shell.Data.Preferences.FontSize,FormatString="0"};
        appearance.Children.Add(Ui.Row("3*,16,*",Ui.Field("Terminal font family",font),new Border(),Ui.Field("Size (DIP)",size)));
        var compact=new CheckBox{Content="Compact chrome",IsChecked=Shell.Data.Preferences.Compact};var motion=new CheckBox{Content="Reduce motion",IsChecked=Shell.Data.Preferences.ReducedMotion};var close=new CheckBox{Content="Confirm before closing running sessions",IsChecked=Shell.Data.Preferences.ConfirmClose};var restore=new CheckBox{Content="Restart local sessions when restoring a workspace",IsChecked=Shell.Data.Preferences.RestoreLocalSessions};
        appearance.Children.Add(Ui.Stack(compact,motion,close,restore));
        appearance.Children.Add(Ui.Button("Apply appearance", "check",()=>{Shell.SetPreferences(Shell.Data.Preferences with{FontFamily=font.Text??"",FontSize=(double)(size.Value??13),Compact=compact.IsChecked==true,ReducedMotion=motion.IsChecked==true,ConfirmClose=close.IsChecked==true,RestoreLocalSessions=restore.IsChecked==true});DismissOverlay();},"primary"));
        appearance.Children.Add(new Separator());appearance.Children.Add(Ui.Button("Edit keyboard bindings","code",ShowKeybindings));appearance.Children.Add(Ui.Button("Terminal, connection & advanced settings","settings",()=>ShowProfiles(Shell.ActiveDocument?.ProfileId)));
        ShowOverlay("Make room for your way of working.","Appearance, keyboard, and workspace behavior. Terminal-specific features remain available in connection profiles.",appearance,800);
    }
    private void ShowKeybindings()
    {
        var search=Ui.Input(null,"Filter commands or shortcuts");var rows=new StackPanel{Spacing=4};
        void Build(){rows.Children.Clear();foreach(var command in _commands.All.Where(c=>(c.Title+" "+c.Gesture).Contains(search.Text??"",StringComparison.OrdinalIgnoreCase)))
        {
            var input=Ui.Input(command.Gesture);input.Width=190;input.FontSize=11;
            var apply=Ui.IconButton("check","Save shortcut",()=>{var gesture=input.Text?.Trim()??"";var error=_commands.ValidateBinding(command.Id,gesture);if(error is not null){Shell.Report(error);ToolTip.SetTip(input,error);input.BorderBrush=ThemeManager.Brush("Danger");return;}Shell.Bind(command.Id,gesture);command.Gesture=gesture;input.BorderBrush=ThemeManager.Brush("Accent");BuildMenu();});
            var row=Ui.Row("*,200,34",Ui.Text(command.Title,12),input,apply);rows.Children.Add(row);
        }}
        search.TextChanged+=(_,_)=>Build();Build();ShowOverlay("Keyboard bindings","Use Ctrl+Shift+P, Meta+K, or a chord such as Ctrl+K,Ctrl+S. Empty removes a binding. Shell Ctrl+C/D/Z remain reserved.",Ui.Stack(search,new ScrollViewer{Content=rows,MaxHeight=440}),820);
    }
    public void ShowProfiles(string? selected=null)
    {
        var state=Shell.Profiles.CreateEditor(selected);var panel=new TerminalSettingsPanel{DataContext=state};
        var production=new CheckBox{Content="Production connection · input locked on every connect · never broadcast",IsChecked=state.SelectedProfile is {} item&&Shell.Profiles.Production.Contains(item.Id)};
        state.PropertyChanged += (_, e) =>
        {
            if(e.Property.Name == nameof(state.SelectedProfile))
                production.IsChecked = state.SelectedProfile is {} selectedProfile && Shell.Profiles.Production.Contains(selectedProfile.Id);
        };
        async Task Save()
        {
            var id=state.SelectedProfile?.Id;if(id is not null){if(production.IsChecked==true)Shell.Profiles.Production.Add(id);else Shell.Profiles.Production.Remove(id);}
            await Shell.Profiles.SaveAsync(state);Shell.Report("Connection profiles saved");QueueRender();
        }
        state.SaveRequested+=(_,_)=>Run(Save);
        state.ApplyRequested+=(_,_)=>Run(async()=>{await Save();if(Shell.ActiveSession is {} session&&state.SelectedProfile?.Id==session.Profile.Id){session.ApplyProfile(Shell.Profiles.Get(session.Profile.Id));Shell.Report("Profile presentation applied. Reconnect to apply transport changes.");}});
        state.BrowseFontFileRequested+=(_,_)=>Run(async()=>{var files=await StorageProvider.OpenFilePickerAsync(new(){Title="Choose terminal font file",AllowMultiple=false});if(files.FirstOrDefault()?.TryGetLocalPath() is {} path)state.LoadFontFile(path);});
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10};actions.Children.Add(production);
        var connect=Ui.Button("Save & connect","right",()=>Run(async()=>{await Save();var id=state.SelectedProfile?.Id;DismissOverlay();if(id is not null)await Shell.NewTerminalAsync(id);}),"primary");
        var body=Ui.Stack(Ui.Text("PTY · SSH · Pipe · Raw TCP · Telnet · Serial",11,"Muted"),panel,actions,connect);
        ShowOverlay("Connections & terminal profiles","RoyalTerminal's complete profile editor. Passwords are runtime-only. Unknown SSH host keys are never trusted automatically.",body,900);
    }
    public void ShowBroadcast()
    {
        if(Shell.BroadcastEnabled)Shell.SetBroadcast(false);
        var body=new StackPanel{Spacing=12};var targets=new List<(SessionRuntime Session,CheckBox Check)>();
        foreach(var document in Shell.Active.Documents.Values)
        {
            var session=Shell.GetSession(document);var available=!session.IsProduction&&!session.Locked&&!session.IsReplay&&session.IsRunning;
            var check=new CheckBox{Content=document.Title+(session.Id==Shell.ActiveDocumentId?" · active source":"")+(!available?" · unavailable":""),IsEnabled=available,IsChecked=available&&session.BroadcastTarget};targets.Add((session,check));body.Children.Add(check);
        }
        body.Children.Add(Ui.Text("Every selected terminal receives the same input. Escape disarms broadcast. Switching workspace also clears every target.",11,"Warning"));
        body.Children.Add(Ui.Button("Arm selected sessions","broadcast",()=>Run(()=>{foreach(var (session,check) in targets)session.BroadcastTarget=check.IsChecked==true;Shell.SetBroadcast(true);DismissOverlay();return Task.CompletedTask;}),"primary"));
        ShowOverlay("One input. Explicit destinations.","Select the active terminal and at least one additional target. Production and replay sessions cannot be selected.",body,650);
    }
    public void ShowSearch()
    {
        var session=Shell.ActiveSession;if(session is null)return;
        var search=Ui.Input(null,"Find in terminal scrollback");var count=Ui.Text("Enter a search term",11,"Muted");
        void Update(){session.Terminal.StartSearch(search.Text??"");count.Text=$"{session.Terminal.SearchSelectedDisplayIndex} of {session.Terminal.SearchTotal} matches";}
        search.TextChanged+=(_,_)=>Update();
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10};actions.Children.Add(Ui.Button("Previous",null,()=>{session.Terminal.SelectPreviousSearchMatch();count.Text=$"{session.Terminal.SearchSelectedDisplayIndex} of {session.Terminal.SearchTotal}";}));actions.Children.Add(Ui.Button("Next",null,()=>{session.Terminal.SelectNextSearchMatch();count.Text=$"{session.Terminal.SearchSelectedDisplayIndex} of {session.Terminal.SearchTotal}";}));actions.Children.Add(count);
        ShowOverlay("Find in "+Shell.ActiveDocument!.Title,"Search the native terminal buffer without modifying its contents.",Ui.Stack(search,actions),680);_onDismiss=()=>session.Terminal.EndSearch();Dispatcher.UIThread.Post(()=>search.Focus());
    }
    private void ShowDiagnostics()
    {
        var text=string.Join("\n",Shell.Sessions.All.Select(s=>$"{s.Id}  {s.Profile.DisplayName}\n  {s.State} · {s.Profile.Transport.TransportId} · {s.Terminal.Columns}x{s.Terminal.Rows} · native VT={s.Terminal.IsUsingNativeVtProcessor}\n  {s.Error}"))+"\n\n"+string.Join("\n",Shell.Events.Take(80));
        var output=new TextBox{Text=text,IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=390};
        ShowOverlay("Runtime, in plain sight.","Live session state and bounded application events. No raw keystroke logging.",output,860);
    }
    private void ShowAbout()
    {
        var body=Ui.Stack(Ui.Text("Your command line, composed.",28),Ui.Text("Tessera · 0.1.0-alpha.1",13,"Accent"),Ui.Text("A native cross-platform workspace built with C#, Avalonia 12 and RoyalTerminal.\nObsidian, Porcelain, and Blueprint — the same considered design, in every mode.",12,"Muted"),Ui.Text("Drag terminal tabs to pane edges or centers. Use the command palette to discover every action. Connection secrets stay out of workspace files.",12,"Muted"),Ui.Button("Explore the commands","right",()=>ShowPalette(),"primary"));
        foreach(var text in body.Children.OfType<TextBlock>())text.TextWrapping=TextWrapping.Wrap;
        ShowOverlay("tessera","A little less friction. A little more focus.",body,700);
    }
}
