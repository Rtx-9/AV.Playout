using System.Windows.Media;

namespace AV.Playout.Media;

/// <summary>
/// Anything that can fill a layer: a still, a video file, a camera, a network stream.
/// Every source exposes its picture as an <see cref="ImageSource"/> so it composes
/// inside WPF with real alpha, and can be shown by several views at once.
/// All members are used on the UI thread.
/// </summary>
public interface IFrameSource : IDisposable
{
    /// <summary>The current picture. Null until the first frame has been decoded.</summary>
    ImageSource? Image { get; }

    /// <summary>Raised when <see cref="Image"/> is replaced (e.g. the stream changed resolution).</summary>
    event EventHandler? ImageChanged;

    /// <summary>Completes once the first frame is on <see cref="Image"/>; faults if the source can't open.</summary>
    Task Ready { get; }

    /// <summary>Raised when a play-once source reaches its end.</summary>
    event EventHandler? Ended;

    /// <summary>Starts a source that was pre-buffered paused on its first frame.</summary>
    void Resume();
}

/// <summary>A pre-decoded still image. Ready immediately.</summary>
public sealed class StillSource(ImageSource image) : IFrameSource
{
    public ImageSource? Image => image;
    public Task Ready => Task.CompletedTask;

    public event EventHandler? ImageChanged { add { } remove { } }
    public event EventHandler? Ended { add { } remove { } }

    public void Resume() { }
    public void Dispose() { }
}
