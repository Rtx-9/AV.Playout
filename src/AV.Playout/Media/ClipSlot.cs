using System.Windows.Media;
using System.Windows.Media.Imaging;
using AV.Playout.Core;
using AV.Playout.Core.Layers;
using AV.Playout.Triggers;

namespace AV.Playout.Media;

public enum ClipKind { Empty, Still, Video, Live }

public enum ClipMode
{
    /// <summary>Loop until something else is triggered on the layer.</summary>
    Loop,
    /// <summary>Play once, then fade the layer out.</summary>
    Once,
    /// <summary>Play once over everything (live feed is pulled), then restore what was on air.</summary>
    Takeover,
}

/// <summary>One cell of the clip grid: a layer row × a column.</summary>
public sealed class ClipSlot : ObservableObject
{
    private string _name = "";
    private string? _source;
    private ClipKind _kind;
    private ClipMode _mode;
    private Hotkey? _hotkey;
    private ImageSource? _thumbnail;
    private BitmapSource? _still;
    private bool _isPlaying, _isCued, _isSelected, _isLoading;

    public ClipSlot(LayerKind layer, int column)
    {
        Layer = layer;
        Column = column;
    }

    public LayerKind Layer { get; }
    public int Column { get; }
    public int ColumnNumber => Column + 1;

    public string Name { get => _name; set => SetField(ref _name, value); }

    /// <summary>File path, or an MRL such as dshow://, rtsp://, srt://.</summary>
    public string? Source { get => _source; set => SetField(ref _source, value); }

    public ClipKind Kind
    {
        get => _kind;
        set
        {
            if (SetField(ref _kind, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(KindGlyph));
            }
        }
    }

    public bool IsEmpty => _kind == ClipKind.Empty;

    public ClipMode Mode
    {
        get => _mode;
        set
        {
            if (SetField(ref _mode, value))
                OnPropertyChanged(nameof(ModeText));
        }
    }

    public string ModeText => _mode switch
    {
        ClipMode.Once => "ONCE",
        ClipMode.Takeover => "TAKEOVER",
        _ => "",
    };

    public string KindGlyph => _kind switch
    {
        ClipKind.Video => "▶",
        ClipKind.Still => "▣",
        ClipKind.Live => "●",
        _ => "",
    };

    public Hotkey? Hotkey
    {
        get => _hotkey;
        set
        {
            if (SetField(ref _hotkey, value))
                OnPropertyChanged(nameof(HotkeyText));
        }
    }

    public string? HotkeyText => _hotkey?.ToString();

    public ImageSource? Thumbnail { get => _thumbnail; set => SetField(ref _thumbnail, value); }

    /// <summary>Full-resolution decoded picture for <see cref="ClipKind.Still"/> slots.</summary>
    public BitmapSource? Still { get => _still; set => SetField(ref _still, value); }

    /// <summary>Tally: on the program output.</summary>
    public bool IsPlaying { get => _isPlaying; set => SetField(ref _isPlaying, value); }

    /// <summary>Pre-buffered in the preview monitor.</summary>
    public bool IsCued { get => _isCued; set => SetField(ref _isCued, value); }

    public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }
    public bool IsLoading { get => _isLoading; set => SetField(ref _isLoading, value); }

    public void Clear()
    {
        Name = "";
        Source = null;
        Kind = ClipKind.Empty;
        Mode = ClipMode.Loop;
        Hotkey = null;
        Thumbnail = null;
        Still = null;
    }
}
