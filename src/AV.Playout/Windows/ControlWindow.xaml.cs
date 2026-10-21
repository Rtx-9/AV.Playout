using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AV.Playout.Core;
using AV.Playout.Core.Commands;
using AV.Playout.Core.Layers;
using AV.Playout.Graphics;
using AV.Playout.Media;
using AV.Playout.Services;
using AV.Playout.Triggers;
using AV.Playout.Venue;
using Microsoft.Win32;

namespace AV.Playout.Windows;

/// <summary>
/// Operator dashboard. Kept thin on purpose: buttons, clicks and keys build
/// PlayoutCommands and hand them to the engine.
/// </summary>
public partial class ControlWindow : Window
{
    private static readonly Color[] Swatches =
    [
        Colors.Black,
        Colors.White,
        Color.FromRgb(0xD7, 0x26, 0x2E),   // home red
        Color.FromRgb(0x2A, 0x5F, 0xE0),   // away blue
        Color.FromRgb(0x12, 0xA1, 0x50),   // green
        Color.FromRgb(0xF5, 0xB7, 0x00),   // amber
        Color.FromRgb(0x80, 0x80, 0x80),   // 50% grey: panel uniformity check
    ];

    private const string MediaFilter =
        "Media|*.mp4;*.m4v;*.mov;*.mkv;*.webm;*.avi;*.wmv;*.mxf;*.mpg;*.mpeg;*.ts;*.m2ts;*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif|All files|*.*";

    private readonly PlayoutEngine _engine;
    private readonly OutputWindow _output;
    private readonly ShowFile _show;
    private readonly AsRunLog _asRun;
    private readonly DispatcherTimer _saveTimer;
    private VenueWindow? _venue;
    private GlobalKeyboardHook? _hook;
    private ClipSlot? _assigningHotkeyFor;
    private bool _showLoaded;

    public ControlWindow(PlayoutEngine engine, OutputWindow output, ShowFile show, AsRunLog asRun)
    {
        _engine = engine;
        _output = output;
        _show = show;
        _asRun = asRun;

        InitializeComponent();
        DataContext = engine;
        AsRunPanel.DataContext = asRun;
        SwatchList.ItemsSource = Swatches;

        GlobalKeysToggle.IsChecked = show.GlobalKeys;
        RehearsalToggle.IsChecked = show.Rehearsal;

        _engine.CommandExecuted += OnCommandExecuted;
        _engine.StatusMessage += OnStatusMessage;
        _output.IsVisibleChanged += OnOutputVisibleChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        var tier = RenderCapability.Tier >> 16;
        RenderTierText.Text = tier == 2 ? "GPU rendering" : $"⚠ Render tier {tier}: check GPU driver";

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Tick += (_, _) => SaveShow();
        WatchForChanges();

        // The operator dashboard lives on the primary display; the output gets the rest.
        WindowStartupLocation = WindowStartupLocation.Manual;
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left;
        Top = workArea.Top;
        WindowState = WindowState.Maximized;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _hook = new GlobalKeyboardHook { KeyPressed = OnKeyPressed };
        }
        catch (InvalidOperationException ex)
        {
            SetStatus($"Global keyboard hook unavailable ({ex.Message}); hotkeys work only while this window is focused.");
        }

        RefreshDisplays();
        if (RehearsalToggle.IsChecked != true && DisplayCombo.SelectedItem is DisplayInfo { IsPrimary: false })
            OutputToggle.IsChecked = true;   // a secondary display is present: go straight to the LED wall

