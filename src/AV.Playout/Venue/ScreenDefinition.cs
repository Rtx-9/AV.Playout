using System.Windows;
using System.Windows.Media;
using AV.Playout.Core;

namespace AV.Playout.Venue;

/// <summary>Where a screen physically hangs in the arena model.</summary>
public enum ScreenPlacement
{
    CubeNorth, CubeEast, CubeSouth, CubeWest,
    /// <summary>Continuous LED band on the fascia between the lower and upper bowl.</summary>
    Ribbon,
    /// <summary>Large board above the upper bowl at one end.</summary>
    EndBoard,
}

/// <summary>
/// A physical LED screen and the rectangle of the program canvas it shows. The LED
/// processor does the real slicing; this mapping drives the venue visualizer so the
/// operator sees what each screen will show before the doors open.
/// </summary>
public sealed class ScreenDefinition : ObservableObject
{
    private string _name;
    private bool _enabled = true;
    private int _x, _y, _width, _height;
    private int _repeat = 1;

    public ScreenDefinition(ScreenPlacement placement, string name, Color color, int x, int y, int width, int height, int repeat = 1)
    {
        Placement = placement;
        _name = name;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        ColorBrush = brush;
        _x = x;
        _y = y;
        _width = width;
        _height = height;
        _repeat = repeat;
    }

    public ScreenPlacement Placement { get; }

    /// <summary>Colour of the region outline on the canvas map.</summary>
    public Brush ColorBrush { get; }

    public string Name { get => _name; set => SetField(ref _name, value); }
    public bool Enabled { get => _enabled; set => SetField(ref _enabled, value); }

    public int X { get => _x; set { if (SetField(ref _x, Math.Max(0, value))) OnPropertyChanged(nameof(Region)); } }
    public int Y { get => _y; set { if (SetField(ref _y, Math.Max(0, value))) OnPropertyChanged(nameof(Region)); } }
    public int Width { get => _width; set { if (SetField(ref _width, Math.Max(1, value))) OnPropertyChanged(nameof(Region)); } }
    public int Height { get => _height; set { if (SetField(ref _height, Math.Max(1, value))) OnPropertyChanged(nameof(Region)); } }

    /// <summary>How many times the region repeats around a ribbon (each side shows the same ad).</summary>
    public int Repeat { get => _repeat; set => SetField(ref _repeat, Math.Clamp(value, 1, 16)); }

    public bool IsRibbon => Placement == ScreenPlacement.Ribbon;

    public Rect Region => new(_x, _y, _width, _height);

    /// <summary>
    /// A typical hockey arena: four-sided centre-hung cube mirroring the full canvas,
    /// a wrap-around ribbon fed from a strip at the bottom of the canvas, and an end board.
    /// </summary>
    public static IEnumerable<ScreenDefinition> Defaults(int canvasWidth, int canvasHeight)
    {
        var strip = canvasHeight / 9;
        yield return new(ScreenPlacement.CubeNorth, "Cube · North", Color.FromRgb(0x22, 0xC3, 0xE6), 0, 0, canvasWidth, canvasHeight);
        yield return new(ScreenPlacement.CubeEast, "Cube · East", Color.FromRgb(0x34, 0xD2, 0x7B), 0, 0, canvasWidth, canvasHeight);
        yield return new(ScreenPlacement.CubeSouth, "Cube · South", Color.FromRgb(0xB0, 0x6C, 0xFF), 0, 0, canvasWidth, canvasHeight);
        yield return new(ScreenPlacement.CubeWest, "Cube · West", Color.FromRgb(0x3F, 0x8C, 0xFF), 0, 0, canvasWidth, canvasHeight);
        yield return new(ScreenPlacement.Ribbon, "Ribbon board", Color.FromRgb(0xF5, 0xA5, 0x24), 0, canvasHeight - strip, canvasWidth, strip, repeat: 4);
        yield return new(ScreenPlacement.EndBoard, "End board", Color.FromRgb(0xE5, 0x25, 0x2A), 0, 0, canvasWidth, canvasHeight);
    }
}
