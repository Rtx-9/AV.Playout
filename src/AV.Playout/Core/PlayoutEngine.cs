using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using AV.Playout.Core.Commands;
using AV.Playout.Core.Layers;
using AV.Playout.Graphics;
using AV.Playout.Media;
using AV.Playout.Triggers;
using AV.Playout.Venue;

namespace AV.Playout.Core;

/// <summary>
/// Single source of truth for what is on the wall. The output window and the
/// program monitor both bind to this object; nothing else changes program state.
/// </summary>
public sealed class PlayoutEngine : ObservableObject
{
    public const int Columns = 8;

    private readonly Dispatcher _dispatcher;
    private Brush _background = Brushes.Black;
    private double _masterOpacity = 1.0;
    private double _crossfadeSeconds = 0.5;
    private ClipSlot? _selectedSlot;
    private ClipSlot? _cuedSlot;
    private IFrameSource? _cue;
    private ImageSource? _previewImage;
    private Takeover? _takeover;
    private readonly DispatcherTimer _tick;
    private bool _rotationRunning;
    private ClipSlot? _rotationSlot;
    private readonly Stopwatch _rotationClock = new();
    private string? _rotationStatus;

    private sealed record Takeover(ClipSlot Slot, Layer Layer, ClipSlot? Restore);

    /// <summary>Stills in rotation with no dwell time set stay up this long.</summary>
    private static readonly TimeSpan DefaultStillDwell = TimeSpan.FromSeconds(15);

    public PlayoutEngine(int canvasWidth, int canvasHeight)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;

        Media = new Layer(LayerKind.Media, "MEDIA", Color.FromRgb(0x3F, 0x8C, 0xFF), Columns);
        Live = new Layer(LayerKind.Live, "LIVE", Color.FromRgb(0x34, 0xD2, 0x7B), Columns);
        Graphics = new Layer(LayerKind.Graphics, "GRAPHICS", Color.FromRgb(0xB0, 0x6C, 0xFF), Columns);
        LayersBottomUp = [Media, Live, Graphics];
        LayersTopDown = [Graphics, Live, Media];

        foreach (var layer in LayersBottomUp)
            layer.ClipEnded += OnClipEnded;

        TextGraphics = new TextGraphics(canvasWidth);
        Screens = new ObservableCollection<ScreenDefinition>(ScreenDefinition.Defaults(canvasWidth, canvasHeight));

