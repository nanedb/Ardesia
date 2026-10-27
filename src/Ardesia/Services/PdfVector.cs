using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ardesia.Editor;

namespace Ardesia.Services;

/// <summary>Un oggetto PDF di una pagina; nel dizionario «@n» è un riferimento all'oggetto n della stessa pagina.</summary>
public sealed record PdfObject(string Dict, byte[]? Stream);

/// <summary>
/// Una pagina del PDF già pronta, con numeri di oggetto locali: si numera solo quando si monta il file, così la stessa
/// pagina si può tenere da parte (cache) e rimettere in un PDF nuovo senza rifarla.
/// </summary>
public sealed class PdfPagePart
{
    const int Magic = 0x41504731; // «APG1»

    public double WidthPt { get; init; }
    public double HeightPt { get; init; }
    public int Contents { get; init; }
    public int Resources { get; init; }
    public List<PdfObject> Objects { get; init; } = new();

    public void Save(Stream s)
    {
        using var w = new BinaryWriter(s, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(WidthPt);
        w.Write(HeightPt);
        w.Write(Contents);
        w.Write(Resources);
        w.Write(Objects.Count);
        foreach (var o in Objects)
        {
            w.Write(o.Dict);
            w.Write(o.Stream?.Length ?? -1);
            if (o.Stream != null) w.Write(o.Stream);
        }
    }

    public static PdfPagePart Load(Stream s)
    {
        using var r = new BinaryReader(s, Encoding.UTF8, leaveOpen: true);
        if (r.ReadInt32() != Magic) throw new InvalidDataException("Pagina PDF in cache non valida.");
        double w = r.ReadDouble(), h = r.ReadDouble();
        int contents = r.ReadInt32(), resources = r.ReadInt32(), n = r.ReadInt32();
        var objects = new List<PdfObject>(n);
        for (int i = 0; i < n; i++)
        {
            var dict = r.ReadString();
            int len = r.ReadInt32();
            objects.Add(new PdfObject(dict, len < 0 ? null : r.ReadBytes(len)));
        }
        return new PdfPagePart { WidthPt = w, HeightPt = h, Contents = contents, Resources = resources, Objects = objects };
    }
}

/// <summary>
/// Trasforma ciò che WPF disegna (l'albero visuale della pagina) in una pagina PDF vettoriale: tratti, testi (come
/// contorni), sfondi a quadretti e grana della matita (motivi ripetuti) restano linee e curve; solo le immagini sono
/// immagini. Le parti che non sa tradurre (sfumature, effetti) diventano un'immagine a doppia risoluzione.
/// </summary>
public sealed class PdfVectorPage
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Flusso di contenuto in scrittura (la pagina, un gruppo trasparente o un motivo).</summary>
    sealed class Content
    {
        public readonly StringBuilder Sb = new();
        public Matrix Ctm;          // dalla pagina (punti, y verso l'alto) allo spazio corrente
        public Matrix Base;         // lo spazio predefinito di questo flusso, per le matrici dei motivi
        public readonly Stack<Matrix> Saved = new();
    }

    readonly List<PdfObject?> _objects = new();
    readonly int _resources;
    readonly SortedDictionary<string, string> _xobjects = new(), _patterns = new(), _states = new();
    readonly Dictionary<(ImageSource, int, int), string> _images = new();
    readonly Dictionary<string, string> _stateNames = new(), _patternNames = new();
    readonly Dictionary<Brush, string> _brushIds = new();
    Content _c = new();

    PdfVectorPage()
    {
        _resources = Reserve();
    }

    /// <summary>La pagina PDF che disegna <paramref name="root"/> (già misurato e disposto), grande w × h DIP.</summary>
    public static PdfPagePart Render(Visual root, double w, double h)
    {
        var p = new PdfVectorPage();
        double wPt = w * 0.75, hPt = h * 0.75;
        // da DIP (y verso il basso) a punti PDF (y verso l'alto)
        p._c.Base = Matrix.Identity;
        p._c.Ctm = Matrix.Identity;
        p.Concat(new Matrix(0.75, 0, 0, -0.75, 0, hPt));
        p.Visual(root);
        int contents = p.Add(new PdfObject("<< /Filter /FlateDecode >>", Deflate(p._c.Sb)));
        p._objects[p._resources] = new PdfObject(p.ResourceDict(), null);
        return new PdfPagePart
        {
            WidthPt = wPt,
            HeightPt = hPt,
            Contents = contents,
            Resources = p._resources,
            Objects = p._objects.Select(o => o ?? new PdfObject("null", null)).ToList(),
        };
    }

