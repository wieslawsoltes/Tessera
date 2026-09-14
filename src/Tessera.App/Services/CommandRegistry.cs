using System.Windows.Input;
using Avalonia.Input;

namespace Tessera.Services;

public sealed class AppCommand(string id, string title, string category, string icon, string gesture, Func<Task> action, Action<Exception> onError, Func<bool>? canExecute = null) : ICommand
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Category { get; } = category;
    public string Icon { get; } = icon;
    public string DefaultGesture { get; } = gesture;
    public string Gesture { get; set; } = gesture;
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public async void Execute(object? parameter)
    {
        if(!CanExecute(parameter)) return;
        try { await action(); }
        catch(OperationCanceledException) { }
        catch(Exception ex) { onError(ex); }
    }
    public Task ExecuteAsync() => CanExecute(null) ? action() : Task.CompletedTask;
    public event EventHandler? CanExecuteChanged;
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Validated one- or two-stroke shortcuts. Invalid imported bindings never crash input dispatch.</summary>
public sealed class CommandRegistry
{
    private readonly List<AppCommand> _commands = [];
    private readonly Dictionary<string, KeyGesture[]> _cache = new(StringComparer.Ordinal);
    private List<(AppCommand Command, KeyGesture Second)>? _pending;
    private DateTimeOffset _pendingAt;
    public IReadOnlyList<AppCommand> All => _commands;
    public IReadOnlyList<string> BindingWarnings { get; private set; } = [];
    public AppCommand this[string id] => _commands.Single(c => c.Id == id);
    public void Add(AppCommand command)
    {
        if(_commands.Any(c => c.Id == command.Id)) throw new InvalidOperationException("Duplicate command id.");
        _commands.Add(command);
    }
    public void ApplyBindings(Dictionary<string, string> bindings)
    {
        var warnings = new List<string>();
        foreach(var command in _commands)
        {
            var candidate = bindings.GetValueOrDefault(command.Id, command.DefaultGesture);
            if(!TryParse(candidate, out _))
            {
                warnings.Add($"Invalid shortcut for {command.Title}; using its default.");
                candidate = command.DefaultGesture;
            }
            command.Gesture = candidate;
        }
        BindingWarnings = warnings;
        _pending = null;
    }
    public string? ValidateBinding(string id, string gesture)
    {
        if(gesture.Length == 0) return null;
        if(!TryParse(gesture, out var parts)) return "Use one or two complete gestures, such as Ctrl+Shift+P or Ctrl+K,Ctrl+S.";
        if(parts.Any(g => g.KeyModifiers == KeyModifiers.Control && g.Key is Key.C or Key.D or Key.Z))
            return "This shortcut belongs to the shell. Use an additional modifier.";
        foreach(var command in _commands.Where(c => c.Id != id && c.Gesture.Length > 0))
        {
            if(!TryParse(command.Gesture, out var other)) continue;
            if(parts.Length == other.Length && parts.Zip(other).All(pair => Same(pair.First, pair.Second)))
                return $"Already assigned to {command.Title}.";
            if(parts.Length != other.Length && Same(parts[0], other[0]))
                return $"The first stroke conflicts with {command.Title}. Unbind or change that command first.";
        }
        return null;
    }
    public bool Handle(KeyEventArgs e)
    {
        if(e.Handled) return false;
        if(e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return false;
        if(_pending is not null)
        {
            var candidates = _pending; _pending = null;
            if(DateTimeOffset.UtcNow - _pendingAt <= TimeSpan.FromSeconds(1.5))
            {
                var match = candidates.FirstOrDefault(c => c.Second.Matches(e) && c.Command.CanExecute(null));
                if(match.Command is not null) { e.Handled = true; match.Command.Execute(null); return true; }
            }
        }
        var matches = _commands.Where(c => c.CanExecute(null) && c.Gesture.Length > 0)
            .Select(c => (Command: c, Parts: ParseCached(c.Gesture)))
            .Where(c => c.Parts.Length > 0 && c.Parts[0].Matches(e)).ToArray();
        if(matches.Length == 0) return false;
        var single = matches.FirstOrDefault(c => c.Parts.Length == 1);
        if(single.Command is not null) { e.Handled = true; single.Command.Execute(null); return true; }
        _pending = matches.Select(c => (c.Command, c.Parts[1])).ToList();
        _pendingAt = DateTimeOffset.UtcNow; e.Handled = true; return true;
    }
    private KeyGesture[] ParseCached(string value)
    {
        if(_cache.TryGetValue(value, out var result)) return result;
        TryParse(value, out result);
        if(_cache.Count > 256) _cache.Clear();
        _cache[value] = result; return result;
    }
    private static bool Same(KeyGesture left, KeyGesture right) => left.Key == right.Key && left.KeyModifiers == right.KeyModifiers;
    private static bool TryParse(string? value, out KeyGesture[] parts)
    {
        parts = [];
        if(value is null || value.Length > 128) return false;
        if(value.Length == 0) return true;
        var strokes = value.Split(',', StringSplitOptions.TrimEntries);
        if(strokes.Length is < 1 or > 2 || strokes.Any(string.IsNullOrWhiteSpace)) return false;
        try { parts = strokes.Select(KeyGesture.Parse).ToArray(); return true; }
        catch(Exception ex) when(ex is ArgumentException or FormatException or InvalidOperationException or NotSupportedException) { return false; }
    }
}
