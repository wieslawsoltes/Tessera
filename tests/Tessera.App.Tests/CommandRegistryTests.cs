using Avalonia.Input;
using Tessera.Services;
using Xunit;

namespace Tessera.NativeTests;

public sealed class CommandRegistryTests
{
    private static AppCommand Command(string id, string gesture, Action action, Func<bool>? can = null) => new(id, id, "Test", "", gesture, () => { action(); return Task.CompletedTask; }, ex => throw ex, can);
    [Fact]
    public void MalformedImportedBindingFallsBackWithoutCrashing()
    {
        var count = 0; var registry = new CommandRegistry(); registry.Add(Command("palette", "Ctrl+Shift+P", () => count++));
        registry.ApplyBindings(new() { ["palette"] = "THIS-IS-NOT-A-KEY" });
        Assert.NotEmpty(registry.BindingWarnings);
        Assert.True(registry.Handle(new KeyEventArgs { Key = Key.P, KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift }));
        Assert.Equal(1, count);
    }
    [Fact]
    public void DuplicateAndAmbiguousChordBindingsAreRejected()
    {
        var registry = new CommandRegistry(); registry.Add(Command("palette", "Ctrl+K", () => { })); registry.Add(Command("settings", "", () => { }));
        Assert.NotNull(registry.ValidateBinding("settings", "Ctrl+K"));
        Assert.NotNull(registry.ValidateBinding("settings", "Ctrl+K,Ctrl+S"));
        Assert.NotNull(registry.ValidateBinding("settings", "Ctrl+C"));
        Assert.Null(registry.ValidateBinding("settings", "Ctrl+Shift+S"));
    }
    [Fact]
    public void TwoStrokeCommandExecutesOnce()
    {
        var count = 0; var registry = new CommandRegistry(); registry.Add(Command("save", "Ctrl+K,Ctrl+S", () => count++));
        Assert.True(registry.Handle(new KeyEventArgs { Key = Key.K, KeyModifiers = KeyModifiers.Control }));
        Assert.Equal(0, count);
        Assert.True(registry.Handle(new KeyEventArgs { Key = Key.S, KeyModifiers = KeyModifiers.Control }));
        Assert.Equal(1, count);
    }
    [Fact]
    public void DisabledCommandDoesNotSwallowShellInput()
    {
        var registry = new CommandRegistry(); registry.Add(Command("disabled", "Alt+K", () => throw new Exception("Must not execute"), () => false));
        var e = new KeyEventArgs { Key = Key.K, KeyModifiers = KeyModifiers.Alt };
        Assert.False(registry.Handle(e)); Assert.False(e.Handled);
    }
}
