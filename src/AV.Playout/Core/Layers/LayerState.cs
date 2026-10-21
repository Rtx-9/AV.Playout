using System.Windows.Media;

namespace AV.Playout.Core.Layers;

/// <summary>
/// The data behind one compositor layer. Holds an <see cref="ImageSource"/>, never a
/// UIElement, so the same layer can be drawn by the output and the preview at once.
/// In Phase 2 <see cref="Source"/> becomes a WriteableBitmap fed by a video decoder.
/// </summary>
public sealed class LayerState : ObservableObject
{
    private ImageSource? _source;
    private double _opacity = 1.0;
    private bool _isVisible = true;

    public LayerState(LayerKind kind) => Kind = kind;

    public LayerKind Kind { get; }

    public ImageSource? Source
    {
        get => _source;
        set => SetField(ref _source, value);
    }

    public double Opacity
    {
        get => _opacity;
        set => SetField(ref _opacity, Math.Clamp(value, 0.0, 1.0));
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetField(ref _isVisible, value);
    }
}