    int Reserve()
    {
        _objects.Add(null);
        return _objects.Count - 1;
    }

    int Add(PdfObject o)
    {
        _objects.Add(o);
        return _objects.Count - 1;
    }

    string ResourceDict()
    {
        var sb = new StringBuilder("<< /ProcSet [/PDF /ImageC /ImageB]");
        void Group(string key, SortedDictionary<string, string> d)
        {
            if (d.Count == 0) return;
            sb.Append($" /{key} <<");
            foreach (var (name, value) in d) sb.Append($" /{name} {value}");
            sb.Append(" >>");
        }
        Group("ExtGState", _states);
        Group("XObject", _xobjects);
        Group("Pattern", _patterns);
        return sb.Append(" >>").ToString();
    }

    // ---------------------------------------------------------------- albero visuale e disegni

    void Visual(Visual v)
    {
        if (v is UIElement { Visibility: not Visibility.Visible }) return;
        double opacity = VisualTreeHelper.GetOpacity(v);
        if (opacity <= 0) return;

        var m = VisualTreeHelper.GetTransform(v)?.Value ?? Matrix.Identity;
        var offset = VisualTreeHelper.GetOffset(v);
        m.Translate(offset.X, offset.Y);

        Push();
        Concat(m);
        if (VisualTreeHelper.GetClip(v) is { } clip) Clip(clip);
        void Body()
        {
            if (StrokeOf(v) is { } stroke && InkStroke(stroke)) { }
            else if (VisualTreeHelper.GetDrawing(v) is { } d) Drawing(d);
            int n = VisualTreeHelper.GetChildrenCount(v);
            for (int i = 0; i < n; i++)
                if (VisualTreeHelper.GetChild(v, i) is Visual child) Visual(child);
        }
        if (opacity < 1) Transparent(opacity, Body); else Body();
        Pop();
    }

    // ---------------------------------------------------------------- tratti d'inchiostro

    /// <summary>Il tratto disegnato da questo visuale dell'InkCanvas (classe interna di WPF), o null.</summary>
    static Stroke? StrokeOf(Visual v) =>
        v.GetType().Name == "StrokeVisual"
            ? v.GetType().GetProperty("Stroke", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(v) as Stroke
            : null;

    /// <summary>
    /// Un tratto di penna o matita come linea centrale con lo spessore della penna, invece del contorno che disegna WPF
    /// (i due bordi: pesa 3–4 volte tanto). Con la pressione la linea si spezza dove cambia lo spessore; i capi tondi
    /// coprono le giunture. Falso per i tratti che non si possono fare così (evidenziatori, punte non tonde,
    /// colori trasparenti, tratteggi): li disegna WPF come al solito.
    /// </summary>
    bool InkStroke(Stroke s)
    {
        var da = s.DrawingAttributes;
        if (s is DashedStroke || da.IsHighlighter || da.StylusTip != StylusTip.Ellipse || Math.Abs(da.Width - da.Height) > 0.01 ||
            !da.StylusTipTransform.IsIdentity || da.Color.A != 255)
            return false;
        Brush brush = s is PencilStroke ? Pencil.BrushFor(da.Color, Pencil.GradeOf(da) ?? Pencil.DefaultGrade) : new SolidColorBrush(da.Color);
        var points = da.FitToCurve ? s.GetBezierStylusPoints() : s.StylusPoints;
        if (points.Count == 0) return true;

        // spessori arrotondati a passi del 4 % (almeno 0,2 DIP): la pressione cambia piano, i pezzi restano lunghi
        double step = Math.Max(0.2, da.Width * 0.04);
        Push();
        Paint(brush, s.GetBounds(), stroke: true);
        Op("1 J 1 j");
        var run = new List<Point>();
        double width = -1;
        void Flush()
        {
            if (run.Count == 0) return;
            Op(N(width) + " w");
            var pts = Simplify(run, 0.12);
            var sb = _c.Sb;
            sb.Append(N1(pts[0].X)).Append(' ').Append(N1(pts[0].Y)).Append(" m\n");
            if (pts.Count == 1) sb.Append(N1(pts[0].X)).Append(' ').Append(N1(pts[0].Y)).Append(" l\n");
            for (int i = 1; i < pts.Count; i++) sb.Append(N1(pts[i].X)).Append(' ').Append(N1(pts[i].Y)).Append(" l\n");
            Op("S");
        }
        foreach (var sp in points)
        {
            // in WPF la pressione 0,5 dà lo spessore nominale
            double w = da.IgnorePressure ? da.Width : da.Width * 2 * sp.PressureFactor;
            w = Math.Max(step, Math.Round(w / step) * step);
            if (Math.Abs(w - width) > 1e-9)
            {
                if (run.Count > 0)
                {
                    var last = run[^1];
                    Flush();
                    run.Clear();
                    run.Add(last);
                }
                width = w;
            }
            run.Add(new Point(sp.X, sp.Y));
        }
        Flush();
        Pop();
        return true;
    }

    /// <summary>Douglas-Peucker: toglie i punti che si discostano dalla linea meno di <paramref name="tolerance"/>.</summary>
    static List<Point> Simplify(List<Point> pts, double tolerance)
    {
        if (pts.Count < 3) return pts;
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            double max = 0;
            int idx = -1;
            var d = pts[b] - pts[a];
            double len = d.Length;
            for (int i = a + 1; i < b; i++)
            {
                double dist = len < 1e-9 ? (pts[i] - pts[a]).Length : Math.Abs(Vector.CrossProduct(d, pts[i] - pts[a])) / len;
                if (dist > max)
                {
                    max = dist;
                    idx = i;
                }
            }
            if (idx >= 0 && max > tolerance)
            {
                keep[idx] = true;
                stack.Push((a, idx));
                stack.Push((idx, b));
            }
        }
        var result = new List<Point>();
        for (int i = 0; i < pts.Count; i++)
            if (keep[i]) result.Add(pts[i]);
        return result;
    }

