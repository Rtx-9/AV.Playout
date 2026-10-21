using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AV.Playout.Core;
using AV.Playout.Interop;
using AV.Playout.Services;

namespace AV.Playout.Windows;

/// <summary>
/// The program output. In fullscreen mode it is borderless, topmost, never takes
/// focus, and is pinned in physical pixels to one display (the LED processor input).
/// In rehearsal mode it is an ordinary window, for working on a single-screen laptop.
/// </summary>
public partial class OutputWindow : Window
{
    private const double RehearsalWidth = 960;
    private const double RehearsalHeight = 540;

    private readonly IntPtr _hwnd;
    private readonly nint _baseExStyle;
    private DisplayInfo? _display;   // null = rehearsal (windowed) mode
    private bool _allowClose;

    public OutputWindow(PlayoutEngine engine)
    {
        InitializeComponent();
        DataContext = engine;

        // Create the HWND now, without showing it, so it can be styled and moved to the
        // LED display *before* the first frame is presented. No flash on the wrong screen.
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        _baseExStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
    }

    /// <summary>The display the output is pinned to, or null in rehearsal mode.</summary>
    public DisplayInfo? Display => _display;

    /// <summary>Borderless fullscreen on <paramref name="display"/>.</summary>
    public void ShowOn(DisplayInfo display)
    {
        _display = display;

        // WPF properties first: changing WindowStyle rewrites the native styles.
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Cursor = Cursors.None;

        // Never steal focus from the operator, and stay out of Alt+Tab.
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE,
            _baseExStyle | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);

        PinToDisplay();
        if (!IsVisible)
        {
            Show();
            PinToDisplay();   // in case WPF applied its own placement while showing
        }
    }

    /// <summary>A normal, movable window for rehearsing without an LED output.</summary>
    public void ShowWindowed()
    {
        _display = null;

        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, _baseExStyle);
        Topmost = false;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        Cursor = null;
        Width = RehearsalWidth;
        Height = RehearsalHeight;

        if (!IsVisible)
            Show();
    }

    /// <summary>Closes for real. Only the control window calls this, on shutdown.</summary>
    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    private void PinToDisplay()
    {
        if (_display is not { } d)
            return;

        // Physical pixels, bypassing WPF's DIP conversion, so a 1920x1080 panel gets
        // exactly 1920x1080 regardless of either monitor's scaling setting.
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST,
            d.X, d.Y, d.Width, d.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        // Moving between monitors with different scaling makes Windows apply a
        // "suggested" rect that rescales the window. Re-pin once that has settled.
        if (_display is not null)
            Dispatcher.InvokeAsync(PinToDisplay, DispatcherPriority.Normal);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            // Alt+F4 must never take the wall down. In rehearsal mode the close
            // button just hides the window.
            e.Cancel = true;
            if (_display is null)
                Hide();
        }
        base.OnClosing(e);
    }
}
