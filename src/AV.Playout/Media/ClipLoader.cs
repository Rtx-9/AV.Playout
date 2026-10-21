using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AV.Playout.Media;

/// <summary>
/// Fills clip slots: classifies the source, pre-decodes stills off the UI thread and
/// grabs video thumbnails with LibVLC.
/// </summary>
public static class ClipLoader
{
    private const int ThumbnailWidth = 320;

    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif" };

    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".mxf",
            ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".flv", ".hap",
        };

    public static ClipKind Classify(string source)
    {
        if (source.Contains("://", StringComparison.Ordinal))
            return ClipKind.Live;
        var ext = Path.GetExtension(source);
        if (ImageExtensions.Contains(ext)) return ClipKind.Still;
        if (VideoExtensions.Contains(ext)) return ClipKind.Video;
        return ClipKind.Empty;
    }

    public static bool IsSupportedFile(string path) => Classify(path) is ClipKind.Still or ClipKind.Video;

    /// <summary>Loads <paramref name="source"/> into <paramref name="slot"/>. Keeps the slot's mode and hotkey.</summary>
    public static async Task LoadAsync(ClipSlot slot, string source, string? name = null)
    {
        var kind = Classify(source);
        if (kind == ClipKind.Empty)
            throw new NotSupportedException($"Unsupported file type: {Path.GetExtension(source)}");

        slot.IsLoading = true;
        try
        {
            slot.Source = source;
            slot.Name = name ?? (kind == ClipKind.Live ? LiveName(source) : Path.GetFileNameWithoutExtension(source));
            slot.Kind = kind;
            slot.Still = null;
            slot.Thumbnail = null;

            switch (kind)
            {
                case ClipKind.Still:
                    var (full, thumb) = await Task.Run(() => (ToPbgra32(Decode(source, 0)), Decode(source, ThumbnailWidth)));
                    slot.Still = full;
                    slot.Thumbnail = thumb;
                    break;

                case ClipKind.Video:
                    slot.Thumbnail = await GrabThumbnailAsync(source, startTime: 1.5)
                                     ?? await GrabThumbnailAsync(source, startTime: 0);
                    break;
            }
        }
        finally
        {
            slot.IsLoading = false;
        }
    }

    private static string LiveName(string mrl) =>
        mrl.StartsWith("dshow://", StringComparison.OrdinalIgnoreCase) ? "Camera" : new Uri(mrl).Host;

    private static async Task<BitmapSource?> GrabThumbnailAsync(string path, double startTime)
    {
        using var source = new VlcSource(path, new VlcSourceOptions { Audio = false, PauseOnFirstFrame = true, StartTime = startTime });
        try
        {
            var first = await Task.WhenAny(source.Ready, Task.Delay(TimeSpan.FromSeconds(6)));
            if (first != source.Ready || !source.Ready.IsCompletedSuccessfully || source.Image is not BitmapSource frame)
                return null;

            var scale = ThumbnailWidth / (double)frame.PixelWidth;
            var scaled = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
            var cached = new CachedBitmap(scaled, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            cached.Freeze();
            return cached;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static BitmapSource Decode(string filePath, int decodePixelWidth)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
        if (decodePixelWidth > 0)
            bitmap.DecodePixelWidth = decodePixelWidth;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    // WPF renders Pbgra32 natively. Converting here, off the UI thread, avoids a
    // format conversion (and a dropped frame) the first time the still goes to air.
    private static BitmapSource ToPbgra32(BitmapSource source)
    {
        if (source.Format == PixelFormats.Pbgra32)
            return source;

        var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var cached = new CachedBitmap(converted, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        cached.Freeze();
        return cached;
    }
}
