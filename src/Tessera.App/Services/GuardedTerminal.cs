using Avalonia.Controls;
using Avalonia.Input;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Avalonia.Rendering;
using RoyalTerminal.Avalonia.Services;
using RoyalTerminal.Terminal;
using RoyalTerminal.Terminal.Services;
using RoyalTerminal.Terminal.Transport.Ssh;

namespace Tessera.Services;

/// <summary>Enforces input policy before encoding, including handled-event fallback dispatch.</summary>
public sealed class GuardedTerminalInputAdapter : ITerminalInputAdapter
{
    private readonly DefaultTerminalInputAdapter _inner = new();
    public bool Locked { get; set; }
    public bool HandleKeyDown(KeyEventArgs e, ITerminalSessionService service, IVtProcessor? processor)
    {
        if(Locked) { e.Handled = true; return true; }
        return _inner.HandleKeyDown(e, service, processor);
    }
    public bool HandleKeyUp(KeyEventArgs e, ITerminalSessionService service)
    {
        if(Locked) { e.Handled = true; return true; }
        return _inner.HandleKeyUp(e, service);
    }
    public bool HandleTextInput(TextInputEventArgs e, ITerminalSessionService service)
    {
        if(Locked) { e.Handled = true; return true; }
        return _inner.HandleTextInput(e, service);
    }
}

/// <summary>Checks paste both before clipboard access and after any asynchronous confirmation.</summary>
public sealed class GuardedTerminalSelectionService(GuardedTerminalInputAdapter input) : ITerminalSelectionService
{
    private readonly DefaultTerminalSelectionService _inner = new();
    public Task CopySelectionAsync(Control owner, ITerminalSessionService service, TerminalScreen? screen, SkiaTerminalRenderer? renderer)
        => _inner.CopySelectionAsync(owner, service, screen, renderer);
    public Task PasteAsync(Control owner, Action<string> sendInput)
        => input.Locked ? Task.CompletedTask : _inner.PasteAsync(owner, text => { if(!input.Locked) sendInput(text); });
    public Task PasteAsync(Control owner, Action<string> sendInput, TerminalPasteRequest request)
        => input.Locked ? Task.CompletedTask : _inner.PasteAsync(owner, text => { if(!input.Locked) sendInput(text); }, request);
    public void ClearSelection(TerminalScreen? screen, SkiaTerminalRenderer? renderer, TerminalPresenter? presenter)
        => _inner.ClearSelection(screen, renderer, presenter);
}

public sealed class GuardedTerminal : TerminalControl
{
    private readonly GuardedTerminalInputAdapter _input;
    public bool InputLocked { get => _input.Locked; set => _input.Locked = value; }
    protected override Type StyleKeyOverride => typeof(TerminalControl);
    public GuardedTerminal(ISshCredentialProvider credentials) : this(credentials, new GuardedTerminalInputAdapter()) { }
    private GuardedTerminal(ISshCredentialProvider credentials, GuardedTerminalInputAdapter input) : base(
        new TerminalSessionService(), input,
        new GuardedTerminalSelectionService(input), new DefaultTerminalScrollService(),
        new DefaultVtProcessorFactory([new GhosttyVtProcessorProvider()]), new DefaultPtyFactory(),
        credentials, new KnownHostsSshHostKeyValidator(), transportFactory: null)
    {
        _input = input;
    }
}
