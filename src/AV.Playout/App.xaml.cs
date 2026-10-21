using System.Diagnostics;
using System.IO;
using System.Windows;
using AV.Playout.Core;
using AV.Playout.Interop;
using AV.Playout.Media;
using AV.Playout.Services;
using AV.Playout.Windows;

namespace AV.Playout;

/// <summary>
/// Composition root. Builds the engine and both windows and wires them together.
/// </summary>
public partial class App : Application
{
    // Program canvas resolution. Set this to the LED processor's input raster
    // (e.g. 1920x1080, or 3840x256 for a ribbon board).
    private const int CanvasWidth = 1920;
    private const int CanvasHeight = 1080;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The wall must stay up: never let a UI exception take the process down mid-game.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled exception (process terminating)", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
        Log.Info($"AV Playout starting ({string.Join(' ', e.Args)})");

        // Keep the displays and the machine awake for the whole show.
        NativeMethods.SetThreadExecutionState(
            NativeMethods.ES_CONTINUOUS | NativeMethods.ES_DISPLAY_REQUIRED | NativeMethods.ES_SYSTEM_REQUIRED);

        // Load libvlc and its ~300 plugins now, in the background, not on the first trigger.
        VlcRuntime.Warmup();

        var showArg = Array.IndexOf(e.Args, "--show");
        if (showArg >= 0 && showArg + 1 < e.Args.Length)
            ShowStore.FilePath = Path.GetFullPath(e.Args[showArg + 1]);
        var show = ShowStore.Load();
        var engine = new PlayoutEngine(CanvasWidth, CanvasHeight);
        var output = new OutputWindow(engine);
        var control = new ControlWindow(engine, output, show);

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
