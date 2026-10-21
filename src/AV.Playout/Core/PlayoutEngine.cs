using System.Windows.Media;
using System.Windows.Threading;
using AV.Playout.Core.Commands;
using AV.Playout.Core.Layers;
using AV.Playout.Graphics;
using AV.Playout.Media;
using AV.Playout.Triggers;

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

    private sealed record Takeover(ClipSlot Slot, Layer Layer, ClipSlot? Restore);

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
                else Trigger(slot);
                break;

            case TriggerColumnCommand c:
                foreach (var layer in LayersBottomUp)
                    if (!layer.Slots[c.Column].IsEmpty)
                        Trigger(layer.Slots[c.Column]);
                break;

            case CueSlotCommand c:
                Cue(GetLayer(c.Layer).Slots[c.Column]);
                break;

            case TakeCommand:
                if (CuedSlot is { } cued) Trigger(cued);
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
                EndTakeover(restore: false);
                foreach (var layer in LayersBottomUp)
                    layer.Clear(Fade);
                Scoreboard.IsVisible = false;
                Background = Brushes.Black;
                break;

            case ScoreboardCommand c:
                Scoreboard.Apply(c.Action);
                break;

            default:
                throw new NotSupportedException($"No handler for {command.GetType().Name}.");
        }

        CommandExecuted?.Invoke(this, command);
    }

    // ---- Triggering -----------------------------------------------------------------

    private void Trigger(ClipSlot slot)
    {
        var layer = GetLayer(slot.Layer);

        if (slot.Mode == ClipMode.Takeover)
            BeginTakeover(slot, layer);
        else if (_takeover?.Layer == layer)
            EndTakeover(restore: false);   // operator overrode the takeover by hand

        var source = slot == CuedSlot ? TakeCue() : CreateSource(slot, cue: false);
        if (source is null)
            return;

        _ = ShowAsync(layer, source, slot);
    }

    private async Task ShowAsync(Layer layer, IFrameSource source, ClipSlot slot)
    {
        if (await layer.ShowAsync(source, slot, Fade) == ShowResult.Failed)
            Report($"Could not play \"{slot.Name}\" ({slot.Source}).");
    }

    private IFrameSource? CreateSource(ClipSlot slot, bool cue)
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
                    Loop = slot.Kind == ClipKind.Video && slot.Mode == ClipMode.Loop,
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
        if (_takeover is { } t && t.Slot == slot)
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
                Trigger(previous);
            else
                t.Layer.Clear(Fade);
        }

        OnPropertyChanged(nameof(IsTakeoverActive));
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