        // 10 Hz: countdowns and the sponsor rotation clock. Cheap property reads only.
        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();
    }

    /// <summary>Raised on the UI thread after a command has changed program state.</summary>
    public event EventHandler<PlayoutCommand>? CommandExecuted;

    /// <summary>Operator-facing messages (load failures, takeover state, ...).</summary>
    public event EventHandler<string>? StatusMessage;

    public int CanvasWidth { get; }
    public int CanvasHeight { get; }

    public Layer Media { get; }
    public Layer Live { get; }
    public Layer Graphics { get; }
    public IReadOnlyList<Layer> LayersBottomUp { get; }
    public IReadOnlyList<Layer> LayersTopDown { get; }
    public IReadOnlyList<int> ColumnNumbers { get; } = Enumerable.Range(1, Columns).ToList();

    public Scoreboard Scoreboard { get; } = new();
    public TextGraphics TextGraphics { get; }

    /// <summary>Physical screens in the venue and the part of the canvas each one shows.</summary>
    public ObservableCollection<ScreenDefinition> Screens { get; }

    public bool RotationRunning { get => _rotationRunning; private set => SetField(ref _rotationRunning, value); }

    /// <summary>"▶ Sponsor A · next: Sponsor B  -0:12"</summary>
    public string? RotationStatus { get => _rotationStatus; private set => SetField(ref _rotationStatus, value); }

    public Brush Background { get => _background; private set => SetField(ref _background, value); }

    public double MasterOpacity
    {
        get => _masterOpacity;
        set
        {
            if (SetField(ref _masterOpacity, Math.Clamp(value, 0, 1)))
                OnPropertyChanged(nameof(MasterPercent));
        }
    }

    public string MasterPercent => $"{_masterOpacity * 100:0}%";

    public double CrossfadeSeconds
    {
        get => _crossfadeSeconds;
        set
        {
            if (SetField(ref _crossfadeSeconds, Math.Round(Math.Clamp(value, 0, 5), 1)))
                OnPropertyChanged(nameof(CrossfadeText));
        }
    }

    public string CrossfadeText => _crossfadeSeconds == 0 ? "CUT" : $"{_crossfadeSeconds:0.0}s";

    private TimeSpan Fade => TimeSpan.FromSeconds(_crossfadeSeconds);

    /// <summary>The slot shown in the clip properties panel.</summary>
    public ClipSlot? SelectedSlot
    {
        get => _selectedSlot;
        set
        {
            if (_selectedSlot == value) return;
            if (_selectedSlot is not null) _selectedSlot.IsSelected = false;
            _selectedSlot = value;
            if (_selectedSlot is not null) _selectedSlot.IsSelected = true;
            OnPropertyChanged();
        }
    }

    public ClipSlot? CuedSlot { get => _cuedSlot; private set => SetField(ref _cuedSlot, value); }
    public ImageSource? PreviewImage { get => _previewImage; private set => SetField(ref _previewImage, value); }

    public bool IsTakeoverActive => _takeover is not null;

    public Layer GetLayer(LayerKind kind) => kind switch
    {
        LayerKind.Media => Media,
        LayerKind.Live => Live,
        LayerKind.Graphics => Graphics,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public IEnumerable<ClipSlot> AllSlots => LayersBottomUp.SelectMany(l => l.Slots);

    public ClipSlot? FindSlot(Hotkey hotkey) => AllSlots.FirstOrDefault(s => !s.IsEmpty && s.Hotkey == hotkey);

    /// <summary>
    /// Applies a command. Safe to call from any thread: keyboard hooks and MIDI
    /// callbacks are marshalled to the UI thread at Send priority so they jump ahead
    /// of layout and input work.
    /// </summary>
    public void Execute(PlayoutCommand command)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.InvokeAsync(() => Execute(command), DispatcherPriority.Send);
            return;
        }

        switch (command)
        {
            case TriggerSlotCommand c:
                var slot = GetLayer(c.Layer).Slots[c.Column];
                if (slot.IsEmpty) ClearLayer(GetLayer(c.Layer));
                else Trigger(slot, c.Origin);
                break;

            case TriggerColumnCommand c:
                foreach (var layer in LayersBottomUp)
                    if (!layer.Slots[c.Column].IsEmpty)
                        Trigger(layer.Slots[c.Column], TriggerOrigin.Column);
                break;

            case CueSlotCommand c:
                Cue(GetLayer(c.Layer).Slots[c.Column]);
                break;

            case TakeCommand:
                if (CuedSlot is { } cued) Trigger(cued, TriggerOrigin.Take);
                break;

            case ClearLayerCommand c:
                ClearLayer(GetLayer(c.Layer));
                break;

            case ToggleBypassCommand c:
                GetLayer(c.Layer).Bypass ^= true;
                break;

            case SetBackgroundCommand c:
                var brush = new SolidColorBrush(c.Color);
                brush.Freeze();
                Background = brush;
                break;

            case BlackoutCommand:
                StopRotation();
                TextGraphics.HideAll();
                EndTakeover(restore: false);
                foreach (var layer in LayersBottomUp)
                    layer.Clear(Fade);
                Scoreboard.IsVisible = false;
                Background = Brushes.Black;
                break;

            case ScoreboardCommand c:
                Scoreboard.Apply(c.Action);
                break;

            case GraphicsCommand c:
                ApplyGraphics(c);
                break;

            case RotationCommand { Run: true }:
                StartRotation();
                break;

            case RotationCommand:
                StopRotation();
                break;

            default:
                throw new NotSupportedException($"No handler for {command.GetType().Name}.");
        }

        CommandExecuted?.Invoke(this, command);
    }

    // ---- Triggering -----------------------------------------------------------------

    private void Trigger(ClipSlot slot, TriggerOrigin origin)
    {
        var layer = GetLayer(slot.Layer);

        if (slot.Mode == ClipMode.Takeover)
            BeginTakeover(slot, layer);
        else if (_takeover?.Layer == layer)
            EndTakeover(restore: false);   // operator overrode the takeover by hand

        // Rotation follows the operator: a rotation clip continues the rotation from
        // there; anything else on MEDIA means the operator has taken over.
        bool? loop = null;
        if (layer == Media && RotationRunning && slot.Mode != ClipMode.Takeover)
        {
            if (slot.InRotation)
            {
                _rotationSlot = slot;
                _rotationClock.Restart();
                loop = slot.DwellSeconds > 0;   // dwell 0: play the video once, then advance
            }
            else
            {
                StopRotation();
            }
        }

        var source = slot == CuedSlot ? TakeCue() : CreateSource(slot, cue: false, loop);
        if (source is null)
            return;

        _ = ShowAsync(layer, source, slot, origin);
    }

    private async Task ShowAsync(Layer layer, IFrameSource source, ClipSlot slot, TriggerOrigin origin)
    {
        if (await layer.ShowAsync(source, slot, Fade, origin) == ShowResult.Failed)
            Report($"Could not play \"{slot.Name}\" ({slot.Source}).");
    }

    private IFrameSource? CreateSource(ClipSlot slot, bool cue, bool? loop = null)
    {
        switch (slot.Kind)
        {
            case ClipKind.Still when slot.Still is not null:
                return new StillSource(slot.Still);

            case ClipKind.Still:
                Report($"\"{slot.Name}\" is still loading.");
                return null;

            case ClipKind.Video:
            case ClipKind.Live:
                return new VlcSource(slot.Source!, new VlcSourceOptions
                {
                    Loop = slot.Kind == ClipKind.Video && (loop ?? slot.Mode == ClipMode.Loop),
                    Audio = slot.Layer != LayerKind.Graphics,
                    PauseOnFirstFrame = cue && slot.Kind == ClipKind.Video,
                    Muted = cue,
                });

            default:
                return null;
        }
    }

    private void ClearLayer(Layer layer)
    {
        if (_takeover?.Layer == layer)
            EndTakeover(restore: false);
        layer.Clear(Fade);
    }

    private void OnClipEnded(object? sender, ClipSlot? slot)
    {
        if (RotationRunning && slot is not null && slot == _rotationSlot && _takeover is null)
            AdvanceRotation();
        else if (_takeover is { } t && t.Slot == slot)
            EndTakeover(restore: true);
        else if (slot?.Mode == ClipMode.Once && sender is Layer layer && layer.OnAirSlot == slot)
            layer.Clear(Fade);
    }

    // ---- Takeovers: e.g. "Home Goal" ------------------------------------------------

    private void BeginTakeover(ClipSlot slot, Layer layer)
    {
        // A second goal during a takeover keeps the original restore point.
        var restore = _takeover?.Restore ?? layer.OnAirSlot;
        if (restore?.Mode == ClipMode.Takeover)
            restore = null;
        _takeover = new Takeover(slot, layer, restore);

        // Pull every layer above the takeover except graphics: the scorebug stays up.
        foreach (var above in LayersBottomUp.SkipWhile(l => l != layer).Skip(1))
            if (above != Graphics)
                above.Gate.FadeTo(0, Fade);

        OnPropertyChanged(nameof(IsTakeoverActive));
        Report($"TAKEOVER: {slot.Name}");
    }

    private void EndTakeover(bool restore)
    {
        if (_takeover is not { } t)
            return;
        _takeover = null;

        foreach (var layer in LayersBottomUp)
            layer.Gate.FadeTo(1, Fade);

        if (restore)
        {
            if (t.Restore is { IsEmpty: false } previous)
                Trigger(previous, TriggerOrigin.Restore);
            else
                t.Layer.Clear(Fade);
        }

        OnPropertyChanged(nameof(IsTakeoverActive));
    }

    // ---- Sponsor rotation -------------------------------------------------------------

    private IEnumerable<ClipSlot> RotationSlots => Media.Slots.Where(s => s.InRotation && !s.IsEmpty);

    private ClipSlot? NextRotationSlot(ClipSlot? after)
    {
        var slots = RotationSlots.ToList();
        if (slots.Count == 0) return null;
        var column = after?.Column ?? -1;
        return slots.FirstOrDefault(s => s.Column > column) ?? slots[0];
    }

    private static TimeSpan? Dwell(ClipSlot slot) =>
        slot.DwellSeconds > 0 ? TimeSpan.FromSeconds(slot.DwellSeconds)
        : slot.Kind == ClipKind.Video ? null   // to the end of the video
        : DefaultStillDwell;

    private void StartRotation()
    {
        var first = NextRotationSlot(Media.OnAirSlot is { InRotation: true } current ? current : null);
        if (first is null)
        {
            Report("Nothing in rotation: tick IN ROTATION on MEDIA clips first.");
            return;
        }
        RotationRunning = true;
        Trigger(first, TriggerOrigin.Rotation);
    }

    private void StopRotation()
    {
        RotationRunning = false;
        _rotationSlot = null;
        _rotationClock.Reset();
        RotationStatus = null;
    }

    private void AdvanceRotation()
    {
        var next = NextRotationSlot(_rotationSlot);
        if (next is null)
            StopRotation();
        else
            Trigger(next, TriggerOrigin.Rotation);
    }

    // ---- 10 Hz tick: countdowns + rotation ----------------------------------------------

    private void OnTick()
    {
        foreach (var layer in LayersBottomUp)
        {
            if (layer.OnAirSlot is not null && layer.OnAirSource is { Position: { } position, Duration: { } duration })
                layer.SetProgress(position, duration);
            else
                layer.ClearProgress();
        }

        if (!RotationRunning || _rotationSlot is not { } slot || _takeover is not null)
            return;

        // A rotation clip that never made it to air (missing file): skip it.
        if (Media.OnAirSlot != slot && _rotationClock.Elapsed > TimeSpan.FromSeconds(12))
        {
            AdvanceRotation();
            return;
        }

        var next = NextRotationSlot(slot);
        if (Dwell(slot) is { } dwell)
        {
            if (_rotationClock.Elapsed >= dwell)
            {
                AdvanceRotation();
                return;
            }
            Media.SetProgress(_rotationClock.Elapsed, dwell);   // the dwell is what matters, not the loop
        }
        RotationStatus = $"▶ {slot.Name}   ·   next: {next?.Name}   {Media.RemainingText}";
    }

    // ---- Text graphics ----------------------------------------------------------------

    private void ApplyGraphics(GraphicsCommand c)
    {
        switch (c.Action)
        {
            case GraphicsAction.ShowLowerThird: TextGraphics.ShowLowerThird(); break;
            case GraphicsAction.HideLowerThird: TextGraphics.HideLowerThird(); break;
            case GraphicsAction.ShowTicker: TextGraphics.ShowTicker(); break;
            case GraphicsAction.HideTicker: TextGraphics.HideTicker(); break;
            case GraphicsAction.ShowPrompt: TextGraphics.ShowPrompt(c.Text); break;
            case GraphicsAction.HidePrompt: TextGraphics.HidePrompt(); break;
        }
    }

    // ---- Preview / cue --------------------------------------------------------------

    private async void Cue(ClipSlot slot)
    {
        DisposeCue();
        if (slot.IsEmpty)
            return;

        var source = CreateSource(slot, cue: true);
        if (source is null)
            return;

        _cue = source;
        CuedSlot = slot;
        slot.IsCued = true;
        source.ImageChanged += OnCueImageChanged;

        try
        {
            await source.Ready.WaitAsync(TimeSpan.FromSeconds(10));
            if (_cue == source)
                PreviewImage = source.Image;
        }
        catch (Exception)
        {
            if (_cue == source)
            {
                DisposeCue();
                Report($"Could not cue \"{slot.Name}\".");
            }
        }
    }

    private void OnCueImageChanged(object? sender, EventArgs e) => PreviewImage = _cue?.Image;

    /// <summary>Hands the pre-buffered cue to the caller (who now owns it) and clears the preview.</summary>
    private IFrameSource? TakeCue()
    {
        var source = _cue;
        if (source is null)
            return null;

        source.ImageChanged -= OnCueImageChanged;
        _cue = null;
        if (CuedSlot is not null) CuedSlot.IsCued = false;
        CuedSlot = null;
        PreviewImage = null;
        source.Resume();
        return source;
    }

    private void DisposeCue()
    {
        if (_cue is not null)
        {
            _cue.ImageChanged -= OnCueImageChanged;
            _cue.Dispose();
            _cue = null;
        }
        if (CuedSlot is not null) CuedSlot.IsCued = false;
        CuedSlot = null;
        PreviewImage = null;
    }

    /// <summary>Removes a slot's content, stopping it first if it is cued.</summary>
    public void ClearSlot(ClipSlot slot)
    {
        if (slot == CuedSlot)
            DisposeCue();
        slot.Clear();
    }

    private void Report(string message) => StatusMessage?.Invoke(this, message);
}