    // ---------------------------------------------------------------- disegni

    void Drawing(Drawing d)
    {
        switch (d)
        {
            case DrawingGroup g:
                if (g.Opacity <= 0) return;
                if (g.OpacityMask != null) { Raster(g); return; }
                Push();
                if (g.Transform is { } t && !t.Value.IsIdentity) Concat(t.Value);
                if (g.ClipGeometry is { } clip) Clip(clip);
                void Children()
                {
                    foreach (var child in g.Children) Drawing(child);
                }
                if (g.Opacity < 1) Transparent(g.Opacity, Children); else Children();
                Pop();
                break;
            case GeometryDrawing gd when gd.Geometry != null:
                Geometry(gd.Brush, gd.Pen, gd.Geometry, d);
                break;
            case GlyphRunDrawing gr when gr.GlyphRun != null && gr.ForegroundBrush != null:
                Geometry(gr.ForegroundBrush, null, gr.GlyphRun.BuildGeometry(), d);
                break;
            case ImageDrawing id when id.ImageSource != null:
                Image(id.ImageSource, id.Rect);
                break;
        }
    }

    /// <summary>Riempie e/o traccia una geometria; se il pennello non si può tradurre, la disegna come immagine.</summary>
    void Geometry(Brush? fill, Pen? pen, Geometry geometry, Drawing original)
    {
        if (geometry.IsEmpty()) return;
        bool doFill = fill != null && !Invisible(fill);
        bool doStroke = pen is { Thickness: > 0, Brush: { } pb } && !Invisible(pb);
        if (!doFill && !doStroke) return;
        if ((doFill && !CanPaint(fill!)) || (doStroke && !CanPaint(pen!.Brush)))
        {
            Raster(original);
            return;
        }

        var bounds = geometry.Bounds;
        if (doFill && fill is DrawingBrush db && TileFill(db, geometry))
            doFill = false;
        if (doFill)
        {
            Push();
            Paint(fill!, bounds, stroke: false);
            bool evenOdd = Path(geometry);
            Op(evenOdd ? "f*" : "f");
            Pop();
        }
        if (doStroke)
        {
            Push();
            Paint(pen!.Brush, bounds, stroke: true);
            PenState(pen);
            Path(geometry);
            Op("S");
            Pop();
        }
    }

    static bool Invisible(Brush b) => b.Opacity <= 0 || b is SolidColorBrush { Color.A: 0 };

    static bool CanPaint(Brush b) => b is SolidColorBrush || TileOk(b as TileBrush);

    /// <summary>Motivi ripetuti che il PDF sa fare da sé: immagini o disegni a piastrelle, senza trasformazioni.</summary>
    static bool TileOk(TileBrush? tb) =>
        tb != null && tb.TileMode == TileMode.Tile &&
        (tb.Transform == null || tb.Transform.Value.IsIdentity) &&
        (tb.RelativeTransform == null || tb.RelativeTransform.Value.IsIdentity) &&
        tb.Viewport.Width > 0 && tb.Viewport.Height > 0 &&
        tb switch
        {
            ImageBrush { ImageSource: BitmapSource } => true,
            DrawingBrush { Drawing: not null } => true,
            _ => false,
        };

