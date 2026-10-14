using System.Globalization;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

namespace Ardesia.Editor;

/// <summary>Base per righello e goniometro: si trascinano col dito/penna/mouse e si ruotano con la maniglia o con due dita.</summary>
public abstract class DraggableTool : FrameworkElement
{
    protected const double Mm = 96.0 / 25.4;
    protected const double HandleRadius = 18;
    static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    readonly RotateTransform _rotate = new();
    readonly TranslateTransform _translate = new();
    enum Mode { None, Move, Rotate }
    Mode _mode;
    Point _last;
    double _grabAngle;

    protected DraggableTool()
    {
        var g = new TransformGroup();
        g.Children.Add(_rotate);
        g.Children.Add(_translate);
        RenderTransform = g;
        IsManipulationEnabled = true;
        Cursor = Cursors.SizeAll;
        Visibility = Visibility.Collapsed;
        Focusable = false;
    }

    public Point Center { get; private set; }

    /// <summary>Rotazione in gradi (senso orario, come in WPF).</summary>
    public double Angle { get; private set; }

    protected abstract Point RotateHandle { get; }

    public void SetPose(Point center, double angle)
    {
        angle %= 360;
        if (angle < 0) angle += 360;
        bool rotated = Math.Abs(angle - Angle) > 1e-9;
        Center = center;
        Angle = angle;
        _rotate.Angle = angle;
        _translate.X = center.X;
        _translate.Y = center.Y;
        if (rotated) InvalidateVisual();
    }

    public Point ToLocal(Point world)
    {
        var v = world - Center;
        double r = -Angle * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        return new Point(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    public Point ToWorld(Point local)
    {
        double r = Angle * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        return new Point(Center.X + local.X * c - local.Y * s, Center.Y + local.X * s + local.Y * c);
    }

    /// <summary>Angolo "matematico" della linea, 0–179°.</summary>
    protected double DisplayAngle => Math.Round(((-Angle % 180) + 180) % 180) % 180;

    IInputElement Container => (IInputElement)VisualTreeHelper.GetParent(this);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var world = e.GetPosition(Container);
        _last = world;
        if ((ToLocal(world) - RotateHandle).Length <= HandleRadius + 6)
        {
            _mode = Mode.Rotate;
            _grabAngle = AngleOf(world) - Angle;
        }
        else
        {
            _mode = Mode.Move;
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_mode == Mode.None) return;
        var world = e.GetPosition(Container);
        if (_mode == Mode.Move) SetPose(Center + (world - _last), Angle);
        else SetPose(Center, Snap(AngleOf(world) - _grabAngle));
        _last = world;
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _mode = Mode.None;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _mode = Mode.None;
        base.OnLostMouseCapture(e);
    }

    protected override void OnManipulationStarting(ManipulationStartingEventArgs e)
    {
        e.ManipulationContainer = Container;
        e.Mode = ManipulationModes.Translate | ManipulationModes.Rotate;
        e.Handled = true;
    }

    protected override void OnManipulationDelta(ManipulationDeltaEventArgs e)
    {
        var d = e.DeltaManipulation;
        var o = e.ManipulationOrigin;
        double r = d.Rotation * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        var v = Center - o;
        var rv = new Vector(v.X * c - v.Y * s, v.X * s + v.Y * c);
        SetPose(o + rv + d.Translation, Angle + d.Rotation);
        e.Handled = true;
    }

    protected override void OnManipulationInertiaStarting(ManipulationInertiaStartingEventArgs e)
    {
        e.TranslationBehavior.DesiredDeceleration = 10;
        e.RotationBehavior.DesiredDeceleration = 10;
        e.Handled = true;
    }

    double AngleOf(Point world) => Math.Atan2(world.Y - Center.Y, world.X - Center.X) * 180 / Math.PI;

    static double Snap(double a)
    {
        double n = Math.Round(a / 15) * 15;
        return Math.Abs(n - a) < 2 ? n : a;
    }

    protected FormattedText Text(string s, double size, Brush brush, bool bold = false) =>
        new(s, It, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected void DrawCentered(DrawingContext dc, FormattedText ft, Point center) =>
        dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));

