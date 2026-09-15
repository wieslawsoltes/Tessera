"""Materialize the reviewed ConPTY compatibility source; not a build-time dependency.

The sealed upstream WindowsPty cannot expose its STARTUPINFO flags through an
adapter. Keep the pinned MIT-licensed implementation, changing only namespace,
visibility, and the explicit standard-handle launch policy. Never patch package
binaries or mutate process-global standard handles.
"""
from __future__ import annotations
import hashlib
from pathlib import Path
import urllib.request

REVISION = 'b740171f3d0ff6e97a1fcc1f58cc311f5dd4507f'
FILES = {
    'src/RoyalTerminal.Terminal.Pty.Windows/Terminal/WindowsPty.cs': '8ff0efe6c50100787466d5102af139a4ce8556a869814176499840e673831194',
    'src/RoyalTerminal.Terminal.Pty.Windows/Terminal/WindowsPtyEnvironment.cs': '210a566b9fc567b774c1a38cdfa0ab6d927b26982e12fd2ed73cec8521c8c5cf',
    'LICENSE': '62da6fc2512a74613433ee264aa5118bcf8d840b7f6b736d34b7dfae16361b42',
}


def transform(name: str, content: bytes) -> bytes:
    if name == 'LICENSE':
        return content
    text = content.decode('utf-8')
    text = text.replace('namespace RoyalTerminal.Terminal;',
                        'using RoyalTerminal.Terminal;\n\nnamespace Tessera.Services.Compatibility;')
    if name.endswith('/WindowsPty.cs'):
        text = text.replace('public sealed class WindowsPty : IPty', 'internal sealed class WindowsPty : IPty')
        before = '        startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();'
        assert text.count(before) == 1
        text = text.replace(before, before + '''
        // Without STARTF_USESTDHANDLES Windows may duplicate redirected parent
        // streams into a ConPTY child even when bInheritHandles is false.
        // Explicit NULL standard handles make the child bind to its own PTY.
        // https://github.com/microsoft/terminal/discussions/15814
        startupInfo.StartupInfo.dwFlags |= 0x00000100; // STARTF_USESTDHANDLES
''')
    return text.encode('utf-8')


def main() -> None:
    root = Path(__file__).resolve().parent.parent
    target = root / 'src/Tessera.App/Services/Compatibility'
    reviewed = {}
    # Verify every upstream file before changing any local source.
    for name, expected in FILES.items():
        url = f'https://raw.githubusercontent.com/royalapplications/RoyalTerminal/{REVISION}/{name}'
        with urllib.request.urlopen(url, timeout=30) as response:
            content = response.read(128 * 1024)
        if hashlib.sha256(content).hexdigest() != expected:
            raise SystemExit(f'Upstream integrity mismatch: {name}')
        reviewed[name] = transform(name, content)
    target.mkdir(parents=True, exist_ok=True)
    for name, content in reviewed.items():
        filename = 'LICENSE-RoyalTerminal' if name == 'LICENSE' else Path(name).name
        (target / filename).write_bytes(content)
        print(filename + ': ' + hashlib.sha256(content).hexdigest())


if __name__ == '__main__':
    main()