    void Paint(Brush b, Rect bounds, bool stroke)
    {
        if (b is SolidColorBrush s)
        {
            var c = s.Color;
            Op($"{C(c.R / 255.0)} {C(c.G / 255.0)} {C(c.B / 255.0)} {(stroke ? "RG" : "rg")}");
            Alpha(c.A / 255.0 * b.Opacity, stroke);
            return;
        }
        var name = Pattern((TileBrush)b, bounds);
        Op(stroke ? $"/Pattern CS /{name} SCN" : $"/Pattern cs /{name} scn");
        Alpha(b.Opacity, stroke);
    }

    void Alpha(double a, bool stroke)
    {
        if (a >= 0.999) return;
        a = Math.Round(a, 3);
        var key = (stroke ? "CA " : "ca ") + C(a);
        if (!_stateNames.TryGetValue(key, out var name))
        {
            name = "GS" + _stateNames.Count;
            _stateNames[key] = name;
            _states[name] = $"<< /{(stroke ? "CA" : "ca")} {C(a)} >>";
        }
        Op($"/{name} gs");
    }

    void PenState(Pen pen)
    {
        int Cap(PenLineCap c) => c switch { PenLineCap.Flat => 0, PenLineCap.Square => 2, _ => 1 };
        Op($"{N(pen.Thickness)} w {Cap(pen.StartLineCap)} J " +
           (pen.LineJoin switch { PenLineJoin.Miter => $"0 j {N(Math.Max(1, pen.MiterLimit))} M", PenLineJoin.Bevel => "2 j", _ => "1 j" }));
        if (pen.DashStyle is { Dashes.Count: > 0 } ds)
        {
            // in WPF trattini e spazi sono multipli dello spessore
            var dashes = ds.Dashes.Select(d => N(Math.Max(d, 0) * pen.Thickness));
            Op($"[{string.Join(" ", dashes)}] {N(ds.Offset * pen.Thickness)} d");
        }
    }

    // ---------------------------------------------------------------- gruppi trasparenti e motivi

    /// <summary>Opacità di un gruppo (es. gli evidenziatori): si fa un gruppo trasparente, che non scurisce dove i pezzi si sovrappongono.</summary>
    void Transparent(double opacity, Action body)
    {
        var parent = _c;
        _c = new Content { Ctm = parent.Ctm, Base = parent.Ctm };
        body();
        var form = _c;
        _c = parent;
        if (form.Sb.Length == 0) return;
        int k = Add(new PdfObject("<< /Type /XObject /Subtype /Form /BBox [-100000 -100000 100000 100000] " +
                                  $"/Group << /S /Transparency >> /Resources @{_resources} /Filter /FlateDecode >>", Deflate(form.Sb)));
        var name = "Fm" + k;
        _xobjects[name] = "@" + k;
        Push();
        Alpha(opacity, stroke: false);
        Alpha(opacity, stroke: true);
        Op($"/{name} Do");
        Pop();
    }

