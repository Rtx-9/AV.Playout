using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using VlcMedia = LibVLCSharp.Shared.Media;

namespace AV.Playout.Media;

public static class VlcRuntime
{
    private static readonly Lazy<LibVLC> Instance = new(() =>
    {
        LibVLCSharp.Shared.Core.Initialize();
        return new LibVLC(
            "--no-osd",
            "--no-video-title-show",
            "--no-snapshot-preview",
            "--no-stats",
            "--quiet",
            "--file-caching=150",      // default 1000 ms delays every trigger
            "--network-caching=300",
            "--live-caching=100");
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public static LibVLC LibVLC => Instance.Value;

    /// <summary>Loads libvlc and its plugins in the background so the first trigger is fast.</summary>
    public static void Warmup() => Task.Run(() => _ = Instance.Value);
}

public sealed record VlcSourceOptions
{
    public bool Loop { get; init; }
    public bool Audio { get; init; } = true;

    /// <summary>Decode the first frame, then pause until <see cref="IFrameSource.Resume"/> (cue / pre-buffer).</summary>
    public bool PauseOnFirstFrame { get; init; }

    /// <summary>Silent until <see cref="IFrameSource.Resume"/>, so a cue in the preview never reaches the PA.</summary>
    public bool Muted { get; init; }

    /// <summary>Start position in seconds (used for thumbnails).</summary>
    public double StartTime { get; init; }
}

/// <summary>
/// Plays anything LibVLC can open (files, dshow:// capture devices, rtsp/srt/udp
/// streams) and renders it into a <see cref="WriteableBitmap"/>.
///
/// Threading: LibVLC decodes on its own threads into a small ring of native BGRA
/// buffers. Once per WPF frame (CompositionTarget.Rendering, UI thread) the newest
/// complete buffer is copied into the bitmap. Decode never blocks the UI and the UI
/// never blocks decode; a slow UI frame just skips to the newest picture.
/// BGRA keeps the alpha channel, so ProRes 4444 / QuickTime Animation clips with
/// alpha composite over the layers beneath them.
/// </summary>
public sealed class VlcSource : IFrameSource
{
    private const int BufferCount = 4;

    private readonly VlcMedia _media;
    private readonly MediaPlayer _player;
    private readonly Dispatcher _dispatcher;
    private readonly VlcSourceOptions _options;

    // Delegates are fields so the GC can't collect them while native code holds them.
    private readonly MediaPlayer.LibVLCVideoFormatCb _formatCb;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanupCb;
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;

    private readonly object _gate = new();
    private readonly List<IntPtr> _retiredBuffers = [];
    private IntPtr[] _buffers = [];
    private int _width, _height, _pitch;
    private int _latest = -1, _reading = -1, _writing = -1;
    private bool _hasNewFrame;

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WriteableBitmap? _bitmap;
    private bool _disposed;

    public VlcSource(string mrl, VlcSourceOptions options)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _options = options;

        var mediaOptions = new List<string>();
        if (options.Loop) mediaOptions.Add(":input-repeat=65535");
        if (!options.Audio) mediaOptions.Add(":no-audio");
        if (options.StartTime > 0) mediaOptions.Add($":start-time={options.StartTime.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

        var fromType = mrl.Contains("://", StringComparison.Ordinal) ? FromType.FromLocation : FromType.FromPath;
        _media = new VlcMedia(VlcRuntime.LibVLC, mrl, fromType, [.. mediaOptions]);
        _player = new MediaPlayer(_media);

        _formatCb = OnFormat;
        _cleanupCb = OnCleanup;
        _lockCb = OnLock;
        _displayCb = OnDisplay;
        _player.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
        _player.SetVideoCallbacks(_lockCb, null, _displayCb);

        _player.EndReached += OnEndReached;
        _player.EncounteredError += OnError;

        if (options.Muted)
            _player.Mute = true;

        CompositionTarget.Rendering += OnRendering;
        _player.Play();
    }

    public ImageSource? Image => _bitmap;
    public Task Ready => _ready.Task;
    public event EventHandler? ImageChanged;
    public event EventHandler? Ended;

    // Plain property reads on the native player: cheap enough for a 10 Hz UI poll.
    public TimeSpan? Position => !_disposed && _player.Time > 0 ? TimeSpan.FromMilliseconds(_player.Time) : null;
    public TimeSpan? Duration => !_disposed && _player.Length > 0 ? TimeSpan.FromMilliseconds(_player.Length) : null;

    public void Resume()
    {
        if (_disposed) return;
        _player.Mute = false;
        _player.SetPause(false);
    }

    // ---- LibVLC threads ------------------------------------------------------------

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        Marshal.Copy("BGRA"u8.ToArray(), 0, chroma, 4);

        lock (_gate)
        {
            // Never free a buffer the UI might be copying from; retire it until Dispose.
            _retiredBuffers.AddRange(_buffers);

            _width = (int)width;
            _height = (int)height;
            _pitch = _width * 4;
            _buffers = new IntPtr[BufferCount];
            for (var i = 0; i < BufferCount; i++)
                _buffers[i] = Marshal.AllocHGlobal(_pitch * _height);
            _latest = _reading = _writing = -1;
            _hasNewFrame = false;
        }

        pitches = (uint)_pitch;
        lines = height;
        return BufferCount;
    }

    private void OnCleanup(ref IntPtr opaque) { }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        int index;
        lock (_gate)
        {
            index = 0;
            while (index == _latest || index == _reading)
                index++;
            _writing = index;
        }
        Marshal.WriteIntPtr(planes, _buffers[index]);
        return index;
    }

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        lock (_gate)
        {
            _latest = (int)picture;
            _writing = -1;
            _hasNewFrame = true;
        }
    }

    private void OnEndReached(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed) Ended?.Invoke(this, EventArgs.Empty);
        });

    private void OnError(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(() =>
            _ready.TrySetException(new InvalidOperationException("LibVLC could not open the source.")));

    // ---- UI thread -----------------------------------------------------------------

    private void OnRendering(object? sender, EventArgs e)
    {
        int index, width, height, pitch;
        IntPtr buffer;
        lock (_gate)
        {
            if (!_hasNewFrame || _latest < 0)
                return;
            _hasNewFrame = false;
            index = _latest;
            _reading = index;
            width = _width;
            height = _height;
            pitch = _pitch;
            buffer = _buffers[index];
        }

        try
        {
            if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
            {
                _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                ImageChanged?.Invoke(this, EventArgs.Empty);
            }
            _bitmap.WritePixels(new Int32Rect(0, 0, width, height), buffer, pitch * height, pitch);
        }
        finally
        {
            lock (_gate) _reading = -1;
        }

        if (!_ready.Task.IsCompleted)
        {
            if (_options.PauseOnFirstFrame)
                _player.SetPause(true);
            _ready.TrySetResult();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CompositionTarget.Rendering -= OnRendering;
        _player.EndReached -= OnEndReached;
        _player.EncounteredError -= OnError;
        _ready.TrySetCanceled();

        // Stop() joins LibVLC's threads and can take tens of milliseconds:
        // never on the UI thread, or the wall drops a frame.
        Task.Run(() =>
        {
            _player.Stop();
            _player.Dispose();
            _media.Dispose();
            lock (_gate)
            {
                foreach (var b in _buffers.Concat(_retiredBuffers))
                    Marshal.FreeHGlobal(b);
                _buffers = [];
                _retiredBuffers.Clear();
            }
        });
    }
}
