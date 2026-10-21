using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AV.Playout.Core;
using AV.Playout.Core.Commands;
using AV.Playout.Core.Layers;
using AV.Playout.Media;
using AV.Playout.Services;
using Microsoft.Win32;

namespace AV.Playout.Windows;

/// <summary>
/// Operator dashboard. Kept thin on purpose: every button builds a PlayoutCommand and
/// hands it to the engine, exactly as the keypad and MIDI triggers will in Phase 3.
/// </summary>
public partial class ControlWindow : Window
{
    private static readonly Color[] Swatches =
    [
        Colors.Black,
        Colors.White,
        Color.FromRgb(0xE1, 0x1D, 0x2A),   // home red
        Color.FromRgb(0x1F, 0x4E, 0xD8),   // away blue
        Color.FromRgb(0x12, 0xA1, 0x50),   // pitch green
        Color.FromRgb(0xF5, 0xB7, 0x00),   // amber
        Color.FromRgb(0x80, 0x80, 0x80),   // 50% grey (panel uniformity check)
    ];

    private readonly PlayoutEngine _engine;
    private readonly OutputWindow _output;
    private readonly ObservableCollection<StillItem> _stills = [];

    public ControlWindow(PlayoutEngine engine, OutputWindow output)
    {
        _engine = engine;
        _output = output;

        InitializeComponent();

        ProgramMonitor.DataContext = engine;
        SwatchList.ItemsSource = Swatches;
        StillsList.ItemsSource = _stills;

        _engine.CommandExecuted += OnCommandExecuted;
        _output.IsVisibleChanged += (_, _) => OutputToggle.IsChecked = _output.IsVisible;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        var tier = RenderCapability.Tier >> 16;
        RenderTierText.Text = tier == 2 ? "GPU rendering (tier 2)" : $"⚠ Render tier {tier}: check GPU driver";

        RefreshDisplays();
    }

    // ---- Output routing -------------------------------------------------------

    private void RefreshDisplays()
    {
        var previous = DisplayCombo.SelectedItem as DisplayInfo;
        var displays = DisplayService.GetDisplays();

        if (previous is null)
            RehearsalCheck.IsChecked = displays.Count < 2;

        // Prefer the display we were on (it may have changed resolution), then the
        // first non-primary display, which is almost always the LED processor.
        DisplayCombo.ItemsSource = displays;
        DisplayCombo.SelectedItem =
            displays.FirstOrDefault(d => d.DeviceName == previous?.DeviceName)
            ?? displays.FirstOrDefault(d => !d.IsPrimary)
            ?? displays.FirstOrDefault();
    }

    private void ShowOutput()
    {
        if (RehearsalCheck.IsChecked == true || DisplayCombo.SelectedItem is not DisplayInfo display)
        {
            _output.ShowWindowed();
            SetStatus("Output in rehearsal window.");
            return;
        }

        // A topmost, focus-less fullscreen window on the operator's own monitor would
        // bury this window. Make that a deliberate choice.
        var ownMonitor = DisplayService.MonitorOf(new WindowInteropHelper(this).Handle);
        if (display.HMonitor == ownMonitor)
        {
            var answer = MessageBox.Show(this,
                $"{display.DeviceName} is the screen this control window is on. The output will cover it.\n\n" +
                "Press Ctrl+F12 to pull the output off again.\n\nContinue?",
                "AV Playout", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK)
            {
                OutputToggle.IsChecked = _output.IsVisible;
                return;
            }
        }

        _output.ShowOn(display);
        SetStatus($"Output live on {display}.");
    }

    private void HideOutput()
    {
        _output.Hide();
        SetStatus("Output hidden.");
    }

    // Checked/Unchecked rather than Click, so keyboard, automation and the
    // IsVisibleChanged sync all go through one path. Both are no-ops when the
    // output is already in the requested state, which stops the sync re-entering.
    private void OutputToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (!_output.IsVisible)
            ShowOutput();
    }

    private void OutputToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_output.IsVisible)
            HideOutput();
    }

    private void DisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_output.IsVisible)
            ShowOutput();
    }

    private void RehearsalCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_output.IsVisible)
            ShowOutput();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Raised when an LED processor is power-cycled or changes raster. Can arrive
        // on a system thread.
        Dispatcher.InvokeAsync(() =>
        {
            RefreshDisplays();
            SetStatus("Display configuration changed: output re-pinned.");
        });
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Panic: pull the output. Works even when the output covers this window,
        // because the output never takes keyboard focus.
        if (e.Key == Key.F12 && Keyboard.Modifiers == ModifierKeys.Control)
        {
            HideOutput();
            e.Handled = true;
        }
    }

    // ---- Content ----------------------------------------------------------------

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Color color)
            _engine.Execute(new SetBackgroundCommand(color));
    }

    private void Still_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is StillItem still)
            _engine.Execute(new ShowStillCommand(still));
    }

    private void ClearMedia_Click(object sender, RoutedEventArgs e) =>
        _engine.Execute(new ClearLayerCommand(LayerKind.Media));

    private void Blackout_Click(object sender, RoutedEventArgs e) =>
        _engine.Execute(new BlackoutCommand());

    private async void AddStills_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add stills",
            Multiselect = true,
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            await AddStillsAsync(dialog.FileNames);
    }

    private void Bin_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Bin_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            await AddStillsAsync(files);
    }

    private async Task AddStillsAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                SetStatus($"Loading {Path.GetFileName(path)}…");
                _stills.Add(await StillItem.LoadAsync(path));
                SetStatus($"Loaded {Path.GetFileName(path)}.");
            }
            catch (Exception ex)
            {
                SetStatus($"Could not load {Path.GetFileName(path)}: {ex.Message}");
            }
        }
    }

    private void OnCommandExecuted(object? sender, PlayoutCommand command)
    {
        foreach (var still in _stills)
            still.IsOnAir = ReferenceEquals(_engine.Media.Source, still.Image);

        SetStatus($"{DateTime.Now:HH:mm:ss}  {command}");
    }

    private void SetStatus(string text) => StatusText.Text = text;

    // ---- Shutdown -------------------------------------------------------------------

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_output.IsVisible)
        {
            var answer = MessageBox.Show(this, "The program output is live. Quit and drop the feed?",
                "AV Playout", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
                e.Cancel = true;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _engine.CommandExecuted -= OnCommandExecuted;
        _output.ForceClose();
        base.OnClosed(e);
    }
}
