using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace AV.Playout.Venue;

/// <summary>The static arena geometry plus one surface per screen, ready for live content.</summary>
public sealed class ArenaModel(
    Model3DGroup root, IReadOnlyDictionary<ScreenPlacement, GeometryModel3D> screens, Func<int, MeshGeometry3D> ribbonMesh)
{
    public Model3DGroup Root { get; } = root;
    public IReadOnlyDictionary<ScreenPlacement, GeometryModel3D> Screens { get; } = screens;

    /// <summary>Ribbon geometry with its texture repeated N times around the bowl.</summary>
    public Func<int, MeshGeometry3D> RibbonMesh { get; } = ribbonMesh;
}

/// <summary>
/// Procedurally builds a hockey arena in metres (X along the rink, Y across, Z up,
/// origin at centre ice): the rink and boards, a stepped lower and upper bowl, the
/// fascia ribbon board between them, a four-sided centre-hung cube and an end board.
/// The bowl follows a superellipse, which gives the rounded-rectangle shape of a
/// real arena with only a handful of parameters.
/// </summary>
public static class ArenaModelBuilder
{
    // NHL rink: 61 × 26 m with rounded corners.
    private const double RinkA = 30.5, RinkB = 13, Squareness = 5;
    private const int Segments = 192;
    private const int Sections = 24;   // seating sections, alternately shaded

    private const double LowerStart = 2.0, LowerDepth = 0.85, LowerRise = 0.45;
    private const int LowerRows = 14;
    private const double UpperDepth = 0.95, UpperRise = 0.62;
    private const int UpperRows = 12;
    private const double FasciaHeight = 2.6;

    public const double CubeHalf = 4.2;
    public const double CubeBottom = 14.3, CubeFaceWidth = 8.0, CubeFaceHeight = 4.5;

    public static ArenaModel Build()
    {
        var root = new Model3DGroup();
        root.Children.Add(new AmbientLight(Color.FromRgb(0x30, 0x30, 0x34)));
        root.Children.Add(new DirectionalLight(Color.FromRgb(0x9A, 0x9A, 0x9A), new Vector3D(-0.3, -0.4, -1)));
        root.Children.Add(new DirectionalLight(Color.FromRgb(0x45, 0x45, 0x50), new Vector3D(0.5, 0.3, -0.6)));

        BuildIce(root);

        // Boards around the rink.
        var boards = new MeshBuilder();
        boards.Wall(Ring(0), 0, 1.15);
        root.Children.Add(Model(boards, Color.FromRgb(0xE8, 0xEA, 0xEE)));
        var cap = new MeshBuilder();
        cap.Band(Ring(0), Ring(0.25), 1.15, 1.15);
        root.Children.Add(Model(cap, Color.FromRgb(0xF5, 0xC5, 0x18)));

        // Floor between the boards and the first row (benches, penalty boxes).
        var floor = new MeshBuilder();
        floor.Band(Ring(0), Ring(LowerStart), 0, 0);
        root.Children.Add(Model(floor, Color.FromRgb(0x1A, 0x1C, 0x20)));

        // Lower bowl.
        var lowerA = new MeshBuilder();
        var lowerB = new MeshBuilder();
        var z = 0.0;
        var d = LowerStart;
        for (var row = 0; row < LowerRows; row++)
        {
            var zTop = 1.2 + row * LowerRise;
            Seats(lowerA, lowerB, d, z, zTop, LowerDepth);
            z = zTop;
            d += LowerDepth;
        }
        root.Children.Add(Model(lowerA, Color.FromRgb(0x6A, 0x1E, 0x24)));
        root.Children.Add(Model(lowerB, Color.FromRgb(0x52, 0x18, 0x1D)));

        // Fascia: the front face of the upper bowl. The ribbon board lives here.
        var fasciaInset = d;
        var fascia = new MeshBuilder();
        fascia.Wall(Ring(fasciaInset), z, z + FasciaHeight);
        root.Children.Add(Model(fascia, Color.FromRgb(0x0C, 0x0C, 0x0E)));
        var ribbonBottom = z + 0.55;
        var ribbonTop = ribbonBottom + 1.25;
        z += FasciaHeight;

        // Upper bowl.
        var upperA = new MeshBuilder();
        var upperB = new MeshBuilder();
        for (var row = 0; row < UpperRows; row++)
        {
            var zTop = z + UpperRise;
            Seats(upperA, upperB, d, z, zTop, UpperDepth);
            z = zTop;
            d += UpperDepth;
        }
        root.Children.Add(Model(upperA, Color.FromRgb(0x2A, 0x34, 0x42)));
        root.Children.Add(Model(upperB, Color.FromRgb(0x22, 0x2A, 0x36)));

        // Back wall of the building.
        var shell = new MeshBuilder();
        shell.Wall(Ring(d), z, z + 7);
        root.Children.Add(Model(shell, Color.FromRgb(0x16, 0x18, 0x1C)));

        var screens = new Dictionary<ScreenPlacement, GeometryModel3D>();

        // Ribbon: one continuous band around the fascia, facing the ice.
        var ribbonRing = Ring(fasciaInset - 0.04);
        MeshGeometry3D RibbonMesh(int repeat)
        {
            var mesh = new MeshBuilder();
            mesh.RibbonBand(ribbonRing, ribbonBottom, ribbonTop, repeat);
            return mesh.ToMesh();
        }
        screens[ScreenPlacement.Ribbon] = new GeometryModel3D(RibbonMesh(1), new DiffuseMaterial(Brushes.Black));

        BuildCube(root, screens);
        BuildEndBoard(root, screens, endX: RinkA + d - 2.2, bottom: z - 1.5);

        foreach (var screen in screens.Values)
            root.Children.Add(screen);
        return new ArenaModel(root, screens, RibbonMesh);
    }

