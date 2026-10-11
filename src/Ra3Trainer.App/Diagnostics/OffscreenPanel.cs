using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ra3Trainer.App.ViewModels;

namespace Ra3Trainer.App.Diagnostics;

/// <summary>Renders the existing expanded floating view; no second UI or input route.</summary>
public sealed class OffscreenPanel : IDisposable
{
    public const int Width = 360, Height = 440, MaxDimension = 512;
    private readonly MainViewModel _model;
    private readonly FloatingWindow _window;
    private bool _disposed;
    public OffscreenPanel(MainViewModel model)
    {
        _model = model;
        _window = new FloatingWindow(model, offscreen: true);
    }
    public bool PanelMode => _model.PanelMode;
    /// <summary>
    /// The panel mirrors the real floating window's shape, using the very rule the desktop
    /// float uses (FloatingWindow.xaml.cs): 细条 200x19, collapsed 360x88, expanded 360x440.
    /// Rendering it at a fixed 360x440 stretched the hairline strip into a box.
    /// </summary>
    public int PanelWidth => (int)Math.Ceiling(_window.Width) is > 0 and <= MaxDimension ? (int)Math.Ceiling(_window.Width) : Width;
    public int PanelHeight => (int)Math.Ceiling(_window.Height) is > 0 and <= MaxDimension ? (int)Math.Ceiling(_window.Height) : Height;
    /// <summary>The arranged content root, i.e. the very tree the bitmap is rendered from.</summary>
    public FrameworkElement Root => (FrameworkElement)_window.Content;
    public int X => _model.OverlayPanelX;
    public int Y => _model.OverlayPanelY;
    public RenderTargetBitmap Render()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _window.Dispatcher.VerifyAccess();
        var content = (FrameworkElement)_window.Content;
        content.Measure(new Size(PanelWidth, PanelHeight));
        content.Arrange(new Rect(0, 0, PanelWidth, PanelHeight));
        content.UpdateLayout();
        // Render the visible content, never the transparent/offscreen Window itself.
        var bitmap = new RenderTargetBitmap(PanelWidth, PanelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        return bitmap;
    }
    public byte[] Pixels()
    {
        var pixels = new byte[PanelWidth * PanelHeight * 4];
        Render().CopyPixels(pixels, PanelWidth * 4, 0);
        return pixels;
    }
    /// <summary>
    /// Runs the same command the panel's own button would run if it were clicked on
    /// the desktop: hit test, walk up to the button, invoke it through its automation
    /// peer. No second UI, no second copy of the command logic, no synthetic input.
    /// </summary>
    public bool TryInvokeAt(int x, int y, out string hit)
    {
        hit = "";
        ObjectDisposedException.ThrowIf(_disposed, this);
        _window.Dispatcher.VerifyAccess();
        if (x < 0 || y < 0 || x >= PanelWidth || y >= PanelHeight) return false;
        var content = (FrameworkElement)_window.Content;
        // Arrange exactly the way Render() does. A bare UpdateLayout() re-runs layout
        // against the host window's own size, which shifts every control away from the
        // coordinates the bitmap was rendered at, so hit tests miss what the player sees.
        content.Measure(new Size(PanelWidth, PanelHeight));
        content.Arrange(new Rect(0, 0, PanelWidth, PanelHeight));
        content.UpdateLayout();
        var visual = VisualTreeHelper.HitTest(content, new Point(x, y))?.VisualHit;
        for (var node = (DependencyObject?)visual; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not ButtonBase button) continue;
            if (!button.IsEnabled) { hit = "disabled"; return false; }
            hit = string.IsNullOrEmpty(button.Name) ? (button.Content?.ToString() ?? "button") : button.Name;
            hit = button.GetType().Name + ":" + hit;
            // Each control family goes through the peer that owns its own semantics,
            // which is exactly what a real click ends up doing.
            switch (button)
            {
                // ToggleButton exposes its action through IToggleProvider only, so go
                // through the interface rather than the peer class.
                case ToggleButton toggle:
                    if (new ToggleButtonAutomationPeer(toggle) is IToggleProvider provider) provider.Toggle();
                    else { hit = "unsupported"; return false; }
                    return true;
                case Button plain:
                    if (new ButtonAutomationPeer(plain) is IInvokeProvider invoker) invoker.Invoke();
                    else { hit = "unsupported"; return false; }
                    return true;
                default:
                    hit = "unsupported";
                    return false;
            }
        }
        // 没点到按钮：**整行**都该能点。以前只有那个小「执行」按钮可点，
        // 点行本体全部 hit=none（用户日志里 35 次点击就是这么白点的）。
        for (var node = (DependencyObject?)visual; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not FrameworkElement element) continue;
            if (element.DataContext is PanelNavRow row)
            {
                _model.ActivatePanelRow(row);
                hit = "row:" + row.Action;
                return true;
            }
        }
        hit = "";
        return false;
    }

    public void Dispose() { if (!_disposed) { _disposed = true; _window.Close(); } }

    /// <summary>The shared row cursor the panel draws its highlight from.</summary>
    public PanelNavigator Navigator => _model.Navigator;

    /// <summary>
    /// M4: applies one panel key the game side swallowed. The mapping is pure and
    /// total, so a code outside the numpad set maps to None and does nothing at all;
    /// outside panel mode the model refuses to act.
    /// </summary>
    public PanelKeyCommand DispatchPanelKey(int virtualKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _window.Dispatcher.VerifyAccess();
        var command = PanelKeyMap.Map(virtualKey);
        if (command != PanelKeyCommand.None) _model.ApplyPanelKey(command);
        return command;
    }

    /// <summary>
    /// 面板内滚轮：原生侧本来就吞掉了面板矩形里的滚轮、delta 也一直在读，只是以前没人用。
    /// 行板比视口高时（面板只有 360×440）用它翻，比"再来一个光标"强。
    /// 正 delta = 滚轮往自己这边滚 = 内容往上翻。
    /// </summary>
    public void DispatchPanelWheel(int delta)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _window.Dispatcher.VerifyAccess();
        if (delta == 0) return;
        _window.ScrollBoard(-delta);
    }
}
