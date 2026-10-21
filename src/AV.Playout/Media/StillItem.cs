using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AV.Playout.Core;

namespace AV.Playout.Media;

/// <summary>
/// A still image in the media bin. Decoded and converted to the compositor's native
/// pixel format when it is added, so taking it to air is a pointer swap, not a decode.
/// </summary>
public sealed class StillItem : ObservableObject
{
    private const int ThumbnailWidth = 320;
    private bool _isOnAir;

    private StillItem(string filePath, BitmapSource image, BitmapSource thumbnail)
    {
        FilePath = filePath;
        Name = Path.GetFileNameWithoutExtension(filePath);
        Image = image;
        Thumbnail = thumbnail;
    }

    public string Name { get; }
    public string FilePath { get; }

    /// <summary>Full-resolution, frozen, premultiplied BGRA.</summary>
    public BitmapSource Image { get; }

    public BitmapSource Thumbnail { get; }

    /// <summary>Tally: true while this still is on the program output.</summary>
    public bool IsOnAir
    {
        get => _isOnAir;
        set => SetField(ref _isOnAir, value);
    }

    public static Task<StillItem> LoadAsync(string filePath) => Task.Run(() =>
    {
        var full = Decode(filePath, decodePixelWidth: 0);
        var thumb = Decode(filePath, ThumbnailWidth);
        return new StillItem(filePath, ToPbgra32(full), thumb);
    });

    private static BitmapSource Decode(string filePath, int decodePixelWidth)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;   // read the file now and release it
        bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
        if (decodePixelWidth > 0)
            bitmap.DecodePixelWidth = decodePixelWidth;
        bitmap.EndInit();
        bitmap.Freeze();                                  // frozen = usable from any thread
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
