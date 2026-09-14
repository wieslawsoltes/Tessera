from pathlib import Path

def edit(path, old, new, count=1):
    p=Path(path); s=p.read_text()
    if s.count(old)!=count: raise RuntimeError(f'{path}: expected {count}, found {s.count(old)}: {old[:90]}')
    p.write_text(s.replace(old,new))

edit('src/Tessera.App/Views/MainWindow.Dialogs.cs', 'var details=Ui.Stack(Ui.Text(entry!.Title,12)', 'if(entry is null) return new Border();\n            var details=Ui.Stack(Ui.Text(entry.Title,12)')
edit('src/Tessera.App/Styles/Shell.axaml', 'Property="FontFamily" Value="Inter"', 'Property="FontFamily" Value="fonts:Inter#Inter"')
edit('src/Tessera.App/Services/SessionRuntime.cs', '    public Guid Id { get; }', '    public string DocumentTitle { get; set; } = "Terminal";\n    public Guid Id { get; }')
edit('src/Tessera.App/Services/SessionRuntime.cs', 'var session = new SessionRuntime(document.Id, profiles.Get(document.ProfileId), profiles, design);', 'var session = new SessionRuntime(document.Id, profiles.Get(document.ProfileId), profiles, design) { DocumentTitle = document.Title };')
old='        var text = Profile.Id == "staging"'
new=r'''        var text = DocumentTitle == "dev server"
            ? "\u001b[90mDesign fixture · local watcher\u001b[0m\r\n\r\n  → Local:  \u001b[34mhttp://localhost:5173/\u001b[0m\r\n  → Mode:   visual acceptance fixture\r\n\r\n  \u001b[32m✓\u001b[0m app mounted\r\n  \u001b[32m✓\u001b[0m workspace shell ready\r\n  \u001b[32m✓\u001b[0m terminal surfaces attached\r\n\r\n\u001b[34m~/Developer/tessera\u001b[32m ❯ \u001b[0m"
            : Profile.Id == "staging"'''
edit('src/Tessera.App/Services/SessionRuntime.cs',old,new)
print('Palette recycling, embedded UI font, and per-pane visual fixtures corrected.')
