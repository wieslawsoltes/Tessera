using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Terminal.Theming;

namespace Tessera.Services;

public static class ThemeManager
{
    public static string Current { get; private set; } = "Obsidian";
    public static event Action? Changed;
    private static readonly string[] Keys = ["Bg", "Surface", "Raised", "Hover", "TerminalBg", "Line", "Subtle", "Text", "Muted", "Faint", "Accent", "AccentInk", "AccentWash", "Warning", "Danger", "Blue"];
    public static readonly IReadOnlyDictionary<string, string[]> Palettes = new Dictionary<string, string[]>
    {
        ["Obsidian"] = ["#101313", "#171b1b", "#1e2322", "#242b28", "#121616", "#2b3230", "#202624", "#e2e8e1", "#95a298", "#78867d", "#b8e4bf", "#1c3422", "#273c2c", "#e7b776", "#ef9a91", "#9ebfdd"],
        ["Porcelain"] = ["#eef0ea", "#f6f7f2", "#ffffff", "#e5eadf", "#fafbf6", "#d6dcd0", "#e8ece3", "#27352c", "#59695d", "#647369", "#316944", "#ffffff", "#dcebdc", "#875714", "#ac453d", "#3f678b"],
        ["Blueprint"] = ["#111723", "#171f2e", "#1d283b", "#27344a", "#121b29", "#2b3a51", "#202c3e", "#e0e8f4", "#a1b0c6", "#8598b2", "#b7cbff", "#17284c", "#2a3b5a", "#ebc186", "#eca5a5", "#b2caff"]
    };
    public static Color Color(string key) => Avalonia.Media.Color.Parse(Palettes[Current][Array.IndexOf(Keys, key)]);
    public static IBrush Brush(string key) => new SolidColorBrush(Color(key));
    public static void Apply(string name)
    {
        if (!Palettes.TryGetValue(name, out var palette)) throw new ArgumentException("Unknown theme.", nameof(name));
        Current = name;
        if (Application.Current is {} app)
        {
            app.RequestedThemeVariant = name == "Porcelain" ? ThemeVariant.Light : ThemeVariant.Dark;
            for (var i = 0; i < Keys.Length; i++) app.Resources[Keys[i]] = new SolidColorBrush(Avalonia.Media.Color.Parse(palette[i]));
            app.Resources["SystemAccentColor"] = Color("Accent");
        }
        Changed?.Invoke();
    }
    public static void ApplyTerminal(TerminalControl terminal)
    {
        var foreground = Color("Text").ToUInt32(); var background = Color("TerminalBg").ToUInt32(); var accent = Color("Accent").ToUInt32();
        uint[] ansi = [background, Color("Danger").ToUInt32(), accent, Color("Warning").ToUInt32(), Color("Blue").ToUInt32(), 0xffc6a9d9, 0xff89c7c0, foreground, Color("Muted").ToUInt32(), Color("Danger").ToUInt32(), accent, Color("Warning").ToUInt32(), Color("Blue").ToUInt32(), 0xffd6bbed, 0xffa1d9d2, foreground];
        terminal.ApplyTheme(TerminalTheme.FromBase16(ansi, foreground, background, accent));
        terminal.DefaultBackground = Color("TerminalBg"); terminal.DefaultForeground = Color("Text"); terminal.BackgroundOpacityEnabled = false;
    }
}
