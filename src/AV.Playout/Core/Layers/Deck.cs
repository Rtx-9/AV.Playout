using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AV.Playout.Media;

namespace AV.Playout.Core.Layers;

/// <summary>
/// One of a layer's two playback decks. Crossfades run as WPF DoubleAnimations on
/// <see cref="Opacity"/>, driven by the composition clock, so the output and the
/// program monitor (which both bind here) fade in lock-step.
/// </summary>
public sealed class Deck : Animatable
{
    public static readonly DependencyProperty OpacityProperty =
        DependencyProperty.Register(nameof(Opacity), typeof(double), typeof(Deck), new PropertyMetadata(0.0));

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(Deck), new PropertyMetadata(null));

    public static readonly DependencyProperty ZIndexProperty =
        DependencyProperty.Register(nameof(ZIndex), typeof(int), typeof(Deck), new PropertyMetadata(0));

    private int _token;

    public Deck(double initialOpacity = 0.0) => Opacity = initialOpacity;

    public double Opacity
    {
        get => (double)GetValue(OpacityProperty);
        set => SetValue(OpacityProperty, value);
    }

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        private set => SetValue(SourceProperty, value);
    }

    public int ZIndex
    {
        get => (int)GetValue(ZIndexProperty);
        set => SetValue(ZIndexProperty, value);
    }

    public IFrameSource? Content { get; private set; }

    public void Attach(IFrameSource source)
    {
        Detach();
        _token++;
        Content = source;
        Source = source.Image;
        source.ImageChanged += OnImageChanged;
    }

    public void Detach()
    {
        if (Content is null)
            return;
        _token++;
        Content.ImageChanged -= OnImageChanged;
        Content.Dispose();
        Content = null;
        Source = null;
    }

    /// <summary>
    /// Animates <see cref="Opacity"/> from its current (possibly mid-fade) value.
    /// <paramref name="completed"/> runs only if nothing re-targets this deck first.
    /// </summary>
    public void FadeTo(double target, TimeSpan duration, Action? completed = null)
    {
        var token = ++_token;
        var from = Opacity;

        if (duration <= TimeSpan.Zero || Math.Abs(from - target) < 0.001)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = target;
            completed?.Invoke();
            return;
        }

        // Base value first; the animation holds the visible value until it stops,
        // then the property rests on the target.
        Opacity = target;
        var animation = new DoubleAnimation(from, target, duration) { FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) =>
        {
            if (token == _token)
                completed?.Invoke();
        };
        BeginAnimation(OpacityProperty, animation);
    }

    private void OnImageChanged(object? sender, EventArgs e) => Source = Content?.Image;

    protected override Freezable CreateInstanceCore() => new Deck();
}
