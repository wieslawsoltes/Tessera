using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Tessera.Core;
using Tessera.Services;

namespace Tessera.Views;

/// <summary>A renderer for the immutable dock tree. TerminalControl instances are borrowed from SessionRegistry.</summary>
public sealed class DockHost : Grid
{
    private readonly MainWindow _window;
    private readonly ShellController _shell;
    private readonly List<ScrollViewer> _terminalHosts = [];
    private readonly Dictionary<Guid, Border> _groups = [];
    private readonly Dictionary<Guid, TextBlock> _footers = [];
    private readonly Dictionary<Guid, Grid> _splitGrids = [];
    private Guid? _dragDocument;
    private Point _dragOrigin;
    private bool _dragging;
    private IPointer? _capturedPointer;
    private Guid? _targetGroup;
    private DockEdge _targetEdge;
    public DockHost(MainWindow window, ShellController shell)
    {
        _window = window; _shell = shell; ClipToBounds = true;
        AddHandler(PointerMovedEvent, DragMove, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, DragRelease, RoutingStrategies.Tunnel);
        PointerCaptureLost += (_, _) => CancelDrag();
    }
    public void Rebuild()
    {
        if(_dragging) return;
        // Explicit detach makes reparenting legal and preserves terminal/transport identity.
        foreach(var host in _terminalHosts) host.Content = null;
        _terminalHosts.Clear(); _groups.Clear(); _footers.Clear(); _splitGrids.Clear(); Children.Clear();
        DockNode root = _shell.Active.Root;
        if(_shell.FocusMode && _shell.ActiveDocumentId is {} id)
        {
            var group = Layout.Groups(root).FirstOrDefault(g => g.Tabs.Contains(id)); if(group is not null) root = group;
        }
        Children.Add(BuildNode(root));
        Dispatcher.UIThread.Post(() => { if(_shell.ActiveSession is {} session && !_window.IsOverlayOpen) session.Terminal.Focus(); }, DispatcherPriority.Loaded);
    }
    private Control BuildNode(DockNode node)
    {
        if(node is TabGroup group) return BuildGroup(group);
        var split = (DockSplit)node;
        var grid = new Grid(); _splitGrids[split.Id] = grid;
        var first = BuildNode(split.First); var second = BuildNode(split.Second);
        var splitter = new GridSplitter { Background = ThemeManager.Brush("Line"), Focusable = true, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        AutomationProperties.SetName(splitter, "Resize terminal panes");
        if(split.Axis == SplitAxis.Columns)
        {
            grid.ColumnDefinitions = new ColumnDefinitions { new(split.Ratio, GridUnitType.Star), new(new GridLength(4)), new(1 - split.Ratio, GridUnitType.Star) };
            Grid.SetColumn(splitter, 1); Grid.SetColumn(second, 2); splitter.Width = 4; splitter.HorizontalAlignment = HorizontalAlignment.Stretch; splitter.VerticalAlignment = VerticalAlignment.Stretch; splitter.ResizeDirection = GridResizeDirection.Columns;
        }
        else
        {
            grid.RowDefinitions = new RowDefinitions { new(split.Ratio, GridUnitType.Star), new(new GridLength(4)), new(1 - split.Ratio, GridUnitType.Star) };
            Grid.SetRow(splitter, 1); Grid.SetRow(second, 2); splitter.Height = 4; splitter.HorizontalAlignment = HorizontalAlignment.Stretch; splitter.VerticalAlignment = VerticalAlignment.Stretch; splitter.ResizeDirection = GridResizeDirection.Rows;
        }
        void SaveRatio()
        {
            var a = split.Axis == SplitAxis.Columns ? grid.ColumnDefinitions[0].ActualWidth : grid.RowDefinitions[0].ActualHeight;
            var b = split.Axis == SplitAxis.Columns ? grid.ColumnDefinitions[2].ActualWidth : grid.RowDefinitions[2].ActualHeight;
            if(a + b > 0) _shell.Resize(split.Id, a / (a + b));
        }
        splitter.AddHandler(PointerReleasedEvent, (_, _) => SaveRatio(), RoutingStrategies.Bubble, true);
        splitter.AddHandler(KeyUpEvent, (_, _) => SaveRatio(), RoutingStrategies.Bubble, true);
        grid.Children.Add(first); grid.Children.Add(splitter); grid.Children.Add(second); return grid;
    }
    private Control BuildGroup(TabGroup group)
    {
        var outer = new Border { BorderBrush = ThemeManager.Brush(group.Tabs.Contains(_shell.ActiveDocumentId ?? Guid.Empty) ? "Accent" : "Line"), BorderThickness = new Thickness(0, 2, 0, 0), Background = ThemeManager.Brush("TerminalBg"), ClipToBounds = true };
        _groups[group.Id] = outer; AutomationProperties.SetName(outer, "Terminal tab group");
        var grid = new Grid { RowDefinitions = new RowDefinitions("35,28,*,24") }; outer.Child = grid;
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach(var id in group.Tabs)
        {
            var doc = _shell.Active.Documents[id];
            var b = Ui.Button((doc.Pinned ? "• " : "") + doc.Title, "terminal", () => { if(!_dragging) _shell.Select(id); }, "tab");
            if(group.Active == id) b.Classes.Add("activeTab");
            ToolTip.SetTip(b, doc.Title + " · drag to a pane edge or center");
            b.PointerPressed += (_, e) => { if(e.GetCurrentPoint(b).Properties.IsLeftButtonPressed) { _dragDocument = id; _dragOrigin = e.GetPosition(this); } };
            b.ContextMenu = _window.TabContextMenu(id);
            var tab = Ui.Row("Auto,Auto", b, Ui.IconButton("close", "Close " + doc.Title, () => _window.Run(() => _window.CloseTabAsync(id))));
            tabs.Children.Add(tab);
        }
        var header = Ui.Row("*,Auto", new ScrollViewer { Content = tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }, Ui.IconButton("add", "New terminal in this group", () => _window.Run(async () => { await _shell.NewTerminalAsync(target: group.Id); })));
        header.Background = ThemeManager.Brush("Surface"); grid.Children.Add(header);
        if(group.Active is not {} active)
        {
            var empty = Ui.Stack(Ui.Text("Room for your next idea.", 24), Ui.Text("Open a terminal or choose a saved connection.", 12, "Muted"), Ui.Button("New terminal", "add", () => _window.Run(async () => { await _shell.NewTerminalAsync(target: group.Id); }), "primary"));
            empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(empty, 2); grid.Children.Add(empty); return outer;
        }
        var document = _shell.Active.Documents[active]; SessionRuntime session;
        try { session = _shell.GetSession(document); }
        catch(Exception ex) { var message = Ui.Text(ex.Message, 12, "Danger"); message.TextWrapping = TextWrapping.Wrap; Grid.SetRow(message, 2); grid.Children.Add(message); return outer; }
        var transport = session.Profile.Transport.TransportId;
        var location = transport == "ssh" ? session.Profile.Transport.Ssh.Username + "@" + session.Profile.Transport.Ssh.Host : transport == "serial" ? session.Profile.Transport.Serial.PortName : session.Profile.Transport.Pty.WorkingDirectory ?? "~";
        var info = Ui.Row("*,Auto", Ui.Text("  " + (session.IsProduction ? "PRODUCTION  ·  " : "") + transport.ToUpperInvariant() + "  /  " + location, 9, session.IsProduction ? "Warning" : "Faint"), Ui.IconButton("more", "Session actions", () => _window.ShowSessionMenu(active)));
        info.Margin = new Thickness(8, 0); Grid.SetRow(info, 1); grid.Children.Add(info);
        if(_window.IsFloating(active))
        {
            var placeholder = Ui.Stack(Ui.Text("This terminal is in its own window.", 16), Ui.Button("Return to workspace", "layout", () => _window.ReturnFloating(active)));
            placeholder.HorizontalAlignment = HorizontalAlignment.Center;
            placeholder.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(placeholder, 2); grid.Children.Add(placeholder); return outer;
        }
        var host = new ScrollViewer { Content = session.Terminal, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Background = ThemeManager.Brush("TerminalBg") };
        _terminalHosts.Add(host); Grid.SetRow(host, 2); grid.Children.Add(host);
        host.AddHandler(PointerPressedEvent, (_, _) => { if(_shell.ActiveDocumentId != active) _shell.Select(active); }, RoutingStrategies.Tunnel);
        var footerText = Ui.Text("", 9, "Faint"); _footers[active] = footerText;
        var reconnect = Ui.Button(session.Locked ? "Read-only · unlock" : "Connect / restart", session.Locked ? "lock" : "right", () => _window.Run(() => session.Locked ? _window.UnlockAsync(session) : session.ReconnectAsync()));
        reconnect.FontSize = 9; reconnect.MinHeight = 22; reconnect.Padding = new Thickness(4, 0);
        var footer = Ui.Row("*,Auto", footerText, reconnect); footer.Margin = new Thickness(12, 0); Grid.SetRow(footer, 3); grid.Children.Add(footer);
        UpdateStatus(); return outer;
    }
    public void UpdateStatus()
    {
        foreach(var (id, text) in _footers)
        {
            var session = _shell.Sessions.Find(id); if(session is null) continue;
            text.Text = $"{session.State}  ·  {session.Terminal.Columns} × {session.Terminal.Rows}" + (session.Capture.IsCaptureActive ? "  ·  REC" : "") + (session.Locked ? "  ·  READ ONLY" : "");
            text.Foreground = ThemeManager.Brush(session.Locked ? "Warning" : "Faint");
        }
    }
    private void DragMove(object? sender, PointerEventArgs e)
    {
        if(_dragDocument is null) return; var point = e.GetPosition(this);
        if(!_dragging && Math.Sqrt(Math.Pow(point.X - _dragOrigin.X, 2) + Math.Pow(point.Y - _dragOrigin.Y, 2)) < 7) return;
        _dragging = true; _capturedPointer = e.Pointer; e.Pointer.Capture(this); e.Handled = true; _targetGroup = null;
        foreach(var (id, border) in _groups)
        {
            border.BorderThickness = new Thickness(0, 2, 0, 0);
            border.BorderBrush = ThemeManager.Brush("Line");
            var relative = e.GetPosition(border); var size = border.Bounds.Size;
            if(relative.X < 0 || relative.Y < 0 || relative.X > size.Width || relative.Y > size.Height) continue;
            _targetGroup = id;
            _targetEdge = relative.X < size.Width * .23 ? DockEdge.Left : relative.X > size.Width * .77 ? DockEdge.Right : relative.Y < size.Height * .23 ? DockEdge.Top : relative.Y > size.Height * .77 ? DockEdge.Bottom : DockEdge.Center;
            border.BorderBrush = ThemeManager.Brush("Accent");
            border.BorderThickness = _targetEdge switch { DockEdge.Left => new(6,0,0,0), DockEdge.Right => new(0,0,6,0), DockEdge.Top => new(0,6,0,0), DockEdge.Bottom => new(0,0,0,6), _ => new(3) };
        }
        _window.SetTransientStatus(_targetGroup is not null ? "Dock " + _targetEdge.ToString().ToLowerInvariant() + " · release to move this session" : "Drag over a pane to dock · Escape cancels");
    }
    private void DragRelease(object? sender, PointerReleasedEventArgs e)
    {
        if(!_dragging) { _dragDocument = null; return; }
        var document = _dragDocument; var target = _targetGroup; var edge = _targetEdge;
        CancelDrag(); e.Handled = true;
        if(document is {} id && target is {} group) _window.Run(() => { _shell.Move(id, group, edge); return Task.CompletedTask; });
        else Rebuild();
    }
    public void CancelDrag()
    {
        _dragging = false; _dragDocument = null; _targetGroup = null; var pointer = _capturedPointer; _capturedPointer = null; pointer?.Capture(null);
        foreach(var border in _groups.Values) { border.BorderThickness = new Thickness(0,2,0,0); border.BorderBrush = ThemeManager.Brush("Line"); }
    }
    public void FocusNext(int delta)
    {
        var groups = Layout.Groups(_shell.Active.Root).ToArray(); var index = Array.FindIndex(groups, g => g.Id == _shell.ActiveGroupId); var next = groups[(index + delta + groups.Length) % groups.Length];
        if(next.Active is {} id) _shell.Select(id);
    }
    public void ResizeActive(SplitAxis axis, double delta)
    {
        DockSplit? Find(DockNode n)
        {
            if(n is not DockSplit split) return null;
            bool Inside(DockNode child) => Layout.Groups(child).Any(g => g.Id == _shell.ActiveGroupId);
            return (Inside(split.First) ? Find(split.First) : Find(split.Second)) ?? (split.Axis == axis ? split : null);
        }
        var split = Find(_shell.Active.Root); if(split is null) return; _shell.Apply(Layout.Resize(_shell.Active, split.Id, split.Ratio + delta), false);
    }
    public void DetachAll() { foreach(var host in _terminalHosts) host.Content = null; _terminalHosts.Clear(); }
}