    // ---- Parts ----------------------------------------------------------------------------

    private static void Seats(MeshBuilder a, MeshBuilder b, double d, double zFrom, double zTo, double depth)
    {
        var inner = Ring(d);
        var outer = Ring(d + depth);
        for (var i = 0; i < Segments; i++)
        {
            var mesh = i * Sections / Segments % 2 == 0 ? a : b;
            // riser (faces the ice) then tread
            mesh.Quad(P(inner[i], zFrom), P(inner[i + 1], zFrom), P(inner[i + 1], zTo), P(inner[i], zTo));
            mesh.Quad(P(inner[i], zTo), P(inner[i + 1], zTo), P(outer[i + 1], zTo), P(outer[i], zTo));
        }
    }

    private static void BuildIce(Model3DGroup root)
    {
        var ice = new MeshBuilder();
        var ring = Ring(0);
        for (var i = 0; i < Segments; i++)
        {
            ice.Triangle(
                new Point3D(0, 0, 0.01), new Point(0.5, 0.5),
                P(ring[i], 0.01), Uv(ring[i]),
                P(ring[i + 1], 0.01), Uv(ring[i + 1]));
        }

        var markings = IceMarkings();
        var emissive = new EmissiveMaterial(markings) { Color = Color.FromRgb(0x70, 0x70, 0x70) };
        var material = new MaterialGroup { Children = { new DiffuseMaterial(markings), emissive } };
        material.Freeze();
        root.Children.Add(new GeometryModel3D(ice.ToMesh(), material) { BackMaterial = material });

        static Point Uv(Point p) => new((p.X + RinkA) / (2 * RinkA), (RinkB - p.Y) / (2 * RinkB));
    }