    /// <summary>
    /// Sfondo a piastrelle (righe, quadretti, puntini) disegnato piastrella per piastrella: con un motivo ripetuto del PDF
    /// molti lettori arrotondano la piastrella ai pixel e su una pagina lunga le righe scivolano rispetto alla scrittura.
    /// Si scrive una piastrella e una riga di piastrelle, poi si ripete la riga: il file resta piccolo.
    /// </summary>
    bool TileFill(DrawingBrush db, Geometry geometry)
    {
        var b = geometry.Bounds;
        var vp = db.Viewport;
        if (db.ViewportUnits == BrushMappingMode.RelativeToBoundingBox)
            vp = new Rect(b.X + vp.X * b.Width, b.Y + vp.Y * b.Height, vp.Width * b.Width, vp.Height * b.Height);
        var vb = db.Viewbox;
        if (db.ViewboxUnits == BrushMappingMode.RelativeToBoundingBox)
        {
            var d = db.Drawing.Bounds;
            vb = new Rect(d.X + vb.X * d.Width, d.Y + vb.Y * d.Height, vb.Width * d.Width, vb.Height * d.Height);
        }
        if (vb.Width <= 0 || vb.Height <= 0) return false;
        int i0 = (int)Math.Floor((b.X - vp.X) / vp.Width), i1 = (int)Math.Ceiling((b.Right - vp.X) / vp.Width);
        int j0 = (int)Math.Floor((b.Y - vp.Y) / vp.Height), j1 = (int)Math.Ceiling((b.Bottom - vp.Y) / vp.Height);
        if ((long)(i1 - i0) * (j1 - j0) > 500_000) return false; // piastrelle minuscole: meglio il motivo

        Push();
        Clip(geometry);
        void Tiles()
        {
            // il fondo pieno della piastrella si stende una volta sola su tutta l'area
            var children = db.Drawing is DrawingGroup { Transform: null, ClipGeometry: null, Opacity: 1, OpacityMask: null } g
                ? g.Children.ToList() : new List<Drawing> { db.Drawing };
            if (children.Count > 0 &&
                children[0] is GeometryDrawing { Brush: SolidColorBrush full, Pen: null, Geometry: RectangleGeometry { Transform: null } r } &&
                r.Rect.Contains(vb))
            {
                Push();
                Paint(full, b, stroke: false);
                Op($"{N(b.X)} {N(b.Y)} {N(b.Width)} {N(b.Height)} re f");
                Pop();
                children.RemoveAt(0);
            }
            if (children.Count == 0) return;

            double sx = vp.Width / vb.Width, sy = vp.Height / vb.Height;
            var parent = _c;
            _c = new Content { Ctm = parent.Ctm, Base = parent.Ctm };
            Concat(new Matrix(sx, 0, 0, sy, -vb.X * sx, -vb.Y * sy));
            foreach (var child in children) Drawing(child);
            // senza ritaglio alla piastrella: Poppler lascia righine chiare tra le piastrelle semitrasparenti
            var tile = Form(_c, null);

            _c = new Content { Ctm = parent.Ctm, Base = parent.Ctm };
            for (int i = i0; i < i1; i++) Op($"/{tile} Do 1 0 0 1 {N(vp.Width)} 0 cm");
            var row = Form(_c, null);
            _c = parent;

            Push();
            Concat(new Matrix(1, 0, 0, 1, vp.X + i0 * vp.Width, vp.Y + j0 * vp.Height));
            for (int j = j0; j < j1; j++) Op($"/{row} Do 1 0 0 1 0 {N(vp.Height)} cm");
            Pop();
        }
        if (db.Opacity < 1) Transparent(db.Opacity, Tiles); else Tiles();
        Pop();
        return true;
    }

    /// <summary>Un Form XObject con il contenuto dato; restituisce il suo nome tra le risorse.</summary>
    string Form(Content content, string? bbox)
    {
        int k = Add(new PdfObject($"<< /Type /XObject /Subtype /Form /BBox [{bbox ?? "-100000 -100000 100000 100000"}] " +
                                  $"/Resources @{_resources} /Filter /FlateDecode >>", Deflate(content.Sb)));
        var name = "Fm" + k;
        _xobjects[name] = "@" + k;
        return name;
    }

    string Pattern(TileBrush tb, Rect bounds)
    {
        var vp = tb.Viewport;
        if (tb.ViewportUnits == BrushMappingMode.RelativeToBoundingBox)
            vp = new Rect(bounds.X + vp.X * bounds.Width, bounds.Y + vp.Y * bounds.Height, vp.Width * bounds.Width, vp.Height * bounds.Height);

        // spazio del motivo: la piastrella va da (0,0) a (vp.Width, vp.Height)
        var abs = new Matrix(1, 0, 0, 1, vp.X, vp.Y) * _c.Ctm;
        var rel = _c.Base;
        rel.Invert();
        rel = abs * rel;

        if (!_brushIds.TryGetValue(tb, out var brushId)) _brushIds[tb] = brushId = _brushIds.Count.ToString(Inv);
        var key = $"{brushId}|{N(vp.Width)} {N(vp.Height)}|{M(rel)}";
        if (_patternNames.TryGetValue(key, out var existing)) return existing;

        var parent = _c;
        _c = new Content { Ctm = abs, Base = abs };
        switch (tb)
        {
            case ImageBrush { ImageSource: BitmapSource bmp } ib:
            {
                var vb = ib.Viewbox;
                if (ib.ViewboxUnits == BrushMappingMode.RelativeToBoundingBox)
                    vb = new Rect(vb.X * bmp.Width, vb.Y * bmp.Height, vb.Width * bmp.Width, vb.Height * bmp.Height);
                double sx = vp.Width / vb.Width, sy = vp.Height / vb.Height;
                Image(bmp, new Rect(-vb.X * sx, -vb.Y * sy, bmp.Width * sx, bmp.Height * sy));
                break;
            }
            case DrawingBrush db:
            {
                var vb = db.Viewbox;
                if (db.ViewboxUnits == BrushMappingMode.RelativeToBoundingBox)
                {
                    var b = db.Drawing.Bounds;
                    vb = new Rect(b.X + vb.X * b.Width, b.Y + vb.Y * b.Height, vb.Width * b.Width, vb.Height * b.Height);
                }
                double sx = vp.Width / vb.Width, sy = vp.Height / vb.Height;
                Push();
                Concat(new Matrix(sx, 0, 0, sy, -vb.X * sx, -vb.Y * sy));
                Drawing(db.Drawing);
                Pop();
                break;
            }
        }
        var content = _c;
        _c = parent;

        int k = Add(new PdfObject(
            $"<< /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 {N(vp.Width)} {N(vp.Height)}] " +
            $"/XStep {N(vp.Width)} /YStep {N(vp.Height)} /Matrix [{M(rel)}] /Resources @{_resources} /Filter /FlateDecode >>",
            Deflate(content.Sb)));
        var name = "P" + k;
        _patterns[name] = "@" + k;
        _patternNames[key] = name;
        return name;
    }