    protected void DrawRotateHandle(DrawingContext dc)
    {
        var h = RotateHandle;
        dc.DrawEllipse(Paint.HandleFill, null, h, HandleRadius, HandleRadius);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(h.X + 8, h.Y), false, false);
            ctx.ArcTo(new Point(h.X, h.Y - 8), new Size(8, 8), 0, true, SweepDirection.Clockwise, true, false);
            ctx.BeginFigure(new Point(h.X + 4, h.Y - 12), false, false);
            ctx.LineTo(new Point(h.X, h.Y - 8), true, false);
            ctx.LineTo(new Point(h.X + 4, h.Y - 4), true, false);
        }
        dc.DrawGeometry(null, Paint.WhitePen, g);
    }
}

static class Paint
{
    public static readonly Brush HandleFill = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0x1E, 0x5A, 0x68)));
    public static readonly Brush ToolBody = Freeze(new SolidColorBrush(Color.FromArgb(0xD0, 0xFB, 0xFB, 0xF4)));
    public static readonly Brush Ink = Freeze(new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26)));
    public static readonly Brush SubtleInk = Freeze(new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70)));
    public static readonly Brush Accent = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x5A, 0x68)));
    public static readonly Brush LabelBg = Freeze(new SolidColorBrush(Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF)));
    public static readonly Pen Outline = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0x50, 0x50, 0x50)), 1));
    public static readonly Pen Tick = Freeze(new Pen(Ink, 1));
    public static readonly Pen ThinTick = Freeze(new Pen(SubtleInk, 0.8));
    public static readonly Pen WhitePen = Freeze(new Pen(Brushes.White, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });

    public static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    public static Pen RoundPen(Color c, double w) =>
        Freeze(new Pen(new SolidColorBrush(c), w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
}

/// <summary>Righello da 30 cm. I tratti tracciati lungo un bordo diventano segmenti perfettamente dritti.</summary>
public sealed class RulerTool : DraggableTool
{
    const double Inset = 14;
    public double Length { get; } = 300 * Mm;
    public double Thickness { get; } = 24 * Mm;

    protected override Point RotateHandle => new(Length / 2 - 34, 0);

    protected override void OnRender(DrawingContext dc)
    {
        double l = Length / 2, t = Thickness / 2;
        dc.DrawRoundedRectangle(Paint.ToolBody, Paint.Outline, new Rect(-l, -t, Length, Thickness), 6, 6);

        int count = (int)((Length - 2 * Inset) / Mm);
        for (int i = 0; i <= count; i++)
        {
            double x = -l + Inset + i * Mm;
            double len = i % 10 == 0 ? 16 : i % 5 == 0 ? 11 : 6;
            dc.DrawLine(Paint.Tick, new Point(x, -t), new Point(x, -t + len));
            dc.DrawLine(Paint.ThinTick, new Point(x, t), new Point(x, t - len * 0.7));
            if (i % 10 == 0)
                DrawCentered(dc, Text((i / 10).ToString(CultureInfo.InvariantCulture), 12, Paint.Ink), new Point(x, -t + 26));
        }

        DrawCentered(dc, Text($"{DisplayAngle:0}°", 16, Paint.Accent, bold: true), new Point(0, 8));
        DrawRotateHandle(dc);
    }

    /// <summary>Se il tratto è stato tracciato lungo un bordo del righello, restituisce il segmento dritto equivalente.</summary>
    public Stroke? TrySnap(Stroke s)
    {
        if (Visibility != Visibility.Visible) return null;
        var pts = s.StylusPoints;
        if (pts.Count < 2) return null;
        double l = Length / 2, t = Thickness / 2;

        foreach (int side in new[] { -1, 1 })
        {
            double edge = side * t;
            int near = 0;
            double minX = double.MaxValue, maxX = double.MinValue;
            foreach (var sp in pts)
            {
                var lp = ToLocal(new Point(sp.X, sp.Y));
                double outward = (lp.Y - edge) * side;
                if (outward > -6 && outward < 32 && lp.X > -l - 20 && lp.X < l + 20)
                {
                    near++;
                    minX = Math.Min(minX, lp.X);
                    maxX = Math.Max(maxX, lp.X);
                }
            }
            if (near < pts.Count * 0.85 || maxX - minX < 10) continue;

            var da = s.DrawingAttributes.Clone();
            da.FitToCurve = false;
            da.IgnorePressure = true;
            double y = edge + side * (da.Height / 2 + 0.5);
            minX = Math.Max(minX, -l);
            maxX = Math.Min(maxX, l);
            var a = ToWorld(new Point(minX, y));
            var b = ToWorld(new Point(maxX, y));
            return new Stroke(new StylusPointCollection(new[] { new StylusPoint(a.X, a.Y), new StylusPoint(b.X, b.Y) }), da);
        }
        return null;
    }
}

/// <summary>Goniometro semicircolare con doppia scala 0–180°.</summary>
public sealed class ProtractorTool : DraggableTool
{
    public double Radius { get; } = 80 * Mm;

    protected override Point RotateHandle => new(0, -Radius * 0.42);

    protected override void OnRender(DrawingContext dc)
    {
        double r = Radius, outer = r + 6;
        var body = new StreamGeometry();
        using (var ctx = body.Open())
        {
            ctx.BeginFigure(new Point(-outer, 22), true, true);
            ctx.LineTo(new Point(outer, 22), true, false);
            ctx.LineTo(new Point(outer, 0), true, false);
            ctx.ArcTo(new Point(-outer, 0), new Size(outer, outer), 0, false, SweepDirection.Counterclockwise, true, false);
        }
        body.Freeze();
        dc.DrawGeometry(Paint.ToolBody, Paint.Outline, body);

        for (int d = 0; d <= 180; d++)
        {
            double rad = d * Math.PI / 180;
            var dir = new Vector(Math.Cos(rad), -Math.Sin(rad));
            double len = d % 10 == 0 ? 18 : d % 5 == 0 ? 12 : 7;
            dc.DrawLine(d % 5 == 0 ? Paint.Tick : Paint.ThinTick, (Point)(dir * r), (Point)(dir * (r - len)));
            if (d % 10 == 0)
            {
                DrawCentered(dc, Text(d.ToString(CultureInfo.InvariantCulture), 12, Paint.Ink), (Point)(dir * (r - 31)));
                DrawCentered(dc, Text((180 - d).ToString(CultureInfo.InvariantCulture), 10, Paint.SubtleInk), (Point)(dir * (r - 52)));
            }
        }

        double inner = r - 66;
        var arc = new StreamGeometry();
        using (var ctx = arc.Open())
        {
            ctx.BeginFigure(new Point(inner, 0), false, false);
            ctx.ArcTo(new Point(-inner, 0), new Size(inner, inner), 0, false, SweepDirection.Counterclockwise, true, false);
        }
        arc.Freeze();
        dc.DrawGeometry(null, Paint.ThinTick, arc);

        dc.DrawLine(Paint.Tick, new Point(-r, 0), new Point(r, 0));
        dc.DrawLine(Paint.Tick, new Point(0, 0), new Point(0, -16));
        dc.DrawEllipse(null, Paint.Tick, new Point(0, 0), 3, 3);

        DrawCentered(dc, Text($"{DisplayAngle:0}°", 14, Paint.Accent, bold: true), new Point(0, 11));
        DrawRotateHandle(dc);
    }
}

/// <summary>
/// Compasso. Trascina la punta (centro) per spostarlo, la matita per cambiare raggio,
/// la manopola o un punto qualsiasi della pagina per ruotarlo e tracciare l'arco.
/// </summary>
public sealed class CompassTool : FrameworkElement
{
    public enum Part { None, Needle, Pencil, Knob, Body }

    static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");
    static readonly Pen Guide = Paint.Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0x1E, 0x5A, 0x68)), 1) { DashStyle = DashStyles.Dash });
    static readonly Pen NeedleLeg = Paint.RoundPen(Color.FromRgb(0x78, 0x90, 0x9C), 8);
    static readonly Pen PencilLeg = Paint.RoundPen(Color.FromRgb(0x54, 0x6E, 0x7A), 8);
    static readonly Pen Needle = Paint.RoundPen(Color.FromRgb(0x26, 0x32, 0x38), 2);
    static readonly Pen Wood = Paint.RoundPen(Color.FromRgb(0xE3, 0xB5, 0x6E), 7);
    static readonly Pen HandlePen = Paint.Freeze(new Pen(Paint.Accent, 1.5));
    static readonly Brush HandleFill = Paint.Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0x1E, 0x5A, 0x68)));
    static readonly Brush HingeFill = Paint.Freeze(new SolidColorBrush(Color.FromRgb(0x45, 0x5A, 0x64)));

    Color _pencilColor = Colors.Black;

    public CompassTool()
    {
        Visibility = Visibility.Collapsed;
        Focusable = false;
        Cursor = Cursors.SizeAll;
    }

    public Point Center { get; private set; }
    public double Radius { get; private set; } = 150;
    public double AngleRad { get; private set; }

    public Color PencilColor
    {
        get => _pencilColor;
        set { _pencilColor = value; InvalidateVisual(); }
    }

    public Point PencilPoint => Center + new Vector(Math.Cos(AngleRad), Math.Sin(AngleRad)) * Radius;
    Vector Normal => new(Math.Sin(AngleRad), -Math.Cos(AngleRad));
    double LegHeight => Math.Clamp(Radius * 0.9, 90, 380);
    public Point Hinge => Center + (PencilPoint - Center) / 2 + Normal * LegHeight;
    public Point Knob => Hinge + Normal * 34;

    public void Set(Point center, double radius, double angle)
    {
        Center = center;
        Radius = Math.Max(12, radius);
        AngleRad = angle;
        InvalidateVisual();
    }

    public Part HitPart(Point p)
    {
        if (Visibility != Visibility.Visible) return Part.None;
        if ((p - Center).Length <= 22) return Part.Needle;
        if ((p - PencilPoint).Length <= 22) return Part.Pencil;
        if ((p - Knob).Length <= 20) return Part.Knob;
        if ((p - Hinge).Length <= 14 || DistToSegment(p, Hinge, Center) <= 9 || DistToSegment(p, Hinge, PencilPoint) <= 9)
            return Part.Body;
        return Part.None;
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters h) =>
        HitPart(h.HitPoint) != Part.None ? new PointHitTestResult(this, h.HitPoint) : null;

    protected override void OnRender(DrawingContext dc)
    {
        var c = Center;
        var p = PencilPoint;
        var h = Hinge;

        dc.DrawEllipse(null, Guide, c, Radius, Radius);

        var dn = c - h;
        dn.Normalize();
        var needleStart = c - dn * 16;
        dc.DrawLine(NeedleLeg, h, needleStart);
        dc.DrawLine(Needle, needleStart, c);

        var dp = p - h;
        dp.Normalize();
        var pencilStart = p - dp * 30;
        dc.DrawLine(PencilLeg, h, pencilStart);
        dc.DrawLine(Wood, pencilStart, p - dp * 8);
        dc.DrawLine(Paint.RoundPen(_pencilColor, 3.5), p - dp * 9, p);

        dc.DrawLine(PencilLeg, h, Knob);
        dc.DrawEllipse(HingeFill, null, h, 10, 10);
        dc.DrawEllipse(Paint.HandleFill, Paint.WhitePen, Knob, 14, 14);

        dc.DrawEllipse(HandleFill, HandlePen, c, 16, 16);
        dc.DrawEllipse(HandleFill, HandlePen, p, 16, 16);

        double cm = Radius / (96 / 2.54);
        var ft = new FormattedText($"r = {cm.ToString("0.0", It)} cm", It, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 13, Paint.Accent, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var mid = c + (p - c) / 2 - Normal * 24;
        var rect = new Rect(mid.X - ft.Width / 2 - 6, mid.Y - ft.Height / 2 - 2, ft.Width + 12, ft.Height + 4);
        dc.DrawRoundedRectangle(Paint.LabelBg, null, rect, 4, 4);
        dc.DrawText(ft, new Point(rect.X + 6, rect.Y + 2));
    }

    static double DistToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        double len2 = ab.LengthSquared;
        if (len2 < 1e-9) return (p - a).Length;
        double t = Math.Clamp(Vector.Multiply(p - a, ab) / len2, 0, 1);
        return (p - (a + ab * t)).Length;
    }
}
