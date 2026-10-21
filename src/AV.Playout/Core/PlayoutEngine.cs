using System.Windows.Media;
using System.Windows.Threading;
using AV.Playout.Core.Commands;
using AV.Playout.Core.Layers;

namespace AV.Playout.Core;

/// <summary>
/// Single source of truth for what is on the wall. Both the output window and the
/// operator's program monitor bind to this object; nothing else mutates layer state.
/// </summary>
public sealed class PlayoutEngine : ObservableObject
{
    private readonly Dispatcher _dispatcher;
    private Brush _background = Brushes.Black;

    public PlayoutEngine(int canvasWidth, int canvasHeight)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
    }

    /// <summary>Raised on the UI thread after a command has changed program state.</summary>
    public event EventHandler<PlayoutCommand>? CommandExecuted;

    public int CanvasWidth { get; }
    public int CanvasHeight { get; }

    public Brush Background
    {
        get => _background;
        private set => SetField(ref _background, value);
    }

    public LayerState Media { get; } = new(LayerKind.Media);
    public LayerState Live { get; } = new(LayerKind.Live);
    public LayerState Graphics { get; } = new(LayerKind.Graphics);

    public LayerState GetLayer(LayerKind kind) => kind switch
    {
        LayerKind.Media => Media,
        LayerKind.Live => Live,
        LayerKind.Graphics => Graphics,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// Applies a command. Safe to call from any thread: keyboard hooks and MIDI
    /// callbacks arrive off the UI thread and are marshalled at Send priority so
    /// they jump ahead of layout and input work.
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
            case SetBackgroundCommand c:
                var brush = new SolidColorBrush(c.Color);
                brush.Freeze();
                Background = brush;
                break;

            case ShowStillCommand c:
                Media.Source = c.Still.Image;
                break;

            case ClearLayerCommand c:
                GetLayer(c.Layer).Source = null;
                break;

            case BlackoutCommand:
                Media.Source = null;
                Live.Source = null;
                Graphics.Source = null;
                Background = Brushes.Black;
                break;

            default:
                throw new NotSupportedException($"No handler for {command.GetType().Name}.");
        }

        CommandExecuted?.Invoke(this, command);
    }
}
