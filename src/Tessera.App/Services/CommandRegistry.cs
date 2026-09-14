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
    public async void Execute(object? parameter) { if(!CanExecute(parameter)) return; try { await action(); } catch(Exception ex) { onError(ex); } }
    public Task ExecuteAsync() => CanExecute(null) ? action() : Task.CompletedTask;
    public event EventHandler? CanExecuteChanged;
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class CommandRegistry
{
    private readonly List<AppCommand> _commands = [];
    private string? _prefix;
    private DateTimeOffset _prefixTime;
    public IReadOnlyList<AppCommand> All => _commands;
    public AppCommand this[string id] => _commands.Single(c => c.Id == id);
    public void Add(AppCommand command) { if(_commands.Any(c => c.Id == command.Id)) throw new InvalidOperationException("Duplicate command id."); _commands.Add(command); }
    public void ApplyBindings(Dictionary<string, string> bindings)
    {
        foreach (var command in _commands) command.Gesture = bindings.GetValueOrDefault(command.Id, command.DefaultGesture);
    }
    public string? ValidateBinding(string id, string gesture)
    {
        try { foreach (var part in gesture.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) KeyGesture.Parse(part); }
        catch { return "Use a gesture such as Ctrl+Shift+P or Ctrl+K,Ctrl+S."; }
        if (gesture.Split(',').Length > 2) return "Use at most two strokes.";
        if(_commands.Any(c => c.Id != id && c.Gesture.Equals(gesture, StringComparison.OrdinalIgnoreCase) && gesture.Length > 0)) return "This shortcut is already assigned.";
        if(gesture is "Ctrl+C" or "Ctrl+D" or "Ctrl+Z") return "This shortcut belongs to the shell. Use an additional modifier.";
        return null;
    }
    public bool Handle(KeyEventArgs e)
    {
        if(e.Handled) return false;
        if(_prefix is not null && DateTimeOffset.UtcNow - _prefixTime > TimeSpan.FromSeconds(1.5)) _prefix = null;
        foreach(var command in _commands.Where(c => c.Gesture.Length > 0))
        {
            var parts = command.Gesture.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if(parts.Length == 2 && _prefix == parts[0] && KeyGesture.Parse(parts[1]).Matches(e)) { _prefix = null; e.Handled = true; command.Execute(null); return true; }
            if(_prefix is null && KeyGesture.Parse(parts[0]).Matches(e))
            {
                e.Handled = true;
                if(parts.Length == 2) { _prefix = parts[0]; _prefixTime = DateTimeOffset.UtcNow; } else command.Execute(null);
                return true;
            }
        }
        _prefix = null; return false;
    }
}
