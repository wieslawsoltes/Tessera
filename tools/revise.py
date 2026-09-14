from pathlib import Path
p = Path('src/Tessera.App/Views/MainWindow.Dialogs.cs')
s = p.read_text()
assert s.count('e.PropertyName == nameof(state.SelectedProfile)') == 1
p.write_text(s.replace('e.PropertyName == nameof(state.SelectedProfile)', 'e.Property.Name == nameof(state.SelectedProfile)'))
print('Corrected Avalonia direct-property notification contract.')
