using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Input.StylusPlugIns;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ardesia.Editor;

/// <summary>
/// Matita: tratto grigio "granuloso" come la grafite sulla carta, nelle durezze da 3H a 3B.
/// La durezza è salvata nei DrawingAttributes del tratto (quindi anche nel file .ardesia).
/// </summary>
public static class Pencil
{
    public static readonly Guid GradeProperty = new("6e0c2b8a-3f4d-4e59-9c1b-2a7d5e8f1c30");
    public static readonly string[] Grades = { "3H", "2H", "H", "HB", "B", "2B", "3B" };
    public const int DefaultGrade = 3; // HB

    // Dalle mine dure (chiare, con molta grana) a quelle morbide (scure, piene).
    static readonly byte[] Gray = { 168, 145, 120, 96, 72, 52, 34 };
    static readonly double[] MinAlpha = { 0.18, 0.26, 0.34, 0.44, 0.54, 0.62, 0.70 };
    static readonly double[] Gaps = { 0.16, 0.14, 0.12, 0.10, 0.08, 0.06, 0.04 };

    static readonly Dictionary<(Color, int), Brush> Cache = new();
    static readonly object CacheLock = new();

    public static int Clamp(int grade) => Math.Clamp(grade, 0, Grades.Length - 1);

    public static Color ColorOf(int grade)
    {
        var g = Gray[Clamp(grade)];
        return Color.FromRgb(g, g, g);
    }

    public static int? GradeOf(DrawingAttributes da) =>
        da.ContainsPropertyData(GradeProperty) && da.GetPropertyData(GradeProperty) is int g ? Clamp(g) : null;

    public static DrawingAttributes Attributes(int grade, double width)
    {
        var da = new DrawingAttributes
        {
            Color = ColorOf(grade),
            Width = width,
            Height = width,
            FitToCurve = true,
            IgnorePressure = false,
            StylusTip = StylusTip.Ellipse,
        };
        da.AddPropertyData(GradeProperty, Clamp(grade));
        return da;
    }

    /// <summary>Pennello con la grana della grafite (immagine di rumore ripetuta), già "congelato" per l'uso tra thread.</summary>
    public static Brush BrushFor(Color color, int grade)
    {
        grade = Clamp(grade);
        lock (CacheLock)
        {
            if (Cache.TryGetValue((color, grade), out var cached)) return cached;

            const int n = 96;
            var rnd = new Random(7919 * (grade + 1));
            var px = new byte[n * n * 4];
            double min = MinAlpha[grade];
            for (int i = 0; i < n * n; i++)
            {
                double a = min + (1 - min) * rnd.NextDouble();
                if (rnd.NextDouble() < Gaps[grade]) a *= 0.3; // "denti" della carta non toccati dalla mina
                a *= color.A / 255.0;
                px[i * 4 + 0] = (byte)(color.B * a);
                px[i * 4 + 1] = (byte)(color.G * a);
                px[i * 4 + 2] = (byte)(color.R * a);
                px[i * 4 + 3] = (byte)(255 * a);
            }
            var bmp = BitmapSource.Create(n, n, 96, 96, PixelFormats.Pbgra32, null, px, n * 4);
            bmp.Freeze();
            var brush = new ImageBrush(bmp)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, n, n),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill,
            };
            brush.Freeze();
            Cache[(color, grade)] = brush;
            return brush;
        }
    }

    /// <summary>Trasforma un tratto "matita" generico (es. caricato da file) nel tipo che lo disegna con la grana.</summary>
    public static Stroke Upgrade(Stroke s) =>
        s is not PencilStroke && GradeOf(s.DrawingAttributes) != null
            ? new PencilStroke(s.StylusPoints.Clone(), s.DrawingAttributes.Clone())
            : s;

    public static bool NeedsUpgrade(StrokeCollection strokes) =>
        strokes.Any(s => s is not PencilStroke && GradeOf(s.DrawingAttributes) != null);

    public static void UpgradeAll(StrokeCollection strokes)
    {
        foreach (var s in strokes.ToList())
        {
            var u = Upgrade(s);
            if (!ReferenceEquals(u, s)) strokes.Replace(s, new StrokeCollection { u });
        }
    }
}

public sealed class PencilStroke : Stroke
{
    public PencilStroke(StylusPointCollection points, DrawingAttributes da) : base(points, da)
    {
    }

    protected override void DrawCore(DrawingContext dc, DrawingAttributes da)
    {
        int grade = Pencil.GradeOf(da) ?? Pencil.DefaultGrade;
        dc.DrawGeometry(Pencil.BrushFor(da.Color, grade), null, GetGeometry(da));
    }

    public override Stroke Clone() => new PencilStroke(StylusPoints.Clone(), DrawingAttributes.Clone());
}

/// <summary>
/// Disegno «dal vivo» del tratto mentre si scrive: normale, a matita, oppure nascosto
/// (gomma col palmo, o linea appena raddrizzata tenendo premuto).
/// </summary>
public sealed class ArdesiaDynamicRenderer : DynamicRenderer
{
    /// <summary>Pennello della matita (congelato), impostato dal thread dell'interfaccia; null = tratto normale.</summary>
    public volatile Brush? PencilBrush;

    /// <summary>Se vero non disegna nulla (il renderer lavora su un thread separato).</summary>
    public volatile bool Suppress;

    protected override void OnDraw(DrawingContext drawingContext, StylusPointCollection stylusPoints, Geometry geometry, Brush fillBrush)
    {
        if (Suppress) return;
        if (PencilBrush is { } pencil) drawingContext.DrawGeometry(pencil, null, geometry);
        else base.OnDraw(drawingContext, stylusPoints, geometry, fillBrush);
    }
}

/// <summary>InkCanvas con il disegno «dal vivo» personalizzato.</summary>
public class ArdesiaInkCanvas : InkCanvas
{
    readonly ArdesiaDynamicRenderer _live = new();

    public ArdesiaInkCanvas()
    {
        DynamicRenderer = _live;
    }

    bool _liveEnabled = true;

    public void SetPencilMode(Brush? pencilBrush) => _live.PencilBrush = pencilBrush;

    /// <summary>Disegno dal vivo attivo solo con penne, evidenziatori e matite (mai con gomme, selezione, ecc.).</summary>
    public bool LiveEnabled
    {
        get => _liveEnabled;
        set
        {
            _liveEnabled = value;
            _live.Suppress = !value;
        }
    }

    /// <summary>Nasconde il tratto che si sta disegnando (e smette di disegnarlo finché non si chiama <see cref="ShowLiveStrokes"/>).</summary>
    public void HideLiveStroke(StylusDevice? device, Point at)
    {
        _live.Suppress = true;
        try
        {
            // Ridisegna il tratto in corso con il disegno disattivato: in pratica lo cancella dallo schermo.
            _live.Reset(device, new StylusPointCollection(new[] { new StylusPoint(at.X, at.Y) }));
        }
        catch
        {
            // Se non è possibile, il tratto resta visibile fino al rilascio: nessun danno.
        }
    }

    public void ShowLiveStrokes() => _live.Suppress = !_liveEnabled;
}
