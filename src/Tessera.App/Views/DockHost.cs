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
    private PointerPressedEventArgs? _dragTrigger;
    private readonly Guid? _windowId;
    private Guid? _targetGroup;
    private DockEdge _targetEdge;
    private int _targetIndex = int.MaxValue;
    private readonly Dictionary<Guid,List<(Guid Document,Control Tab)>> _tabBounds = [];
    public DockHost(MainWindow window, ShellController shell, Guid? windowId = null)
    {
        _window = window; _shell = shell; _windowId = windowId; ClipToBounds = true;
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, NativeDragOver);
        DragDrop.AddDragLeaveHandler(this, (_, _) => ClearDropHint());
        DragDrop.AddDropHandler(this, NativeDrop);
        AddHandler(PointerMovedEvent, DragMove, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, DragRelease, RoutingStrategies.Tunnel);

    }
    public void Rebuild()
    {
        if(_window.DockDragActive) return;
        // Explicit detach makes reparenting legal and preserves terminal/transport identity.
        foreach(var host in _terminalHosts) host.Content = null;
        _terminalHosts.Clear(); _groups.Clear(); _footers.Clear(); _splitGrids.Clear(); _tabBounds.Clear(); Children.Clear();
        DockNode root = Layout.WindowRoot(_shell.Active, _windowId);
        if(_shell.FocusMode && _shell.ActiveDocumentId is {} id)
        {
            var group = Layout.Groups(root).FirstOrDefault(g => g.Tabs.Contains(id)); if(group is not null) root = group;
        }
        Children.Add(BuildNode(root));
        Dispatcher.UIThread.Post(() => { if(_shell.ActiveSession is {} session && !_window.IsOverlayOpen && _terminalHosts.Any(h => ReferenceEquals(h.Content, session.Terminal))) session.Terminal.Focus(); }, DispatcherPriority.Loaded);
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
        var tabBounds = new List<(Guid Document,Control Tab)>(); _tabBounds[group.Id] = tabBounds;
        foreach(var id in group.Tabs)
        {
            var doc = _shell.Active.Documents[id];
            var b = Ui.Button((doc.Pinned ? "• " : "") + doc.Title, "terminal", () => { if(!_dragging) _shell.Select(id); }, "tab");
            if(group.Active == id) b.Classes.Add("activeTab");
            ToolTip.SetTip(b, doc.Title + " · drag to a pane edge or center");
            b.PointerPressed += (_, e) => { if(e.GetCurrentPoint(b).Properties.IsLeftButtonPressed) { _dragDocument = id; _dragTrigger = e; _dragOrigin = e.GetPosition(this); } };
            b.ContextMenu = _window.TabContextMenu(id);
            var tab = Ui.Row("Auto,Auto", b, Ui.IconButton("close", "Close " + doc.Title, () => _window.Run(() => _window.CloseTabAsync(id))));
            tabs.Children.Add(tab); tabBounds.Add((id,tab));
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
    private async void DragMove(object? sender, PointerEventArgs e)
    {
        if (_dragDocument is not {} document || _dragTrigger is not {} trigger || _dragging) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { CancelDrag(); return; }
        var p = e.GetPosition(this);
        if (Math.Sqrt(Math.Pow(p.X - _dragOrigin.X, 2) + Math.Pow(p.Y - _dragOrigin.Y, 2)) < 7) return;
        _dragging = true; _window.DockDragActive = true; e.Handled = true;
        string? token = null;
        try
        {
            token = DockDragBroker.Begin(_shell, document);
            var data = new DataTransfer(); data.Add(DataTransferItem.Create(DockDragBroker.Format, token));
            await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Move);
        }
        catch (Exception ex) { _shell.Report("Docking cancelled: " + ex.Message); }
        finally
        {
            if (token is not null) DockDragBroker.End(token);
            _window.DockDragActive = false; CancelDrag(); _window.RefreshDockWindows();
        }
    }
    private void NativeDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None; ClearDropHint();
        if (!DockDragBroker.TryResolve(e.DataTransfer.TryGetValue(DockDragBroker.Format), _shell, out var draggedDocument)) return;
        foreach (var (id, border) in _groups)
        {
            var p = e.GetPosition(border); var size = border.Bounds.Size;
            if (!new Rect(size).Contains(p)) continue;
            _targetGroup = id; _targetIndex = int.MaxValue;
            if (p.Y < 36 && _tabBounds.TryGetValue(id,out var tabs))
            {
                _targetIndex = 0;
                foreach (var item in tabs)
                {
                    if (item.Document == draggedDocument) continue;
                    if (e.GetPosition(item.Tab).X < item.Tab.Bounds.Width / 2) break;
                    _targetIndex++;
                }
                _targetEdge = DockEdge.Center;
            }
            else _targetEdge = p.X < size.Width * .23 ? DockEdge.Left : p.X > size.Width * .77 ? DockEdge.Right :
                p.Y < size.Height * .23 ? DockEdge.Top : p.Y > size.Height * .77 ? DockEdge.Bottom : DockEdge.Center;
            border.BorderBrush = ThemeManager.Brush("Accent");
            border.BorderThickness = _targetEdge switch { DockEdge.Left => new(6,0,0,0), DockEdge.Right => new(0,0,6,0), DockEdge.Top => new(0,6,0,0), DockEdge.Bottom => new(0,0,0,6), _ => new(3) };
            e.DragEffects = DragDropEffects.Move; e.Handled = true; return;
        }
    }
    private void NativeDrop(object? sender, DragEventArgs e)
    {
        NativeDragOver(sender, e);
        var group = _targetGroup; var edge = _targetEdge; var index = _targetIndex; ClearDropHint();
        e.DragEffects = group is {} id && DockDragBroker.Drop(e.DataTransfer.TryGetValue(DockDragBroker.Format), _shell, id, edge, index)
            ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }
    private void DragRelease(object? sender, PointerReleasedEventArgs e) { if (!_dragging) CancelDrag(); }
    private void ClearDropHint()
    {
        _targetGroup = null;
        foreach (var (id, border) in _groups)
        {
            border.BorderThickness = new Thickness(0,2,0,0);
            border.BorderBrush = ThemeManager.Brush(id == _shell.ActiveGroupId ? "Accent" : "Line");
        }
    }
    public void CancelDrag() { _dragging = false; _dragDocument = null; _dragTrigger = null; ClearDropHint(); }
    public void FocusNext(int delta)
    {
        var groups = Layout.Groups(_shell.Active).ToArray(); var index = Array.FindIndex(groups, g => g.Id == _shell.ActiveGroupId); var next = groups[(index + delta + groups.Length) % groups.Length];
        if(next.Active is {} id) _shell.Select(id);
    }
    public bool FocusDirection(DockEdge direction)
    {
        if (!_groups.TryGetValue(_shell.ActiveGroupId,out var source)) return false;
        var origin=source.TranslatePoint(default,this) ?? default;
        var center=origin+new Vector(source.Bounds.Width/2,source.Bounds.Height/2);
        var candidate=_groups.Where(p=>p.Key!=_shell.ActiveGroupId).Select(p=>
        {
            var point=p.Value.TranslatePoint(default,this) ?? default;
            var other=point+new Vector(p.Value.Bounds.Width/2,p.Value.Bounds.Height/2);
            double dx=other.X-center.X,dy=other.Y-center.Y;
            double forward=direction switch {DockEdge.Left=>-dx,DockEdge.Right=>dx,DockEdge.Top=>-dy,_=>dy};
            double perpendicular=direction is DockEdge.Left or DockEdge.Right?Math.Abs(dy):Math.Abs(dx);
            return (p.Key,Forward:forward,Score:forward+perpendicular*2);
        }).Where(p=>p.Forward>1).OrderBy(p=>p.Score).ThenBy(p=>p.Key).FirstOrDefault();
        if(candidate.Key==Guid.Empty)return false;
        if(Layout.Groups(_shell.Active).Single(g=>g.Id==candidate.Key).Active is {} id)_shell.Select(id);
        return true;
    }
    public void ResizeActive(SplitAxis axis, double delta)
    {
        DockSplit? Find(DockNode n)
        {
            if(n is not DockSplit split) return null;
            bool Inside(DockNode child) => Layout.Groups(child).Any(g => g.Id == _shell.ActiveGroupId);
            return (Inside(split.First) ? Find(split.First) : Find(split.Second)) ?? (split.Axis == axis ? split : null);
        }
        var activeRoot = _shell.Active.Floating.FirstOrDefault(f => Layout.Groups(f.Root).Any(g => g.Id == _shell.ActiveGroupId))?.Root ?? _shell.Active.Root;
        var split = Find(activeRoot); if(split is null) return; var first = Layout.Groups(split.First).Any(g => g.Id == _shell.ActiveGroupId); _shell.Apply(Layout.Resize(_shell.Active, split.Id, split.Ratio + (first ? delta : -delta)), false);
    }
    public void DetachAll() { foreach(var host in _terminalHosts) host.Content = null; _terminalHosts.Clear(); }
}
