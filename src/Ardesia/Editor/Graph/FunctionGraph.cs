using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Ardesia.Editor.Graph;

/// <summary>
/// Grafico di funzione disegnato da Ardesia: tutto vettoriale, quindi nitido a ogni zoom della pagina,
/// e sotto l'inchiostro come gli altri oggetti. Lo stato (funzioni, vista, opzioni) è in <see cref="State"/>.
/// </summary>
public sealed class FunctionGraph : FrameworkElement
{
    static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");
    static readonly Typeface Font = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    static readonly Typeface FontItalic = new(new FontFamily("Cambria Math, Cambria, Segoe UI"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
    static readonly Brush BackBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF)));
    static readonly Pen Border = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8))), 1));
    static readonly Pen GridPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4))), 1));
    static readonly Brush AxisBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)));
    static readonly Pen AxisPen = Frozen(new Pen(AxisBrush, 1.5));
    static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)));
    static readonly Brush ErrorBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)));
    static readonly Brush PositiveBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x38, 0x2E, 0x9D, 0x32)));
    static readonly Brush NegativeBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x38, 0xD3, 0x2F, 0x2F)));
    static readonly Brush LabelBack = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)));
    static readonly Brush PointBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)));
    static readonly Pen WhitePen = Frozen(new Pen(Brushes.White, 1.5));

    GraphState _state = new();
    List<Curve> _curves = new();
    /// <summary>Spazi già occupati da numeri ed etichette, durante il disegno: le etichette dei punti li evitano.</summary>
    readonly List<Rect> _taken = new();

    /// <summary>Una riga del grafico, già interpretata (o con l'errore da mostrare).</summary>
    sealed record Curve(GraphFunctionData Data, ParsedCurve? Parsed, string? Error, Func<double, double[], double>? Eval, Brush Brush);

    public FunctionGraph()
    {
        SnapsToDevicePixels = false;
        ClipToBounds = true;
    }

    public GraphState State
    {
        get => _state;
        set
        {
            _state = value;
            Refresh();
        }
    }

    /// <summary>Da chiamare dopo aver cambiato <see cref="State"/> direttamente: rilegge le funzioni e ridisegna.</summary>
    public void Refresh()
    {
        _curves = new List<Curve>();
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in _state.Functions)
        {
            var brush = Frozen(new SolidColorBrush(ParseColor(f.Color)));
            try
            {
                var parsed = MathParser.ParseLine(MathText.Normalize(f.Text));
                // anche le funzioni nascoste tengono i loro parametri, per ritrovarne i valori quando si rimettono
                foreach (var p in parsed.Parameters) names.Add(p);
                if (!f.Hidden) _curves.Add(new Curve(f, parsed, null, null, brush));
            }
            catch (MathSyntaxException ex)
            {
                if (!f.Hidden) _curves.Add(new Curve(f, null, ex.Message, null, brush));
            }
        }

        // parametri: si tengono i valori già scelti, si aggiungono i nuovi, si tolgono quelli non più usati
        var old = _state.Params;
        _state.Params = names.Select(n => old.FirstOrDefault(p => p.Name == n) ?? new GraphParameter { Name = n }).ToList();
        var paramNames = _state.Params.Select(p => p.Name).ToList();
        _curves = _curves.Select(c => c.Parsed == null ? c : c with { Eval = c.Parsed.Compile(paramNames) }).ToList();
        InvalidateVisual();
    }

    /// <summary>Vista con la stessa unità sui due assi, da −10 a 10 in orizzontale, centrata nell'origine.</summary>
    public void ResetView()
    {
        double w = Size.Width, h = Size.Height;
        double half = 10 * h / w;
        _state.View = new GraphView { Xmin = -10, Xmax = 10, Ymin = -half, Ymax = half };
        InvalidateVisual();
    }

    /// <summary>Ingrandisce (<paramref name="factor"/> &lt; 1) o rimpicciolisce la vista attorno al centro.</summary>
    public void Zoom(double factor)
    {
        var v = _state.View;
        double cx = (v.Xmin + v.Xmax) / 2, cy = (v.Ymin + v.Ymax) / 2;
        double hx = (v.Xmax - v.Xmin) / 2 * factor, hy = (v.Ymax - v.Ymin) / 2 * factor;
        if (hx < 1e-6 || hx > 1e6) return;
        _state.View = new GraphView { Xmin = cx - hx, Xmax = cx + hx, Ymin = cy - hy, Ymax = cy + hy };
        InvalidateVisual();
    }

    Size Size => new(double.IsNaN(Width) ? Math.Max(ActualWidth, 1) : Width, double.IsNaN(Height) ? Math.Max(ActualHeight, 1) : Height);

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        InvalidateVisual();
    }

    // ───────────────────────────── Disegno ─────────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        double w = RenderSize.Width, h = RenderSize.Height;
        if (w < 20 || h < 20) return;
        _taken.Clear();
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var rect = new Rect(0, 0, w, h);
        dc.DrawRectangle(BackBrush, Border, rect);
        dc.PushClip(new RectangleGeometry(rect));

        var v = _state.View;
        if (!(v.Xmax > v.Xmin) || !(v.Ymax > v.Ymin)) v = new GraphView();
        double sx = w / (v.Xmax - v.Xmin), sy = h / (v.Ymax - v.Ymin);
        double X(double x) => (x - v.Xmin) * sx;
        double Y(double y) => (v.Ymax - y) * sy;
        Point P(double x, double y) => new(X(x), Y(y));

        var args = _state.Params.Select(p => p.Value).ToArray();
        var functions = _curves.Where(c => c.Eval != null && c.Parsed!.Kind == CurveKind.Function)
            .Select(c => (Curve: c, F: (Func<double, double>)(x => c.Eval!(x, args)))).ToList();
        var verticals = _curves.Where(c => c.Eval != null && c.Parsed!.Kind == CurveKind.Vertical)
            .Select(c => (Curve: c, K: c.Eval!(0, args))).Where(t => double.IsFinite(t.K)).ToList();
        int samples = (int)Math.Clamp(w * 1.5, 200, 4000);

        // passi delle tacche
        double stepX = _state.Show.Pi ? GraphMath.PiStep(v.Xmax - v.Xmin, w) : GraphMath.NiceStep(v.Xmax - v.Xmin, w);
        double stepY = GraphMath.NiceStep(v.Ymax - v.Ymin, h, 55);

        // griglia
        if (_state.Show.Grid)
        {
            foreach (var t in Ticks(v.Xmin, v.Xmax, stepX)) dc.DrawLine(GridPen, new Point(X(t), 0), new Point(X(t), h));
            foreach (var t in Ticks(v.Ymin, v.Ymax, stepY)) dc.DrawLine(GridPen, new Point(0, Y(t)), new Point(w, Y(t)));
        }

        // segno: zone colorate tra la curva e l'asse x, verde dove f > 0, rossa dove f < 0
        var sampled = functions.Select(f => (f.Curve, f.F, Parts: GraphMath.Sample(f.F, v.Xmin, v.Xmax, v.Ymin, v.Ymax, samples))).ToList();
        if (_state.Show.Sign)
        {
            double axis = Math.Clamp(Y(0), -1, h + 1);
            foreach (var s in sampled)
            {
                foreach (var part in s.Parts)
                {
                    var geo = new StreamGeometry();
                    using (var g = geo.Open())
                    {
                        g.BeginFigure(new Point(X(part[0].X), axis), true, true);
                        g.PolyLineTo(part.Select(p => P(p.X, p.Y)).ToList(), false, false);
                        g.LineTo(new Point(X(part[^1].X), axis), false, false);
                    }
                    geo.Freeze();
                    dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, Math.Max(0, axis))));
                    dc.DrawGeometry(PositiveBrush, null, geo);
                    dc.Pop();
                    dc.PushClip(new RectangleGeometry(new Rect(0, Math.Max(0, axis), w, Math.Max(0, h - axis))));
                    dc.DrawGeometry(NegativeBrush, null, geo);
                    dc.Pop();
                }
            }
        }

        DrawAxes(dc, w, h, v, X, Y, stepX, stepY, ppd);

        // rette verticali
        foreach (var (curve, k) in verticals)
        {
            var pen = Frozen(new Pen(curve.Brush, 2.5));
            dc.DrawLine(pen, new Point(X(k), 0), new Point(X(k), h));
        }

        // curve (e derivate tratteggiate)
        foreach (var s in sampled)
        {
            var pen = Frozen(new Pen(s.Curve.Brush, 2.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
            DrawParts(dc, pen, s.Parts, P);
            if (_state.Show.Derivative)
            {
                var d = GraphMath.Derivative(s.F);
                var dashed = Frozen(new Pen(s.Curve.Brush, 2) { DashStyle = new DashStyle(new[] { 3.0, 2.5 }, 0), LineJoin = PenLineJoin.Round });
                DrawParts(dc, dashed, GraphMath.Sample(d, v.Xmin, v.Xmax, v.Ymin, v.Ymax, samples), P);
            }
        }

        // punti notevoli
        var points = new List<(Point At, Brush Brush, string Label, bool Above)>();
        bool pi = _state.Show.Pi;
        double yRange = v.Ymax - v.Ymin;
        bool Visible(double x, double y) => x >= v.Xmin && x <= v.Xmax && y >= v.Ymin && y <= v.Ymax;
        if (_state.Show.Zeros)
            foreach (var (curve, f) in functions)
                foreach (var z in GraphMath.Zeros(f, v.Xmin, v.Xmax))
                    if (Visible(z, 0)) points.Add((P(z, 0), curve.Brush, GraphMath.FormatPoint(z, 0, pi), false));
        if (_state.Show.Extrema)
            foreach (var (curve, f) in functions)
                foreach (var e in GraphMath.Extrema(f, v.Xmin, v.Xmax, yRange))
                    if (Visible(e.X, e.Y)) points.Add((P(e.X, e.Y), curve.Brush, (e.IsMax ? "max " : "min ") + GraphMath.FormatPoint(e.X, e.Y, pi), e.IsMax));
        if (_state.Show.Intersections)
        {
            for (int i = 0; i < functions.Count; i++)
            {
                var fi = functions[i].F;
                double y0 = fi(0);
                if (double.IsFinite(y0) && Visible(0, y0)) points.Add((P(0, y0), PointBrush, GraphMath.FormatPoint(0, y0, pi), true));
                for (int j = i + 1; j < functions.Count; j++)
                {
                    var fj = functions[j].F;
                    foreach (var x in GraphMath.Zeros(t => fi(t) - fj(t), v.Xmin, v.Xmax))
                        if (fi(x) is var y && double.IsFinite(y) && Visible(x, y)) points.Add((P(x, y), PointBrush, GraphMath.FormatPoint(x, y, pi), true));
                }
                foreach (var (_, k) in verticals)
                    if (fi(k) is var y && double.IsFinite(y) && Visible(k, y)) points.Add((P(k, y), PointBrush, GraphMath.FormatPoint(k, y, pi), true));
            }
        }
        // stesso punto trovato due volte (zero che è anche intersezione con l'asse y…): una volta sola
        var unique = new List<(Point At, Brush Brush, string Label, bool Above)>();
        foreach (var pt in points)
            if (!unique.Any(u => (u.At - pt.At).Length < 3 && u.Label.EndsWith(pt.Label))) unique.Add(pt);
        foreach (var pt in unique.Take(40))
        {
            dc.DrawEllipse(pt.Brush, WhitePen, pt.At, 5, 5);
            _taken.Add(new Rect(pt.At.X - 6, pt.At.Y - 6, 12, 12));
        }

        var legend = DrawLegend(dc, w, ppd);
        if (!legend.IsEmpty) _taken.Add(legend);
        foreach (var (at, _, label, above) in unique.Take(40))
        {
            var ft = Text(label, 13, PointBrush, ppd);
            // si prova accanto al punto in più posizioni; se sono tutte occupate l'etichetta non si scrive
            var spots = new List<Point>
            {
                above ? new Point(at.X + 7, at.Y - ft.Height - 5) : new Point(at.X + 7, at.Y + 6),
                above ? new Point(at.X + 7, at.Y + 6) : new Point(at.X + 7, at.Y - ft.Height - 5),
                new Point(at.X - ft.Width - 7, above ? at.Y - ft.Height - 5 : at.Y + 6),
                new Point(at.X - ft.Width - 7, above ? at.Y + 6 : at.Y - ft.Height - 5),
                new Point(at.X - ft.Width / 2, at.Y + 9),
                new Point(at.X - ft.Width / 2, at.Y - ft.Height - 9),
                new Point(at.X + 7, at.Y + ft.Height + 8),
                new Point(at.X + 7, at.Y - 2 * ft.Height - 8),
            };
            foreach (var spot in spots)
            {
                var r = new Rect(Math.Clamp(spot.X, 2, Math.Max(2, w - ft.Width - 2)) - 2, Math.Clamp(spot.Y, 2, Math.Max(2, h - ft.Height - 2)),
                    ft.Width + 4, ft.Height);
                if (_taken.Any(t => t.IntersectsWith(r))) continue;
                _taken.Add(r);
                dc.DrawRoundedRectangle(LabelBack, null, r, 3, 3);
                dc.DrawText(ft, new Point(r.X + 2, r.Y));
                break;
            }
        }
        dc.Pop();
    }

    static void DrawParts(DrawingContext dc, Pen pen, List<List<Point>> parts, Func<double, double, Point> map)
    {
        if (parts.Count == 0) return;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            foreach (var part in parts)
            {
                g.BeginFigure(map(part[0].X, part[0].Y), false, false);
                g.PolyLineTo(part.Skip(1).Select(p => map(p.X, p.Y)).ToList(), true, true);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    void DrawAxes(DrawingContext dc, double w, double h, GraphView v, Func<double, double> X, Func<double, double> Y,
        double stepX, double stepY, double ppd)
    {
        double ax = Y(0), ay = X(0);
        bool xAxis = ax >= 0 && ax <= h, yAxis = ay >= 0 && ay <= w;
        // se un asse è fuori dalla vista, i numeri stanno sul bordo
        double labelRow = Math.Clamp(ax, 0, h - 18);
        double labelCol = Math.Clamp(ay, 30, w);

        if (xAxis)
        {
            dc.DrawLine(AxisPen, new Point(0, ax), new Point(w - 1, ax));
            Arrow(dc, new Point(w - 1, ax), new Vector(1, 0));
            var name = Text("x", 17, AxisBrush, ppd, italic: true);
            dc.DrawText(name, new Point(w - name.Width - 6, ax - name.Height - 2));
        }
        if (yAxis)
        {
            dc.DrawLine(AxisPen, new Point(ay, h), new Point(ay, 1));
            Arrow(dc, new Point(ay, 1), new Vector(0, -1));
            var name = Text("y", 17, AxisBrush, ppd, italic: true);
            dc.DrawText(name, new Point(ay + 7, 0));
        }

        // tacche e numeri sull'asse x
        foreach (var t in Ticks(v.Xmin, v.Xmax, stepX))
        {
            if (Math.Abs(t) < stepX * 1e-6) continue;
            double px = X(t);
            if (px < 8 || px > w - 24) continue;
            if (xAxis) dc.DrawLine(AxisPen, new Point(px, ax - 4), new Point(px, ax + 4));
            var label = (_state.Show.Pi ? GraphMath.FormatPi(t) : null) ?? GraphMath.Format(t, stepX);
            var ft = Text(label, 13, LabelBrush, ppd);
            dc.DrawText(ft, new Point(px - ft.Width / 2, labelRow + 5));
            _taken.Add(new Rect(px - ft.Width / 2, labelRow + 5, ft.Width, ft.Height));
        }
        // tacche e numeri sull'asse y
        foreach (var t in Ticks(v.Ymin, v.Ymax, stepY))
        {
            if (Math.Abs(t) < stepY * 1e-6) continue;
            double py = Y(t);
            if (py < 20 || py > h - 8) continue;
            if (yAxis) dc.DrawLine(AxisPen, new Point(ay - 4, py), new Point(ay + 4, py));
            var ft = Text(GraphMath.Format(t, stepY), 13, LabelBrush, ppd);
            dc.DrawText(ft, new Point(labelCol - ft.Width - 6, py - ft.Height / 2));
            _taken.Add(new Rect(labelCol - ft.Width - 6, py - ft.Height / 2, ft.Width, ft.Height));
        }
        if (xAxis && yAxis)
        {
            var o = Text("O", 13, LabelBrush, ppd, italic: true);
            dc.DrawText(o, new Point(ay - o.Width - 5, ax + 3));
        }
    }

    static void Arrow(DrawingContext dc, Point tip, Vector dir)
    {
        var n = new Vector(-dir.Y, dir.X);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(tip, true, true);
            g.LineTo(tip - dir * 10 + n * 4.5, true, false);
            g.LineTo(tip - dir * 10 - n * 4.5, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(AxisBrush, null, geo);
    }

    /// <summary>Legenda in alto a sinistra: le funzioni come le ha scritte il docente, gli errori in rosso, i parametri.</summary>
    Rect DrawLegend(DrawingContext dc, double w, double ppd)
    {
        var lines = new List<(FormattedText Text, Brush? Swatch, bool Dashed)>();
        foreach (var c in _curves)
        {
            var text = c.Data.Text.Trim();
            if (c.Error != null)
            {
                lines.Add((Text($"{text}   ⚠ {c.Error}", 15, ErrorBrush, ppd), null, false));
                continue;
            }
            if (!text.Contains('=')) text = "y = " + text;
            lines.Add((Text(text, 16, c.Brush, ppd, italic: true), c.Brush, false));
            if (_state.Show.Derivative && c.Parsed!.Kind == CurveKind.Function)
                lines.Add((Text(c.Parsed.Name != null ? $"{c.Parsed.Name}′(x)" : $"derivata di {text}", 15, c.Brush, ppd, italic: true), c.Brush, true));
        }
        foreach (var p in _state.Params)
            lines.Add((Text($"{p.Name} = {GraphMath.Format(p.Value, p.Step)}", 15, LabelBrush, ppd, italic: true), null, false));
        if (lines.Count == 0) return Rect.Empty;

        double width = lines.Max(l => l.Text.Width + (l.Swatch != null ? 30 : 0)) + 16;
        double height = lines.Sum(l => l.Text.Height + 2) + 10;
        width = Math.Min(width, w - 16);
        var box = new Rect(8, 8, width, height);
        dc.DrawRoundedRectangle(LabelBack, Border, box, 5, 5);
        double y = 13;
        foreach (var (ft, swatch, dashed) in lines)
        {
            double x = 16;
            if (swatch != null)
            {
                var pen = new Pen(swatch, 3);
                if (dashed) pen.DashStyle = new DashStyle(new[] { 2.0, 1.5 }, 0);
                dc.DrawLine(pen, new Point(x, y + ft.Height / 2), new Point(x + 22, y + ft.Height / 2));
                x += 30;
            }
            ft.MaxTextWidth = Math.Max(10, width - (x - 8) - 6);
            ft.Trimming = TextTrimming.CharacterEllipsis;
            ft.MaxLineCount = 1;
            dc.DrawText(ft, new Point(x, y));
            y += ft.Height + 2;
        }
        return box;
    }

    static IEnumerable<double> Ticks(double min, double max, double step)
    {
        if (!(step > 0) || (max - min) / step > 500) yield break;
        for (double t = Math.Ceiling(min / step) * step; t <= max; t += step) yield return Math.Round(t / step) * step;
    }

    static FormattedText Text(string s, double size, Brush brush, double ppd, bool italic = false) =>
        new(s, It, FlowDirection.LeftToRight, italic ? FontItalic : Font, size, brush, ppd);

    static Color ParseColor(string s)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(s);
        }
        catch
        {
            return Color.FromRgb(0x1E, 0x5A, 0xC8);
        }
    }

    static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