    /// <summary>Red/blue lines, circles and creases, drawn in a 61 × 26 m box.</summary>
    private static Brush IceMarkings()
    {
        var red = new SolidColorBrush(Color.FromRgb(0xC8, 0x10, 0x2E));
        var blue = new SolidColorBrush(Color.FromRgb(0x00, 0x33, 0xA0));
        var crease = new SolidColorBrush(Color.FromRgb(0x8E, 0xC8, 0xF0));
        const double w = 2 * RinkA, h = 2 * RinkB, cx = RinkA, cy = RinkB;

        var g = new DrawingGroup();
        void Fill(Brush brush, Geometry geometry) => g.Children.Add(new GeometryDrawing(brush, null, geometry));
        void Ring(Brush brush, double x, double y, double r) =>
            g.Children.Add(new GeometryDrawing(null, new Pen(brush, 0.12), new EllipseGeometry(new Point(x, y), r, r)));

        Fill(new SolidColorBrush(Color.FromRgb(0xF2, 0xF6, 0xF9)), new RectangleGeometry(new Rect(0, 0, w, h)));
        Fill(red, new RectangleGeometry(new Rect(cx - 0.15, 0, 0.3, h)));
        Fill(blue, new RectangleGeometry(new Rect(cx - 7.62 - 0.15, 0, 0.3, h)));
        Fill(blue, new RectangleGeometry(new Rect(cx + 7.62 - 0.15, 0, 0.3, h)));
        foreach (var gx in new[] { 3.35, w - 3.35 })
        {
            Fill(red, new RectangleGeometry(new Rect(gx - 0.05, 1.5, 0.1, h - 3)));
            var side = gx < cx ? 1 : -1;
            var half = new CombinedGeometry(GeometryCombineMode.Intersect,
                new EllipseGeometry(new Point(gx, cy), 1.83, 1.83),
                new RectangleGeometry(side > 0 ? new Rect(gx, 0, 2, h) : new Rect(gx - 2, 0, 2, h)));
            Fill(crease, half);
        }
        Ring(blue, cx, cy, 4.57);
        Fill(blue, new EllipseGeometry(new Point(cx, cy), 0.15, 0.15));
        foreach (var fx in new[] { 9.45, w - 9.45 })
            foreach (var fy in new[] { cy - 6.7, cy + 6.7 })
            {
                Ring(red, fx, fy, 4.57);
                Fill(red, new EllipseGeometry(new Point(fx, fy), 0.3, 0.3));
            }

        var brush = new DrawingBrush(g)
        {
            Stretch = Stretch.Fill,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, w, h),
        };
        brush.Freeze();
        return brush;
    }

    private static void BuildCube(Model3DGroup root, Dictionary<ScreenPlacement, GeometryModel3D> screens)
    {
        const double top = CubeBottom + CubeFaceHeight + 0.4;
        var body = new MeshBuilder();
        body.Box(new Point3D(-CubeHalf, -CubeHalf, CubeBottom - 0.3), new Point3D(CubeHalf, CubeHalf, top));
        // Rigging up to the roof steel.
        foreach (var (x, y) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
            body.Box(new Point3D(x * CubeHalf * 0.8 - 0.04, y * CubeHalf * 0.8 - 0.04, top),
                     new Point3D(x * CubeHalf * 0.8 + 0.04, y * CubeHalf * 0.8 + 0.04, top + 12));
        root.Children.Add(Model(body, Color.FromRgb(0x10, 0x11, 0x13)));

        var faces = new (ScreenPlacement Placement, Vector3D Normal)[]
        {
            (ScreenPlacement.CubeNorth, new Vector3D(0, 1, 0)),
            (ScreenPlacement.CubeEast, new Vector3D(1, 0, 0)),
            (ScreenPlacement.CubeSouth, new Vector3D(0, -1, 0)),
            (ScreenPlacement.CubeWest, new Vector3D(-1, 0, 0)),
        };
        foreach (var (placement, normal) in faces)
        {
            var face = new MeshBuilder();
            var center = new Point3D(normal.X * (CubeHalf + 0.02), normal.Y * (CubeHalf + 0.02), 0);
            face.Screen(center, normal, CubeFaceWidth, CubeBottom, CubeBottom + CubeFaceHeight);
            screens[placement] = ScreenModel(face);
        }
    }

    private static void BuildEndBoard(Model3DGroup root, Dictionary<ScreenPlacement, GeometryModel3D> screens, double endX, double bottom)
    {
        const double width = 14, height = width * 9 / 16;
        var frame = new MeshBuilder();
        frame.Box(new Point3D(endX, -width / 2 - 0.3, bottom - 0.3), new Point3D(endX + 0.8, width / 2 + 0.3, bottom + height + 0.3));
        root.Children.Add(Model(frame, Color.FromRgb(0x10, 0x11, 0x13)));

        var face = new MeshBuilder();
        face.Screen(new Point3D(endX - 0.02, 0, 0), new Vector3D(-1, 0, 0), width, bottom, bottom + height);
        screens[ScreenPlacement.EndBoard] = ScreenModel(face);
    }

    // ---- Helpers ------------------------------------------------------------------------

    /// <summary>The superellipse <paramref name="offset"/> metres outside the rink edge; first point repeated at the end.</summary>
    private static Point[] Ring(double offset)
    {
        double a = RinkA + offset, b = RinkB + offset;
        var points = new Point[Segments + 1];
        for (var i = 0; i <= Segments; i++)
        {
            var t = 2 * Math.PI * i / Segments;
            double c = Math.Cos(t), s = Math.Sin(t);
            points[i] = new Point(
                a * Math.Sign(c) * Math.Pow(Math.Abs(c), 2 / Squareness),
                b * Math.Sign(s) * Math.Pow(Math.Abs(s), 2 / Squareness));
        }
        return points;
    }

    private static Point3D P(Point p, double z) => new(p.X, p.Y, z);

    private static GeometryModel3D Model(MeshBuilder mesh, Color color)
    {
        var material = new DiffuseMaterial(new SolidColorBrush(color));
        material.Freeze();
        // Double-sided: winding mistakes can never make a wall disappear.
        return new GeometryModel3D(mesh.ToMesh(), material) { BackMaterial = material };
    }

    private static GeometryModel3D ScreenModel(MeshBuilder mesh)
    {
        var off = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x08, 0x08, 0x0A)));
        return new GeometryModel3D(mesh.ToMesh(), off);
    }

    private sealed class MeshBuilder
    {
        private readonly MeshGeometry3D _mesh = new();

        public void Triangle(Point3D a, Point ta, Point3D b, Point tb, Point3D c, Point tc)
        {
            var i = _mesh.Positions.Count;
            _mesh.Positions.Add(a); _mesh.Positions.Add(b); _mesh.Positions.Add(c);
            _mesh.TextureCoordinates.Add(ta); _mesh.TextureCoordinates.Add(tb); _mesh.TextureCoordinates.Add(tc);
            _mesh.TriangleIndices.Add(i); _mesh.TriangleIndices.Add(i + 1); _mesh.TriangleIndices.Add(i + 2);
        }

        /// <summary>p0 bottom-left, p1 bottom-right, p2 top-right, p3 top-left as seen from the front.</summary>
        public void Quad(Point3D p0, Point3D p1, Point3D p2, Point3D p3,
            Point? t0 = null, Point? t1 = null, Point? t2 = null, Point? t3 = null)
        {
            var i = _mesh.Positions.Count;
            _mesh.Positions.Add(p0); _mesh.Positions.Add(p1); _mesh.Positions.Add(p2); _mesh.Positions.Add(p3);
            _mesh.TextureCoordinates.Add(t0 ?? new Point(0, 1));
            _mesh.TextureCoordinates.Add(t1 ?? new Point(1, 1));
            _mesh.TextureCoordinates.Add(t2 ?? new Point(1, 0));
            _mesh.TextureCoordinates.Add(t3 ?? new Point(0, 0));
            foreach (var k in new[] { 0, 1, 2, 0, 2, 3 })
                _mesh.TriangleIndices.Add(i + k);
        }

        public void Wall(Point[] ring, double zBottom, double zTop)
        {
            for (var i = 0; i < ring.Length - 1; i++)
                Quad(P(ring[i], zBottom), P(ring[i + 1], zBottom), P(ring[i + 1], zTop), P(ring[i], zTop));
        }

        public void Band(Point[] inner, Point[] outer, double zInner, double zOuter)
        {
            for (var i = 0; i < inner.Length - 1; i++)
                Quad(P(inner[i], zInner), P(inner[i + 1], zInner), P(outer[i + 1], zOuter), P(outer[i], zOuter));
        }

        /// <summary>
        /// Inward-facing LED band. U runs along the perimeter so that, standing on the
        /// ice and looking at any side, the content reads left to right.
        ///
        /// The texture repeats <paramref name="repeat"/> times in the mesh itself (quads
        /// that straddle a seam are split there): WPF can't reliably tile a brush across
        /// a surface this long and thin, and renders it black.
        /// </summary>
        public void RibbonBand(Point[] ring, double zBottom, double zTop, int repeat)
        {
            var lengths = new double[ring.Length];
            for (var i = 1; i < ring.Length; i++)
                lengths[i] = lengths[i - 1] + (ring[i] - ring[i - 1]).Length;
            var total = lengths[^1];

            for (var i = 0; i < ring.Length - 1; i++)
            {
                // w runs from `repeat` down to 0 around the ring; u = w within the current repeat.
                double hi = repeat * (1 - lengths[i] / total), lo = repeat * (1 - lengths[i + 1] / total);
                var seam = Math.Ceiling(lo);
                if (seam > lo && seam < hi)
                {
                    var f = (hi - seam) / (hi - lo);
                    var mid = ring[i] + (ring[i + 1] - ring[i]) * f;
                    Span(ring[i], mid, hi - seam, 0);
                    Span(mid, ring[i + 1], 1, lo - (seam - 1));
                }
                else
                {
                    var origin = Math.Floor(lo);
                    Span(ring[i], ring[i + 1], hi - origin, lo - origin);
                }
            }

            void Span(Point a, Point b, double ua, double ub) =>
                Quad(P(a, zBottom), P(b, zBottom), P(b, zTop), P(a, zTop),
                    new Point(ua, 1), new Point(ub, 1), new Point(ub, 0), new Point(ua, 0));
        }

        /// <summary>A flat 16:9-style screen whose content reads correctly from the side <paramref name="normal"/> points to.</summary>
        public void Screen(Point3D center, Vector3D normal, double width, double zBottom, double zTop)
        {
            var right = Vector3D.CrossProduct(-normal, new Vector3D(0, 0, 1));
            right.Normalize();
            var half = right * (width / 2);
            var c = new Point3D(center.X, center.Y, 0);
            Quad(new Point3D(c.X - half.X, c.Y - half.Y, zBottom),
                 new Point3D(c.X + half.X, c.Y + half.Y, zBottom),
                 new Point3D(c.X + half.X, c.Y + half.Y, zTop),
                 new Point3D(c.X - half.X, c.Y - half.Y, zTop));
        }

        public void Box(Point3D min, Point3D max)
        {
            Point3D V(double x, double y, double z) => new(x, y, z);
            double x0 = min.X, y0 = min.Y, z0 = min.Z, x1 = max.X, y1 = max.Y, z1 = max.Z;
            Quad(V(x0, y0, z0), V(x1, y0, z0), V(x1, y0, z1), V(x0, y0, z1));
            Quad(V(x1, y1, z0), V(x0, y1, z0), V(x0, y1, z1), V(x1, y1, z1));
            Quad(V(x1, y0, z0), V(x1, y1, z0), V(x1, y1, z1), V(x1, y0, z1));
            Quad(V(x0, y1, z0), V(x0, y0, z0), V(x0, y0, z1), V(x0, y1, z1));
            Quad(V(x0, y0, z1), V(x1, y0, z1), V(x1, y1, z1), V(x0, y1, z1));
            Quad(V(x0, y1, z0), V(x1, y1, z0), V(x1, y0, z0), V(x0, y0, z0));
        }

        public MeshGeometry3D ToMesh()
        {
            _mesh.Freeze();
            return _mesh;
        }
    }
}
