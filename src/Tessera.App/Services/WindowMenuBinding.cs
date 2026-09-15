using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Tessera.Services;

/// <summary>
/// Owns a single native menu tree for the entire lifetime of one top-level window.
/// Avalonia.Native's exporter keeps the NativeMenu identity passed on first export;
/// replacing it on a shell render causes IAvnMenu.Update to reject the new instance.
/// Item state and gestures are updated in place, including while a submenu is open.
/// </summary>
internal sealed class WindowMenuBinding : IDisposable
{
    private static readonly string[] Categories = ["File", "Edit", "View", "Session", "Tools", "Help"];
    private readonly List<(AppCommand Command, MenuItem? Managed, NativeMenuItem Native)> _items = [];
    private readonly List<NativeMenu> _menus = [];
    private bool _disposed;

    public WindowMenuBinding(Window owner, CommandRegistry commands, Menu? managedMenu = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        var native = new NativeMenu();
        _menus.Add(native);
        var managed = new List<MenuItem>();
        foreach (string category in Categories)
        {
            var submenu = new NativeMenu();
            var children = new List<MenuItem>();
            foreach (var command in commands.All.Where(c => c.Category == category))
            {
                var nativeItem = new NativeMenuItem(command.Title) { Command = command };
                MenuItem? managedItem = managedMenu is null ? null
                    : new MenuItem { Header = command.Title, Command = command };
                submenu.Items.Add(nativeItem);
                if (managedItem is not null) children.Add(managedItem);
                _items.Add((command, managedItem, nativeItem));
            }
            native.Items.Add(new NativeMenuItem(category) { Menu = submenu });
            _menus.Add(submenu);
            if (managedMenu is not null) managed.Add(new MenuItem { Header = category, ItemsSource = children });
        }

        foreach (var menu in _menus) menu.NeedsUpdate += OnNeedsUpdate;
        if (managedMenu is not null) managedMenu.ItemsSource = managed;
        Refresh();
        // Assign once. Do not clear/reassign this property during redraw or disposal:
        // a null assignment also creates a different root inside the macOS exporter.
        NativeMenu.SetMenu(owner, native);
    }

    public void Refresh()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed) return;
        foreach (var (command, managed, native) in _items)
        {
            KeyGesture? gesture = SingleGesture(command.Gesture);
            if (!Equals(native.Gesture, gesture)) native.Gesture = gesture;
            if (managed is not null && !Equals(managed.InputGesture, gesture)) managed.InputGesture = gesture;
        }
    }

    private void OnNeedsUpdate(object? sender, EventArgs e)
    {
        if (_disposed) return;
        Refresh();
        foreach (var (command, _, _) in _items) command.Refresh();
    }

    private static KeyGesture? SingleGesture(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Contains(',')) return null;
        try { return KeyGesture.Parse(text); }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException or NotSupportedException)
        { return null; }
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        foreach (var menu in _menus) menu.NeedsUpdate -= OnNeedsUpdate;
    }
}
