using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Ardesia.Editor;

/// <summary>
/// Puntatore laser: una scia bianca con un alone rosso che non resta sulla pagina.
/// Le scie fatte una dopo l'altra (un cerchio, poi una sottolineatura) restano insieme
/// e svaniscono tutte poco dopo che si è alzata la penna; una scia nuova le fa ricomparire.
/// </summary>
sealed class LaserPointer : FrameworkElement
{
    static readonly TimeSpan Hold = TimeSpan.FromSeconds(1.2), Fade = TimeSpan.FromSeconds(0.6);
    // Dall'esterno verso l'interno: alone largo e tenue, alone rosso, anima bianca (spessori sullo schermo).
    static readonly (Color Color, double Width)[] Layers =
    {
        (Color.FromArgb(0x38, 0xFF, 0x20, 0x20), 22),
        (Color.FromArgb(0x70, 0xFF, 0x18, 0x18), 13),
        (Color.FromArgb(0xFF, 0xF0, 0x10, 0x10), 7),
        (Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 3),
    };

    readonly List<List<Point>> _trails = new();
    List<Point>? _current;
    int _fadeId;

    public LaserPointer()
    {
        IsHitTestVisible = false;
    }

    /// <summary>Ingrandimento della pagina: la scia ha sempre lo stesso spessore sullo schermo.</summary>
    public double Zoom { get; set; } = 1;

    public bool IsDrawing => _current != null;

    public void Begin(Point p)
    {
        _fadeId++;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        _current = new List<Point> { p };
        _trails.Add(_current);
        InvalidateVisual();
    }

    public void Add(Point p)
    {
        if (_current == null) return;
        var last = _current[^1];
        if ((p - last).Length * Zoom < 1.5) return;
        _current.Add(p);
        InvalidateVisual();
    }

    /// <summary>Penna alzata: fra poco la scia svanisce.</summary>
    public void End()
    {
        if (_current == null) return;
        _current = null;
        InvalidateVisual();
        int id = ++_fadeId;
        var anim = new DoubleAnimation(1, 0, new Duration(Fade)) { BeginTime = Hold };
        anim.Completed += (_, _) =>
        {
            if (id != _fadeId) return; // nel frattempo è cominciata un'altra scia
            Clear();
        };
        BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>Toglie subito ogni scia (cambio di pagina o di strumento).</summary>
    public void Clear()
    {
        _fadeId++;
        _current = null;
        _trails.Clear();
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_trails.Count == 0) return;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
            foreach (var t in _trails) AddSmooth(ctx, t);
        geometry.Freeze();

        double k = 1 / Math.Max(Zoom, 0.01);
        foreach (var (color, width) in Layers)
        {
            var pen = new Pen(new SolidColorBrush(color), width * k)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            pen.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }

        // La punta, mentre si disegna: un punto luminoso un po' più grande della scia.
        if (_current != null)
        {
            var tip = _current[^1];
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0x20, 0x20)), null, tip, 16 * k, 16 * k);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xF0, 0x10, 0x10)), null, tip, 6 * k, 6 * k);
            dc.DrawEllipse(Brushes.White, null, tip, 3 * k, 3 * k);
        }
    }

    /// <summary>Spezzata addolcita: curve che passano per i punti di mezzo dei segmenti.</summary>
    static void AddSmooth(StreamGeometryContext ctx, List<Point> pts)
    {
        ctx.BeginFigure(pts[0], false, false);
        if (pts.Count == 1)
        {
            ctx.LineTo(pts[0], true, true); // un tocco solo: un punto
            return;
        }
        for (int i = 1; i < pts.Count - 1; i++)
        {
            var mid = new Point((pts[i].X + pts[i + 1].X) / 2, (pts[i].Y + pts[i + 1].Y) / 2);
            ctx.QuadraticBezierTo(pts[i], mid, true, true);
        }
        ctx.LineTo(pts[^1], true, true);
    }
}
