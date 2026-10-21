using System.Diagnostics;
using System.Windows;
using AV.Playout.Core;
using AV.Playout.Interop;
using AV.Playout.Windows;

namespace AV.Playout;

/// <summary>
/// Composition root. Builds the engine and both windows and wires them together.
/// </summary>
public partial class App : Application
{
    // Program canvas resolution. Set this to the LED processor's input raster
    // (e.g. 1920x1080, or 3840x256 for a ribbon board). Moves to show config in Phase 6.
    private const int CanvasWidth = 1920;
    private const int CanvasHeight = 1080;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The wall must stay up: never let a UI exception take the process down mid-game.
        DispatcherUnhandledException += (_, args) =>
        {
            Trace.TraceError(args.Exception.ToString());
            args.Handled = true;
        };

        // Keep the displays and the machine awake for the whole show.
        NativeMethods.SetThreadExecutionState(
            NativeMethods.ES_CONTINUOUS | NativeMethods.ES_DISPLAY_REQUIRED | NativeMethods.ES_SYSTEM_REQUIRED);

        var engine = new PlayoutEngine(CanvasWidth, CanvasHeight);
        var output = new OutputWindow(engine);
        var control = new ControlWindow(engine, output);

        // The first Window constructed becomes MainWindow automatically; that was the
        // output. Make the operator window the one whose closing ends the app.
        MainWindow = control;
        control.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);
        base.OnExit(e);
    }
}
