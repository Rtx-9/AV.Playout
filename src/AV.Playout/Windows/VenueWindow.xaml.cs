using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AV.Playout.Core;
using AV.Playout.Venue;

namespace AV.Playout.Windows;

/// <summary>
/// Live 3D preview of the arena: every screen surface is textured with its region
/// of the program canvas, so the operator can check how content reads on the cube,
/// ribbon and end board before it goes up.
/// </summary>
public partial class VenueWindow : Window
{
    private readonly PlayoutEngine _engine;
    private readonly ArenaModel _arena;
    private readonly Dictionary<ScreenDefinition, PropertyChangedEventHandler> _watchers = [];
    private int _ribbonRepeat = 1;

    // Orbit camera (degrees / metres).
    private Point3D _target;
    private double _distance, _yaw, _pitch;
    private Point? _dragFrom;
    private TimeSpan _lastFrame;

    private sealed record CameraPreset(Point3D Target, double Distance, double Yaw, double Pitch);

    private static readonly Dictionary<string, CameraPreset> Presets = new()
    {
        ["Overview"] = new(new Point3D(0, 0, 6), 105, -58, 34),
        ["LowerBowl"] = new(new Point3D(0, 0, 14), 23, -90, -20),     // a seat about ten rows up
        ["UpperDeck"] = new(new Point3D(0, 0, 12), 30, -90, 18),
        ["EndBoard"] = new(new Point3D(40, 0, 18), 34, 180, 4),
    };

    public VenueWindow(PlayoutEngine engine)
    {
        _engine = engine;
        InitializeComponent();
        DataContext = engine;

        _arena = ArenaModelBuilder.Build();
        SceneRoot.Content = _arena.Root;

        foreach (var screen in engine.Screens)
        {
            PropertyChangedEventHandler handler = (_, _) => ApplyScreen(screen);
            screen.PropertyChanged += handler;
            _watchers[screen] = handler;
            ApplyScreen(screen);
        }

        ApplyPreset("Overview");
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRendering;
            foreach (var (screen, handler) in _watchers)
                screen.PropertyChanged -= handler;
        };
    }

    /// <summary>Textures one screen with its canvas region, or turns it off.</summary>
    private void ApplyScreen(ScreenDefinition screen)
    {
        if (!_arena.Screens.TryGetValue(screen.Placement, out var model))
            return;

        if (screen.IsRibbon && screen.Repeat != _ribbonRepeat)
        {
            _ribbonRepeat = screen.Repeat;
            model.Geometry = _arena.RibbonMesh(_ribbonRepeat);
        }

        if (!screen.Enabled)
        {
            model.Material = model.BackMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x08, 0x08, 0x0A)));
            return;
        }

        // A VisualBrush re-renders the live canvas region each frame: video, crossfades,
        // scorebug and ticker all appear on the 3D screens exactly as on the output.
        var brush = new VisualBrush(MapCompositor.ProgramCanvas)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = screen.Region,
            Stretch = Stretch.Fill,
        };

        // LEDs emit light: black diffuse + emissive content, so screens glow in the dark bowl.
        // Both sides: the ribbon is seen from inside the ring, i.e. from its back face.
        model.Material = model.BackMaterial = new MaterialGroup
        {
            Children = { new DiffuseMaterial(Brushes.Black), new EmissiveMaterial(brush) },
        };
    }

    private void ResetScreens_Click(object sender, RoutedEventArgs e)
    {
        var defaults = ScreenDefinition.Defaults(_engine.CanvasWidth, _engine.CanvasHeight).ToDictionary(s => s.Placement);
        foreach (var screen in _engine.Screens)
        {
            if (!defaults.TryGetValue(screen.Placement, out var d)) continue;
            screen.Enabled = true;
            screen.X = d.X;
            screen.Y = d.Y;
            screen.Width = d.Width;
            screen.Height = d.Height;
            screen.Repeat = d.Repeat;
        }
    }

    // ---- Camera ------------------------------------------------------------------------

    private void Camera_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string preset)
            ApplyPreset(preset);
    }

    private void ApplyPreset(string name)
    {
        var p = Presets[name];
        _target = p.Target;
        _distance = p.Distance;
        _yaw = p.Yaw;
        _pitch = p.Pitch;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        double yaw = _yaw * Math.PI / 180, pitch = _pitch * Math.PI / 180;
        var offset = new Vector3D(
            Math.Cos(pitch) * Math.Cos(yaw),
            Math.Cos(pitch) * Math.Sin(yaw),
            Math.Sin(pitch)) * _distance;
        Camera.Position = _target + offset;
        Camera.LookDirection = -offset;
    }

    private void Orbit_Click(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _lastFrame = TimeSpan.Zero;
        if (OrbitToggle.IsChecked == true)
            CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_lastFrame != TimeSpan.Zero)
        {
            _yaw += 7 * (now - _lastFrame).TotalSeconds;   // one lap in ~50 s
            UpdateCamera();
        }
        _lastFrame = now;
    }

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = e.GetPosition(ViewportHost);
        ViewportHost.CaptureMouse();
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom is not { } from) return;
        var now = e.GetPosition(ViewportHost);
        _yaw -= (now.X - from.X) * 0.35;
        _pitch = Math.Clamp(_pitch + (now.Y - from.Y) * 0.25, -80, 85);
        _dragFrom = now;
        UpdateCamera();
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = null;
        ViewportHost.ReleaseMouseCapture();
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _distance = Math.Clamp(_distance * (e.Delta > 0 ? 0.88 : 1.14), 4, 260);
        UpdateCamera();
    }
}