        SetStatus("Loading show…");
        await ShowStore.ApplyAsync(_engine, _show, SetStatus);
        _showLoaded = true;
        SetStatus($"Ready. Show: {ShowStore.FilePath}");
    }

    // ---- Output routing ------------------------------------------------------------

    private void RefreshDisplays()
    {
        var previous = (DisplayCombo.SelectedItem as DisplayInfo)?.DeviceName ?? _show.OutputDevice;
        var displays = DisplayService.GetDisplays();

        if (displays.Count < 2)
            RehearsalToggle.IsChecked = true;

        // The display we were on (it may have changed resolution), then the first
        // non-primary display, which is almost always the LED processor.
        DisplayCombo.ItemsSource = displays;
        DisplayCombo.SelectedItem =
            displays.FirstOrDefault(d => d.DeviceName == previous)
            ?? displays.FirstOrDefault(d => !d.IsPrimary)
            ?? displays.FirstOrDefault();
    }

    private void ShowOutput()
    {
        if (RehearsalToggle.IsChecked == true || DisplayCombo.SelectedItem is not DisplayInfo display)
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

    private void OnOutputVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        OutputToggle.IsChecked = _output.IsVisible;

    private void DisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        MarkDirty();
        if (_output.IsVisible)
            ShowOutput();
    }

    private void RehearsalToggle_Click(object sender, RoutedEventArgs e)
    {
        MarkDirty();
        if (_output.IsVisible)
            ShowOutput();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // An LED processor was power-cycled or changed raster. Can arrive on a system thread.
        Dispatcher.InvokeAsync(() =>
        {
            RefreshDisplays();
            SetStatus("Display configuration changed: output re-pinned.");
        });
    }

    // ---- Keys -------------------------------------------------------------------------

    /// <summary>Global hook callback. Must return fast: decide, queue, return.</summary>
    private bool OnKeyPressed(Hotkey hotkey)
    {
        if (_assigningHotkeyFor is { } target)
        {
            if (hotkey.Key != Key.Escape)
                AssignHotkey(target, hotkey);
            EndHotkeyAssign();
            return true;
        }

        // Typing a team name in this window: the keys belong to the text box.
        if (IsActive && Keyboard.FocusedElement is TextBox)
            return false;
        if (GlobalKeysToggle.IsChecked != true && !IsActive)
            return false;

        if (_engine.FindSlot(hotkey) is not { } slot)
            return false;

        Dispatcher.BeginInvoke(() => _engine.Execute(new TriggerSlotCommand(slot.Layer, slot.Column, TriggerOrigin.Hotkey)), DispatcherPriority.Send);
        return true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Panic: pull the output. Works even when the output covers this window,
        // because the output never takes keyboard focus.
        if (e.Key == Key.F12 && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OutputToggle.IsChecked = false;
            e.Handled = true;
            return;
        }

        // Without the global hook, fall back to window-local hotkeys.
        if (_hook is null && !Hotkey.IsModifierKey(e.Key))
            e.Handled = OnKeyPressed(new Hotkey(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers));
    }

    private void GlobalKeysToggle_Click(object sender, RoutedEventArgs e) => MarkDirty();

    private void AssignHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.SelectedSlot is not { IsEmpty: false } slot)
            return;
        _assigningHotkeyFor = slot;
        AssignHotkeyButton.Content = "PRESS A KEY…";
        SetStatus("Press the key (or macro-pad button) for this clip. Esc cancels.");
    }

    private void AssignHotkey(ClipSlot slot, Hotkey hotkey)
    {
        // One key, one clip.
        foreach (var other in _engine.AllSlots.Where(s => s != slot && s.Hotkey == hotkey))
            other.Hotkey = null;
        slot.Hotkey = hotkey;
        SetStatus($"{hotkey} → {slot.Name}");
    }

    private void EndHotkeyAssign()
    {
        _assigningHotkeyFor = null;
        AssignHotkeyButton.Content = "ASSIGN";
    }

    private void ClearHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.SelectedSlot is { } slot)
            slot.Hotkey = null;
    }

    // ---- Clip grid --------------------------------------------------------------------

    private void Slot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ClipSlot slot)
            return;
        e.Handled = true;
        _engine.SelectedSlot = slot;

        if (slot.IsEmpty)
        {
            if (e.ClickCount == 2)
                BrowseInto(slot);
            return;
        }
        if (e.ClickCount == 1)
            _engine.Execute(new TriggerSlotCommand(slot.Layer, slot.Column));
    }

    private void Slot_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ClipSlot slot)
            return;
        e.Handled = true;
        _engine.SelectedSlot = slot;
        _engine.Execute(new CueSlotCommand(slot.Layer, slot.Column));
    }

    private void Slot_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Slot_Drop(object sender, DragEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ClipSlot slot && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            await LoadFilesAsync(slot, files);
    }

    /// <summary>Loads files into <paramref name="first"/> and the slots to its right.</summary>
    private async Task LoadFilesAsync(ClipSlot first, IEnumerable<string> files)
    {
        var layer = _engine.GetLayer(first.Layer);
        var column = first.Column;
        foreach (var file in files.Where(ClipLoader.IsSupportedFile))
        {
            if (column >= layer.Slots.Count)
            {
                SetStatus("Row full: some files were not loaded.");
                break;
            }
            await LoadIntoAsync(layer.Slots[column++], file);
        }
        _engine.SelectedSlot = first;
    }

    private async Task LoadIntoAsync(ClipSlot slot, string source)
    {
        try
        {
            SetStatus($"Loading {source}…");
            await ClipLoader.LoadAsync(slot, source);
            SetStatus($"Loaded {slot.Name} into {slot.Layer} {slot.ColumnNumber}.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not load {source}: {ex.Message}");
        }
    }

    private async void BrowseInto(ClipSlot slot)
    {
        var dialog = new OpenFileDialog { Title = "Load media", Multiselect = true, Filter = MediaFilter };
        if (dialog.ShowDialog(this) == true)
            await LoadFilesAsync(slot, dialog.FileNames);
    }

    private void Column_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is int number)
            _engine.Execute(new TriggerColumnCommand(number - 1));
    }

    private void ClearLayer_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Layer layer)
            _engine.Execute(new ClearLayerCommand(layer.Kind));
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Color color)
            _engine.Execute(new SetBackgroundCommand(color));
    }

    private void Take_Click(object sender, RoutedEventArgs e) => _engine.Execute(new TakeCommand());

    private void Blackout_Click(object sender, RoutedEventArgs e) => _engine.Execute(new BlackoutCommand());

    // ---- Clip properties --------------------------------------------------------------

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.SelectedSlot is { } slot)
            BrowseInto(slot);
    }

    private async void Camera_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.SelectedSlot is { } slot)
            await LoadIntoAsync(slot, "dshow://");
    }

    private async void SourceBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _engine.SelectedSlot is not { } slot || string.IsNullOrWhiteSpace(SourceBox.Text))
            return;
        e.Handled = true;
        await LoadIntoAsync(slot, SourceBox.Text.Trim().Trim('"'));
    }

    private void CueSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.SelectedSlot is { } slot)
            _engine.Execute(new CueSlotCommand(slot.Layer, slot.Column));
    }

    private void TriggerSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.SelectedSlot is { IsEmpty: false } slot)
            _engine.Execute(new TriggerSlotCommand(slot.Layer, slot.Column));
    }

    private void ClearSlot_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.SelectedSlot is { } slot)
            _engine.ClearSlot(slot);
    }

    // ---- Text graphics, rotation, as-run, venue ---------------------------------------

    private void Graphics_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string tag && Enum.TryParse(tag, out GraphicsAction action))
            _engine.Execute(new GraphicsCommand(action));
    }

    private void PromptPreset_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is string text)
            _engine.Execute(new GraphicsCommand(GraphicsAction.ShowPrompt, text));
    }

    private void Rotation_Click(object sender, RoutedEventArgs e) =>
        _engine.Execute(new RotationCommand(!_engine.RotationRunning));

    private void OpenAsRun_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AsRunLog.Directory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AsRunLog.Directory}\"") { UseShellExecute = true });
    }

    private void Venue_Click(object sender, RoutedEventArgs e)
    {
        if (_venue is null)
        {
            _venue = new VenueWindow(_engine) { Owner = this };
            _venue.Closed += (_, _) => _venue = null;
            _venue.Show();
        }
        else
        {
            _venue.Activate();
        }
    }

    // ---- Scoreboard -------------------------------------------------------------------

    private void Score_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string tag && Enum.TryParse(tag, out ScoreboardAction action))
            _engine.Execute(new ScoreboardCommand(action));
    }

    private void RemovePenalty_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Penalty penalty)
            _engine.Scoreboard.RemovePenalty(penalty);
    }

    // ---- Status and show persistence ----------------------------------------------

    private void OnCommandExecuted(object? sender, PlayoutCommand command) =>
        SetStatus($"{DateTime.Now:HH:mm:ss}   {Describe(command)}");

    private string Describe(PlayoutCommand command) => command switch
    {
        TriggerSlotCommand c => $"{c.Layer} {c.Column + 1}  ▸  {_engine.GetLayer(c.Layer).Slots[c.Column].Name}",
        TriggerColumnCommand c => $"Column {c.Column + 1}",
        CueSlotCommand c => $"Cued {_engine.GetLayer(c.Layer).Slots[c.Column].Name}",
        ClearLayerCommand c => $"Cleared {c.Layer}",
        ScoreboardCommand c => $"Scoreboard: {c.Action}",
        GraphicsCommand c => c.Text is null ? $"Graphics: {c.Action}" : $"Prompt: {c.Text}",
        RotationCommand c => c.Run ? "Rotation started" : "Rotation stopped",
        _ => command.GetType().Name.Replace("Command", ""),
    };

    private void OnStatusMessage(object? sender, string message) => SetStatus(message);

    private void SetStatus(string text) => StatusText.Text = text;

    private void WatchForChanges()
    {
        string[] slotProps =
        [
            nameof(ClipSlot.Name), nameof(ClipSlot.Source), nameof(ClipSlot.Kind), nameof(ClipSlot.Mode),
            nameof(ClipSlot.Hotkey), nameof(ClipSlot.InRotation), nameof(ClipSlot.DwellSeconds),
        ];
        foreach (var slot in _engine.AllSlots)
            slot.PropertyChanged += (_, e) => { if (slotProps.Contains(e.PropertyName)) MarkDirty(); };
        foreach (var layer in _engine.LayersBottomUp)
            layer.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Layer.Opacity)) MarkDirty(); };
        _engine.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PlayoutEngine.CrossfadeSeconds)) MarkDirty(); };
        _engine.Scoreboard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Scoreboard.HomeName) or nameof(Scoreboard.AwayName)) MarkDirty();
        };
        _engine.TextGraphics.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TextGraphics.LowerThirdTitle) or nameof(TextGraphics.LowerThirdSubtitle)
                or nameof(TextGraphics.TickerText) or nameof(TextGraphics.TickerSpeed)
                or nameof(TextGraphics.PromptAutoHide) or nameof(TextGraphics.PromptSeconds))
                MarkDirty();
        };
        foreach (var screen in _engine.Screens)
            screen.PropertyChanged += (_, _) => MarkDirty();
    }

    private void MarkDirty()
    {
        // Until the show has finished loading, slots are half-filled: saving now would lose clips.
        if (!_showLoaded) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveShow()
    {
        _saveTimer.Stop();
        if (!_showLoaded) return;
        _show.OutputDevice = (DisplayCombo.SelectedItem as DisplayInfo)?.DeviceName;
        _show.Rehearsal = RehearsalToggle.IsChecked == true;
        _show.GlobalKeys = GlobalKeysToggle.IsChecked == true;
        try
        {
            ShowStore.Save(ShowStore.Capture(_engine, _show));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Could not save show: {ex.Message}");
        }
    }

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
        SaveShow();
        _asRun.Flush();
        _hook?.Dispose();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _engine.CommandExecuted -= OnCommandExecuted;
        _engine.StatusMessage -= OnStatusMessage;
        _output.ForceClose();
        base.OnClosed(e);
    }
}