    // ---------------------------------------------------------------- immagini

    void Image(ImageSource src, Rect r)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        if (src is DrawingImage di)
        {
            var b = di.Drawing.Bounds;
            if (b.IsEmpty || b.Width <= 0 || b.Height <= 0) return;
            Push();
            Concat(new Matrix(r.Width / b.Width, 0, 0, r.Height / b.Height, r.X - b.X * r.Width / b.Width, r.Y - b.Y * r.Height / b.Height));
            Drawing(di.Drawing);
            Pop();
            return;
        }
        if (src is not BitmapSource bmp) return;

        // niente pixel inutili: al massimo il doppio di quelli che servono alla dimensione sulla pagina
        double scale = Scale() * 2;
        int maxW = Math.Max(1, (int)Math.Ceiling(r.Width * scale)), maxH = Math.Max(1, (int)Math.Ceiling(r.Height * scale));
        int tw = bmp.PixelWidth, th = bmp.PixelHeight;
        if (tw > maxW * 1.25 || th > maxH * 1.25)
        {
            double f = Math.Min((double)maxW / tw, (double)maxH / th);
            tw = Math.Max(1, (int)Math.Round(tw * f));
            th = Math.Max(1, (int)Math.Round(th * f));
        }
        if (!_images.TryGetValue((src, tw, th), out var name))
        {
            int k = ImageObject(bmp, tw, th);
            name = "Im" + k;
            _xobjects[name] = "@" + k;
            _images[(src, tw, th)] = name;
        }
        Push();
        Concat(new Matrix(r.Width, 0, 0, -r.Height, r.X, r.Y + r.Height));
        Op($"/{name} Do");
        Pop();
    }

    /// <summary>Scala attuale da DIP a pixel dello schermo (1 = 96 dpi).</summary>
    double Scale() => Math.Sqrt(Math.Abs(_c.Ctm.Determinant)) / 0.75;

    int ImageObject(BitmapSource bmp, int w, int h)
    {
        BitmapSource src = bmp;
        if (w != bmp.PixelWidth || h != bmp.PixelHeight)
            src = new TransformedBitmap(bmp, new ScaleTransform((double)w / bmp.PixelWidth, (double)h / bmp.PixelHeight));
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        w = bgra.PixelWidth;
        h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);

        var rgb = new byte[w * h * 3];
        var alpha = new byte[w * h];
        bool hasAlpha = false;
        for (int i = 0, j = 0; i < w * h; i++, j += 4)
        {
            rgb[i * 3] = px[j + 2];
            rgb[i * 3 + 1] = px[j + 1];
            rgb[i * 3 + 2] = px[j];
            alpha[i] = px[j + 3];
            if (px[j + 3] != 255) hasAlpha = true;
        }

        string smask = "";
        if (hasAlpha)
        {
            int m = Add(new PdfObject($"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceGray " +
                                      "/BitsPerComponent 8 /Filter /FlateDecode >>", Deflate(alpha)));
            smask = $" /SMask @{m}";
        }

        // Le foto vengono meglio in JPEG, gli schermi e i disegni (pochi colori, bordi netti) senza perdite.
        var flate = Deflate(rgb);
        byte[]? jpeg = null;
        if (flate.Length > 64 * 1024)
        {
            var enc = new JpegBitmapEncoder { QualityLevel = 88 };
            enc.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0)));
            using var ms = new MemoryStream();
            enc.Save(ms);
            if (ms.Length * 2 < flate.Length) jpeg = ms.ToArray();
        }
        return Add(new PdfObject($"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB " +
                                 $"/BitsPerComponent 8 /Filter /{(jpeg != null ? "DCTDecode" : "FlateDecode")}{smask} >>", jpeg ?? flate));
    }

    /// <summary>Ripiego per ciò che il PDF non sa fare da sé: il disegno diventa un'immagine a doppia risoluzione.</summary>
    void Raster(Drawing d)
    {
        var b = d.Bounds;
        if (b.IsEmpty || b.Width <= 0 || b.Height <= 0) return;
        double s = Math.Min(Scale() * 2, Math.Sqrt(16_000_000 / (b.Width * b.Height)));
        int pw = Math.Max(1, (int)Math.Ceiling(b.Width * s)), ph = Math.Max(1, (int)Math.Ceiling(b.Height * s));
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new MatrixTransform(s, 0, 0, s, -b.X * s, -b.Y * s));
            dc.DrawDrawing(d);
        }
        var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        Image(rtb, new Rect(b.X, b.Y, pw / s, ph / s));
    }

    // ---------------------------------------------------------------- percorsi

    void Clip(Geometry g)
    {
        if (g.IsEmpty())
        {
            Op("0 0 m h W n");
            return;
        }
        bool evenOdd = Path(g);
        Op(evenOdd ? "W* n" : "W n");
    }

    /// <summary>Scrive il percorso della geometria (nello spazio corrente); restituisce vero se la regola è «pari-dispari».</summary>
    bool Path(Geometry g) => Path(g, Matrix.Identity);

    bool Path(Geometry g, Matrix m)
    {
        if (g.Transform is { } t && !t.Value.IsIdentity) m = t.Value * m;
        switch (g)
        {
            case GeometryGroup gg:
                foreach (var child in gg.Children) Path(child, m);
                return gg.FillRule == FillRule.EvenOdd;
            case PathGeometry pg:
                Figures(pg, m);
                return pg.FillRule == FillRule.EvenOdd;
            default:
            {
                // StreamGeometry, rettangoli, ellissi, combinazioni: in percorso, senza la trasformazione già contata
                var copy = g.CloneCurrentValue();
                copy.Transform = null;
                var pg = PathGeometry.CreateFromGeometry(copy);
                if (pg.Transform is { } pt && !pt.Value.IsIdentity) m = pt.Value * m;
                Figures(pg, m);
                return pg.FillRule == FillRule.EvenOdd;
            }
        }
    }

    void Figures(PathGeometry pg, Matrix m)
    {
        var sb = _c.Sb;
        void Pt(Point p)
        {
            p = m.Transform(p);
            sb.Append(N(p.X)).Append(' ').Append(N(p.Y));
        }
        void Line(Point p)
        {
            Pt(p);
            sb.Append(" l\n");
        }
        void Curve(Point a, Point b, Point c)
        {
            Pt(a); sb.Append(' ');
            Pt(b); sb.Append(' ');
            Pt(c); sb.Append(" c\n");
        }

        foreach (var f in pg.Figures)
        {
            var cur = f.StartPoint;
            Pt(cur);
            sb.Append(" m\n");
            foreach (var seg in f.Segments)
            {
                switch (seg)
                {
                    case LineSegment ls:
                        Line(cur = ls.Point);
                        break;
                    case PolyLineSegment pls:
                        foreach (var p in pls.Points) Line(cur = p);
                        break;
                    case BezierSegment bs:
                        Curve(bs.Point1, bs.Point2, cur = bs.Point3);
                        break;
                    case PolyBezierSegment pbs:
                        for (int i = 0; i + 2 < pbs.Points.Count; i += 3)
                            Curve(pbs.Points[i], pbs.Points[i + 1], cur = pbs.Points[i + 2]);
                        break;
                    case QuadraticBezierSegment qs:
                        Quad(cur, qs.Point1, qs.Point2);
                        cur = qs.Point2;
                        break;
                    case PolyQuadraticBezierSegment pqs:
                        for (int i = 0; i + 1 < pqs.Points.Count; i += 2)
                        {
                            Quad(cur, pqs.Points[i], pqs.Points[i + 1]);
                            cur = pqs.Points[i + 1];
                        }
                        break;
                    case ArcSegment arc:
                        foreach (var (a, b, c) in ArcToBeziers(cur, arc)) Curve(a, b, c);
                        cur = arc.Point;
                        break;
                }
            }
            if (f.IsClosed) sb.Append("h\n");
        }

        void Quad(Point p0, Point q, Point p) =>
            Curve(p0 + (q - p0) * (2.0 / 3), p + (q - p) * (2.0 / 3), p);
    }

    /// <summary>Arco ellittico (come in SVG) → curve di Bézier cubiche, una ogni quarto di giro al massimo.</summary>
    static IEnumerable<(Point, Point, Point)> ArcToBeziers(Point p0, ArcSegment arc)
    {
        var p1 = arc.Point;
        double rx = Math.Abs(arc.Size.Width), ry = Math.Abs(arc.Size.Height);
        if (rx < 1e-9 || ry < 1e-9 || (p0 - p1).Length < 1e-9)
        {
            yield return (p0, p1, p1);
            yield break;
        }
        double phi = arc.RotationAngle * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
        double dx = (p0.X - p1.X) / 2, dy = (p0.Y - p1.Y) / 2;
        double x1 = cos * dx + sin * dy, y1 = -sin * dx + cos * dy;
        double lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
        if (lambda > 1)
        {
            rx *= Math.Sqrt(lambda);
            ry *= Math.Sqrt(lambda);
        }
        double num = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
        double den = rx * rx * y1 * y1 + ry * ry * x1 * x1;
        double coef = Math.Sqrt(Math.Max(0, num / den));
        bool clockwise = arc.SweepDirection == SweepDirection.Clockwise;
        if (arc.IsLargeArc == clockwise) coef = -coef;
        double cx1 = coef * rx * y1 / ry, cy1 = -coef * ry * x1 / rx;
        double cx = cos * cx1 - sin * cy1 + (p0.X + p1.X) / 2, cy = sin * cx1 + cos * cy1 + (p0.Y + p1.Y) / 2;

        double Angle(double ux, double uy, double vx, double vy)
        {
            double a = Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
            return a;
        }
        double t1 = Angle(1, 0, (x1 - cx1) / rx, (y1 - cy1) / ry);
        double dt = Angle((x1 - cx1) / rx, (y1 - cy1) / ry, (-x1 - cx1) / rx, (-y1 - cy1) / ry);
        if (clockwise && dt < 0) dt += 2 * Math.PI;
        else if (!clockwise && dt > 0) dt -= 2 * Math.PI;

        int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(dt) / (Math.PI / 2) - 1e-9));
        double step = dt / n, k = 4.0 / 3 * Math.Tan(step / 4);
        Point On(double t) => new(cx + rx * Math.Cos(t) * cos - ry * Math.Sin(t) * sin, cy + rx * Math.Cos(t) * sin + ry * Math.Sin(t) * cos);
        Vector D(double t) => new(-rx * Math.Sin(t) * cos - ry * Math.Cos(t) * sin, -rx * Math.Sin(t) * sin + ry * Math.Cos(t) * cos);
        double ta = t1;
        for (int i = 0; i < n; i++)
        {
            double tb = ta + step;
            var a = On(ta) + D(ta) * k;
            var b = On(tb) - D(tb) * k;
            yield return (a, b, i == n - 1 ? p1 : On(tb));
            ta = tb;
        }
    }

    // ---------------------------------------------------------------- stato e numeri

    void Op(string s) => _c.Sb.Append(s).Append('\n');

    void Push()
    {
        _c.Saved.Push(_c.Ctm);
        Op("q");
    }

    void Pop()
    {
        _c.Ctm = _c.Saved.Pop();
        Op("Q");
    }

    void Concat(Matrix m)
    {
        if (m.IsIdentity) return;
        Op(M(m) + " cm");
        _c.Ctm = m * _c.Ctm;
    }

    static string N(double v) => Math.Round(v, 2).ToString("0.##", Inv);
    static string N1(double v) => Math.Round(v, 1).ToString("0.#", Inv);
    static string C(double v) => Math.Round(v, 3).ToString("0.###", Inv);
    static string M(Matrix m) =>
        string.Join(" ", new[] { m.M11, m.M12, m.M21, m.M22 }.Select(v => v.ToString("0.#####", Inv))) +
        " " + m.OffsetX.ToString("0.###", Inv) + " " + m.OffsetY.ToString("0.###", Inv);

    static byte[] Deflate(StringBuilder sb) => Deflate(Encoding.ASCII.GetBytes(sb.ToString()));

    public static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }
}
