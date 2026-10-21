using System.Collections.ObjectModel;
using System.Windows.Media;
using AV.Playout.Media;

namespace AV.Playout.Core.Layers;

public enum ShowResult { Shown, Superseded, Failed }

/// <summary>
/// A compositor layer and its row of clip slots. Holds two decks so a new clip can
/// crossfade over the old one; the incoming deck is only revealed once its first
/// frame has decoded, so a trigger never flashes black.
/// </summary>
public sealed class Layer : ObservableObject
{
    private double _opacity = 1.0;
    private bool _bypass;
    private bool _aOnTop;
    private ClipSlot? _activeSlot;
    private ClipSlot? _onAirSlot;
    private IFrameSource? _pending;

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
            if (_activeSlot is not null) _activeSlot.IsPlaying = false;
            _activeSlot = value;
            if (_activeSlot is not null) _activeSlot.IsPlaying = true;
            OnPropertyChanged();
        }
    }

    /// <summary>The slot whose picture is actually on the decks.</summary>
    public ClipSlot? OnAirSlot => _onAirSlot;

    /// <summary>Raised when a play-once clip on this layer finishes.</summary>
    public event EventHandler<ClipSlot?>? ClipEnded;

    /// <summary>
    /// Crossfades <paramref name="source"/> onto the layer once its first frame is
    /// decoded. A newer trigger while this one is still buffering supersedes it.
    /// </summary>
    public async Task<ShowResult> ShowAsync(IFrameSource source, ClipSlot? slot, TimeSpan fade)
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
        return ShowResult.Shown;
    }

    public void Clear(TimeSpan fade)
    {
        _pending?.Dispose();
        _pending = null;
        DeckA.FadeTo(0, fade, DeckA.Detach);
        DeckB.FadeTo(0, fade, DeckB.Detach);
        ActiveSlot = null;
        _onAirSlot = null;
    }
}
