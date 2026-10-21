using System.Collections.ObjectModel;
using System.Windows.Media;
using AV.Playout.Media;

namespace AV.Playout.Core.Layers;

public enum ShowResult { Shown, Superseded, Failed }

/// <summary>Why a clip went to air; recorded in the as-run log.</summary>
public enum TriggerOrigin { Operator, Hotkey, Column, Take, Rotation, Takeover, Restore }

public sealed record OnAirChange(ClipSlot? Slot, TriggerOrigin Origin);

/// <summary>
/// A compositor layer and its row of clip slots. Holds two decks so a new clip can
/// crossfade over the old one; the incoming deck is only revealed once its first
/// frame has decoded, so a trigger never flashes black.
/// </summary>
public sealed class Layer : ObservableObject
{
    private static readonly TimeSpan EndingWarning = TimeSpan.FromSeconds(5);

    private double _opacity = 1.0;
    private bool _bypass;
    private bool _aOnTop;
    private ClipSlot? _activeSlot;
    private ClipSlot? _onAirSlot;
    private IFrameSource? _pending;
    private double _progress;
    private string? _remainingText;
    private bool _isEnding;

    public Layer(LayerKind kind, string name, Color color, int columns)
    {
        Kind = kind;
        Name = name;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        AccentBrush = brush;
        Slots = new ObservableCollection<ClipSlot>(Enumerable.Range(0, columns).Select(c => new ClipSlot(kind, c)));
    }

    public LayerKind Kind { get; }
    public string Name { get; }
    public Brush AccentBrush { get; }
    public ObservableCollection<ClipSlot> Slots { get; }

    public Deck DeckA { get; } = new();
    public Deck DeckB { get; } = new();

    /// <summary>Takeover gate: faded to 0 while a takeover on a lower layer pulls this one.</summary>
    public Deck Gate { get; } = new(initialOpacity: 1.0);

    /// <summary>Layer fader, 0–1.</summary>
    public double Opacity
    {
        get => _opacity;
        set
        {
            if (SetField(ref _opacity, Math.Clamp(value, 0, 1)))
                OnPropertyChanged(nameof(OpacityPercent));
        }
    }

    public string OpacityPercent => $"{_opacity * 100:0}%";

    public bool Bypass { get => _bypass; set => SetField(ref _bypass, value); }

    /// <summary>The slot showing tally (set on trigger, before its first frame is up).</summary>
    public ClipSlot? ActiveSlot
    {
        get => _activeSlot;
        private set
        {
            if (_activeSlot == value) return;
            if (_activeSlot is not null)
            {
                _activeSlot.IsPlaying = false;
                _activeSlot.SetProgress(0, null, false);
            }
            _activeSlot = value;
            if (_activeSlot is not null) _activeSlot.IsPlaying = true;
            OnPropertyChanged();
        }
    }

    /// <summary>The slot whose picture is actually on the decks.</summary>
    public ClipSlot? OnAirSlot => _onAirSlot;

    /// <summary>The source on the top deck.</summary>
    public IFrameSource? OnAirSource => (_aOnTop ? DeckA : DeckB).Content;

    // ---- Countdown -------------------------------------------------------------------

    /// <summary>0–1 through the current clip (or rotation dwell).</summary>
    public double Progress { get => _progress; private set => SetField(ref _progress, value); }

    /// <summary>"-0:12", or null when the layer has nothing with a timeline.</summary>
    public string? RemainingText { get => _remainingText; private set => SetField(ref _remainingText, value); }

    /// <summary>True in the last five seconds: the operator should be ready for what's next.</summary>
    public bool IsEnding { get => _isEnding; private set => SetField(ref _isEnding, value); }

    public void SetProgress(TimeSpan position, TimeSpan duration)
    {
        var remaining = duration - position;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        Progress = duration > TimeSpan.Zero ? Math.Clamp(position / duration, 0, 1) : 0;
        RemainingText = FormatRemaining(remaining);
        IsEnding = remaining <= EndingWarning;
        _activeSlot?.SetProgress(Progress, RemainingText, IsEnding);
    }

    public void ClearProgress()
    {
        Progress = 0;
        RemainingText = null;
        IsEnding = false;
        _activeSlot?.SetProgress(0, null, false);
    }

    public static string FormatRemaining(TimeSpan t)
    {
        var seconds = (int)Math.Ceiling(t.TotalSeconds);
        return $"-{seconds / 60}:{seconds % 60:00}";
    }

    // ---- Playback ---------------------------------------------------------------------

    /// <summary>Raised on the UI thread when what is actually on air changes (null = layer cleared).</summary>
    public event EventHandler<OnAirChange>? OnAirChanged;

    /// <summary>Raised when a play-once clip on this layer finishes.</summary>
    public event EventHandler<ClipSlot?>? ClipEnded;

    /// <summary>
    /// Crossfades <paramref name="source"/> onto the layer once its first frame is
    /// decoded. A newer trigger while this one is still buffering supersedes it.
    /// </summary>
    public async Task<ShowResult> ShowAsync(IFrameSource source, ClipSlot? slot, TimeSpan fade, TriggerOrigin origin)
    {
        if (_pending is not null && _pending != source)
            _pending.Dispose();
        _pending = source;
        ActiveSlot = slot;

        try
        {
            await source.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
            if (_pending == source)
            {
                _pending = null;
                ActiveSlot = _onAirSlot;
            }
            source.Dispose();
            return ShowResult.Failed;
        }

        if (_pending != source)
            return ShowResult.Superseded;
        _pending = null;

        var incoming = _aOnTop ? DeckB : DeckA;
        var outgoing = _aOnTop ? DeckA : DeckB;
        _aOnTop = !_aOnTop;

        incoming.Attach(source);
        incoming.FadeTo(0, TimeSpan.Zero);   // cancel any fade still running on this deck
        incoming.ZIndex = 1;
        outgoing.ZIndex = 0;

        incoming.FadeTo(1, fade);
        outgoing.FadeTo(0, fade, outgoing.Detach);

        _onAirSlot = slot;
        source.Ended += (_, _) =>
        {
            if (incoming.Content == source)
                ClipEnded?.Invoke(this, slot);
        };
        OnAirChanged?.Invoke(this, new OnAirChange(slot, origin));
        return ShowResult.Shown;
    }

    public void Clear(TimeSpan fade)
    {
        _pending?.Dispose();
        _pending = null;
        DeckA.FadeTo(0, fade, DeckA.Detach);
        DeckB.FadeTo(0, fade, DeckB.Detach);
        ActiveSlot = null;
        ClearProgress();
        if (_onAirSlot is not null)
        {
            _onAirSlot = null;
            OnAirChanged?.Invoke(this, new OnAirChange(null, TriggerOrigin.Operator));
        }
    }
}
