using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Tessera.Services;
using Path = Avalonia.Controls.Shapes.Path;

namespace Tessera.Views;
public static class Ui
{
    private static readonly Dictionary<string, string> Icons = new()
    {
        ["terminal"] = "M3 4 L8 8 L3 12 M10 13 L15 13", ["add"] = "M8 2 L8 14 M2 8 L14 8", ["close"] = "M4 4 L12 12 M12 4 L4 12",
        ["layout"] = "M2 2 L14 2 L14 14 L2 14 Z M7 2 L7 14 M7 8 L14 8", ["server"] = "M2 2 L14 2 L14 6 L2 6 Z M2 10 L14 10 L14 14 L2 14 Z M4 4 L6 4 M4 12 L6 12",
        ["folder"] = "M2 4 L6 4 L8 6 L14 6 L14 14 L2 14 Z", ["code"] = "M5 4 L1 8 L5 12 M11 4 L15 8 L11 12 M9 2 L7 14",
        ["record"] = "M8 2 A6 6 0 1 1 7.99 2 M8 6 A2 2 0 1 1 7.99 6", ["search"] = "M7 2 A5 5 0 1 1 6.99 2 M11 11 L15 15",
        ["settings"] = "M6 1 L10 1 L11 4 L14 5 L14 10 L11 12 L10 15 L6 15 L5 12 L2 10 L2 5 L5 4 Z M8 5 A3 3 0 1 1 7.99 5",
        ["help"] = "M8 1 A7 7 0 1 1 7.99 1 M5.5 5 A2.5 2.5 0 1 1 8 8 L8 10 M8 12 L8 12.5", ["sun"] = "M8 4 A4 4 0 1 1 7.99 4 M8 0 L8 2 M8 14 L8 16 M0 8 L2 8 M14 8 L16 8 M2 2 L3 3 M13 13 L14 14 M2 14 L3 13 M13 3 L14 2",
        ["right"] = "M2 8 L14 8 M9 3 L14 8 L9 13", ["check"] = "M2 8 L6 12 L14 4", ["split"] = "M2 2 L14 2 L14 14 L2 14 Z M8 2 L8 14",
        ["rows"] = "M2 2 L14 2 L14 14 L2 14 Z M2 8 L14 8", ["focus"] = "M1 6 L1 1 L6 1 M10 1 L15 1 L15 6 M15 10 L15 15 L10 15 M6 15 L1 15 L1 10",
        ["save"] = "M4 1 L12 1 L12 15 L8 12 L4 15 Z", ["history"] = "M8 2 A6 6 0 1 1 2 8 M1 2 L1 6 L5 6 M8 4 L8 8 L11 10",
        ["lock"] = "M4 7 L4 4 A4 4 0 0 1 12 4 L12 7 M2 7 L14 7 L14 15 L2 15 Z M8 10 L8 12", ["broadcast"] = "M5 4 Q1 8 5 12 M2 1 Q-4 8 2 15 M11 4 Q15 8 11 12 M14 1 Q20 8 14 15 M8 7 L8 10",
        ["play"] = "M4 2 L14 8 L4 14 Z", ["pause"] = "M5 2 L5 14 M11 2 L11 14", ["stop"] = "M3 3 L13 3 L13 13 L3 13 Z", ["more"] = "M2 8 L3 8 M7 8 L8 8 M12 8 L13 8",
        ["file"] = "M3 1 L9 1 L13 5 L13 15 L3 15 Z M9 1 L9 5 L13 5", ["note"] = "M3 1 L13 1 L13 15 L3 15 Z M5 5 L11 5 M5 8 L11 8 M5 11 L9 11", ["branch"] = "M4 3 A2 2 0 1 1 3.99 3 M4 7 L4 12 A2 2 0 1 1 3.99 12 M4 10 Q12 10 12 5 M12 1 A2 2 0 1 1 11.99 1"
    };
    public static Control Icon(string name, double size = 16) => new Path { Data = Geometry.Parse(Icons.GetValueOrDefault(name, Icons["terminal"])), Stroke = ThemeManager.Brush("Muted"), StrokeThickness = 1.4, Width = size, Height = size, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
    public static TextBlock Text(string text, double size = 12, string? color = null) => new() { Text = text, FontSize = size, Foreground = ThemeManager.Brush(color ?? "Text"), VerticalAlignment = VerticalAlignment.Center };
    public static Button Button(string label, string? icon, Action action, string? style = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (icon is not null) row.Children.Add(Icon(icon));
        if (label.Length > 0) row.Children.Add(Text(label));
        var button = new Button { Content = row }; if (style is not null) button.Classes.Add(style);
        AutomationProperties.SetName(button, label.Length > 0 ? label : icon ?? "Action");
        if(style == "primary")
        {
            foreach(var text in row.Children.OfType<TextBlock>()) { text.Foreground = ThemeManager.Brush("AccentInk"); text.FontSize = 11; text.FontWeight = FontWeight.SemiBold; }
            foreach(var path in row.Children.OfType<Path>()) path.Stroke = ThemeManager.Brush("AccentInk");
        }
        button.Click += (_, _) => action(); return button;
    }
    public static Button IconButton(string icon, string tooltip, Action action)
    {
        var b = Button("", icon, action, "icon"); ToolTip.SetTip(b, tooltip); AutomationProperties.SetName(b, tooltip); return b;
    }
    public static Border Card(Control child, Thickness? padding = null) => new() { Child = child, Padding = padding ?? new Thickness(16), BorderBrush = ThemeManager.Brush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Background = ThemeManager.Brush("Surface") };
    public static StackPanel Stack(params Control[] children) { var s = new StackPanel { Spacing = 12 }; foreach (var c in children) s.Children.Add(c); return s; }
    public static Grid Row(string columns, params Control[] children) { var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(columns) }; for (int i = 0; i < children.Length; i++) { Grid.SetColumn(children[i], i); grid.Children.Add(children[i]); } return grid; }
    public static Control Field(string label, Control input) => Stack(Text(label, 11, "Muted"), input);
    public static TextBox Input(string? text = null, string? watermark = null) => new() { Text = text, PlaceholderText = watermark, HorizontalAlignment = HorizontalAlignment.Stretch };
}
