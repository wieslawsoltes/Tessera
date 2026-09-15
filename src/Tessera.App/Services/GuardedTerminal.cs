using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Tessera.Core;
using RoyalTerminal.Terminal.Transport.Pty;
using RoyalTerminal.Terminal.Transport.Pipe;
using RoyalTerminal.Terminal.Transport.Raw;
using RoyalTerminal.Terminal.Transport.Telnet;
using RoyalTerminal.Terminal.Transport.Serial;
using RoyalTerminal.Terminal.Transport.Ssh.SshNet;
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
    public bool BackspaceSendsControlH { get; set; }
    public bool HandleKeyDown(KeyEventArgs e, ITerminalSessionService service, IVtProcessor? processor)
    {
        if(Locked) { e.Handled = true; return true; }
        if (BackspaceSendsControlH && e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None) { service.SendInput("\b"); e.Handled = true; return true; }
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
    private SkiaTerminalRenderer? _metricsRenderer;
    private float _baseHeight;
    private double _lineHeight = 1;
    public SshConnectionContext SecurityContext { get; }
    public bool InputLocked { get => _input.Locked; set => _input.Locked = value; }
    public bool BackspaceSendsControlH { get => _input.BackspaceSendsControlH; set => _input.BackspaceSendsControlH = value; }
    public double LineHeight
    {
        get => _lineHeight;
        set
        {
            if (!double.IsFinite(value) || value is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(value));
            _lineHeight = value; ApplyCellMetrics(); InvalidateMeasure(); InvalidateArrange(); InvalidateTerminal();
        }
    }
    protected override Type StyleKeyOverride => typeof(TerminalControl);
    public GuardedTerminal(ProfileRepository profiles) : this(profiles, new GuardedTerminalInputAdapter(), new SshConnectionContext(profiles)) { }
    private GuardedTerminal(ProfileRepository profiles, GuardedTerminalInputAdapter input, SshConnectionContext security) : base(
        new TerminalSessionService(), input,
        new GuardedTerminalSelectionService(input), new DefaultTerminalScrollService(),
        new DefaultVtProcessorFactory([new GhosttyVtProcessorProvider()]), new TesseraPtyFactory(),
        security, security, new CompositeTerminalTransportFactory([
            new PtyTerminalTransportProvider(new TesseraPtyFactory()), new PipeTerminalTransportProvider(),
            new RawTcpTerminalTransportProvider(), new TelnetTerminalTransportProvider(), new SerialTerminalTransportProvider(),
            new SshNetTerminalTransportProvider(security, security, [security])]))
    {
        _input = input; SecurityContext = security;
        Avalonia.Automation.AutomationProperties.SetName(this, "Terminal output and input");
    }
    private void ApplyCellMetrics()
    {
        if (Renderer is not {} renderer || _lineHeight < 1) return;
        if (!ReferenceEquals(_metricsRenderer, renderer)) { _metricsRenderer = renderer; _baseHeight = renderer.CellHeight; }
        renderer.SetCellSize(renderer.CellWidth, (float)(_baseHeight * _lineHeight));
        if (ScrollData is {} scroll)
        {
            scroll.CellHeight = renderer.CellHeight;
            scroll.Viewport = Math.Max(1, Rows) * renderer.CellHeight;
        }
    }
    protected override Size MeasureOverride(Size availableSize) { ApplyCellMetrics(); return base.MeasureOverride(availableSize); }
    protected override Size ArrangeOverride(Size finalSize) { ApplyCellMetrics(); return base.ArrangeOverride(finalSize); }
    public void ApplyCursor(string style, bool blink)
    {
        int shape = style switch { "Underline" => 3, "Bar" => 5, _ => 1 };
        if (!blink) shape++;
        WriteOutput(System.Text.Encoding.UTF8.GetBytes($"\x1b[{shape} q"));
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new TerminalAutomationPeer(this);
    private sealed class TerminalAutomationPeer(GuardedTerminal owner) : ControlAutomationPeer(owner), IValueProvider
    {
        protected override string GetClassNameCore() => "TesseraTerminal";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
        public bool IsReadOnly => true;
        public string Value
        {
            get
            {
                if (owner.TryExportSnapshot(TerminalSnapshotExportFormat.PlainText, new TerminalSnapshotExportOptions(true, true), out var text))
                    return text.Length > 65536 ? text[^65536..] : text;
                return string.Empty;
            }
        }
        public void SetValue(string? value) => throw new InvalidOperationException("Use the terminal keyboard or explicit command insertion; accessibility replacement must not execute commands.");
    }
}
