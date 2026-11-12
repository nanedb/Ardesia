using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Media;

namespace Ardesia.Editor;

/// <summary>
/// Rotazione della selezione (tratti, caselle di testo, immagini, grafici): maniglia tonda sopra la selezione, da
/// trascinare con penna, dito o mouse; vicino a 0°, 90°, 180° e 270° si aggancia. Anche «Ruota di 90°» nel menu.
/// I tratti si trasformano davvero; gli oggetti hanno una LayoutTransform (ElementFactory.SetAngle) e l'angolo si
/// salva nel blocco (ElementData.Angle). Il trascinamento usa lo stesso giro dello spostamento (DragMode.MoveSelection).
/// </summary>
public partial class PageEditor
{
    const double RotateHandleSize = 34; // sullo schermo, in pixel
    const double RotateHandleGap = 30;  // distanza dal bordo della selezione, sullo schermo
    const double RotateSnap = 4;        // gradi

    readonly Border _rotateHandle = new()
    {
        Width = RotateHandleSize,
        Height = RotateHandleSize,
        CornerRadius = new CornerRadius(RotateHandleSize / 2),
        Background = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x5A, 0x68)),
        BorderThickness = new Thickness(2),
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false, // la presa la riconosce TryBeginMove dalla posizione
        ToolTip = "Trascina per ruotare",
        Child = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x5A, 0x68)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    readonly Border _rotateLabel = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x1E, 0x5A, 0x68)),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 2, 6, 2),
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
        Child = new TextBlock { Foreground = Brushes.White, FontSize = 14 },
    };

    bool _rotating;
    bool _cornerResize; // ultimo ridimensionamento da un angolo: le caselle di testo restano «larghe quanto il testo»
    Point _rotCenter, _rotHandleStart;
    double _rotStartAngle, _rotApplied;
    List<(FrameworkElement El, Point Center, double Angle)> _rotElements = new();

    void SetupRotation()
    {
        Overlay.Children.Add(_rotateHandle);
        Overlay.Children.Add(_rotateLabel);
        Ink.SelectionChanged += (_, _) => UpdateRotateHandle();
        Ink.SelectionMoved += (_, _) => UpdateRotateHandle();
        Ink.SelectionResized += (_, _) => UpdateRotateHandle();
        Ink.SelectionMoving += (_, _) => _rotateHandle.Visibility = Visibility.Collapsed;
        Ink.SelectionResizing += Ink_SelectionResizing;
        Ink.LayoutUpdated += (_, _) => { if (_rotateHandle.Visibility == Visibility.Visible) UpdateRotateHandle(); };
        ZoomChanged += (_, _) => UpdateRotateHandle();
        ToolChanged += (_, _) => UpdateRotateHandle();
    }

    Point RotateHandlePoint(Rect b)
    {
        double gap = (RotateHandleGap + RotateHandleSize / 2) / _zoom;
        // in cima alla pagina non c'è posto sopra: la maniglia va sotto
        double y = b.Top - gap < 0 ? b.Bottom + gap : b.Top - gap;
        return new Point(b.Left + b.Width / 2, y);
    }

    void UpdateRotateHandle()
    {
        UpdateMiniBar();
        if (_rotating) return;
        bool show = _tool == EditorTool.Select && _drag == DragMode.None && HasSelection;
        var b = show ? Ink.GetSelectionBounds() : Rect.Empty;
        if (b.IsEmpty)
        {
            _rotateHandle.Visibility = Visibility.Collapsed;
            return;
        }
        PlaceRotateHandle(RotateHandlePoint(b));
        _rotateHandle.Visibility = Visibility.Visible;
    }

    void PlaceRotateHandle(Point c)
    {
        double s = 1 / _zoom;
        if (_rotateHandle.RenderTransform is not ScaleTransform st || Math.Abs(st.ScaleX - s) > 1e-6)
            _rotateHandle.RenderTransform = new ScaleTransform(s, s);
        double left = c.X - RotateHandleSize * s / 2, top = c.Y - RotateHandleSize * s / 2;
        // (all'inizio la posizione è NaN: il confronto è scritto in modo da impostarla)
        if (!(Math.Abs(Canvas.GetLeft(_rotateHandle) - left) <= 0.01)) Canvas.SetLeft(_rotateHandle, left);
        if (!(Math.Abs(Canvas.GetTop(_rotateHandle) - top) <= 0.01)) Canvas.SetTop(_rotateHandle, top);
    }

    /// <summary>Vero se il punto (coordinate della pagina) è sulla maniglia: allora comincia la rotazione.</summary>
    bool TryBeginRotate(Point p)
    {
        if (_rotateHandle.Visibility != Visibility.Visible || _tool != EditorTool.Select || !HasSelection) return false;
        var b = Ink.GetSelectionBounds();
        var h = RotateHandlePoint(b);
        if ((p - h).Length > (RotateHandleSize / 2 + 8) / _zoom) return false;

        _moveStrokes = Ink.GetSelectedStrokes();
        _moveElements = Ink.GetSelectedElements().ToList();
        _rotCenter = new Point(b.Left + b.Width / 2, b.Top + b.Height / 2);
        _rotHandleStart = h;
        _rotStartAngle = Math.Atan2(p.Y - _rotCenter.Y, p.X - _rotCenter.X) * 180 / Math.PI;
        _rotApplied = 0;
        _rotElements = _moveElements.OfType<FrameworkElement>().Select(el => (el, BoxCenter(el), ElementFactory.GetAngle(el))).ToList();
        _moved = false;
        _rotating = true;
        _drag = DragMode.MoveSelection;
        ClearSelection();
        return true;
    }

    void ContinueRotate(Point p)
    {
        var v = p - _rotCenter;
        if (v.Length < 4 / _zoom) return;
        double total = ElementFactory.NormalizeAngle(Math.Atan2(v.Y, v.X) * 180 / Math.PI - _rotStartAngle);
        double snapped = Math.Round(total / 90) * 90;
        if (Math.Abs(total - snapped) < RotateSnap) total = ElementFactory.NormalizeAngle(snapped);
        RotateBy(total - _rotApplied);
        _rotApplied = total;

        var m = Matrix.Identity;
        m.RotateAt(total, _rotCenter.X, _rotCenter.Y);
        var h = m.Transform(_rotHandleStart);
        PlaceRotateHandle(h);
        ((TextBlock)_rotateLabel.Child).Text = $"{Math.Round(total):0}°";
        double s = 1 / _zoom;
        _rotateLabel.RenderTransform = new ScaleTransform(s, s);
        Canvas.SetLeft(_rotateLabel, h.X + (RotateHandleSize / 2 + 6) * s);
        Canvas.SetTop(_rotateLabel, h.Y - 12 * s);
        _rotateLabel.Visibility = Visibility.Visible;
    }

    /// <summary>Ruota tratti e oggetti presi in TryBeginRotate (o dal menu) di <paramref name="delta"/> gradi attorno al centro.</summary>
    void RotateBy(double delta)
    {
        if (Math.Abs(delta) < 0.001) return;
        _moved = true;
        var m = Matrix.Identity;
        m.RotateAt(delta, _rotCenter.X, _rotCenter.Y);
        _moveStrokes.Transform(m, false);
        var total = Matrix.Identity;
        total.RotateAt(_rotApplied + delta, _rotCenter.X, _rotCenter.Y);
        foreach (var (el, center, angle) in _rotElements)
        {
            ElementFactory.SetAngle(el, angle + _rotApplied + delta);
            PlaceAroundCenter(el, total.Transform(center));
        }
    }

    void EndRotate()
    {
        _rotating = false;
        _rotateLabel.Visibility = Visibility.Collapsed;
        if (_tool == EditorTool.Select) Ink.Select(_moveStrokes, _moveElements);
        if (_moved) MarkChanged();
        UpdateRotateHandle();
    }

    /// <summary>«Ruota di 90°» dal menu contestuale (positivo: in senso orario).</summary>
    void RotateSelection(double degrees)
    {
        if (!HasSelection) return;
        var b = Ink.GetSelectionBounds();
        _moveStrokes = Ink.GetSelectedStrokes();
        _moveElements = Ink.GetSelectedElements().ToList();
        _rotCenter = new Point(b.Left + b.Width / 2, b.Top + b.Height / 2);
        _rotElements = _moveElements.OfType<FrameworkElement>().Select(el => (el, BoxCenter(el), ElementFactory.GetAngle(el))).ToList();
        _rotApplied = 0;
        _moved = false;
        ClearSelection();
        RotateBy(degrees);
        Ink.UpdateLayout();
        Ink.Select(_moveStrokes, _moveElements);
        MarkChanged();
        CommitPending();
        UpdateRotateHandle();
    }

    /// <summary>Dimensioni dell'oggetto senza la rotazione.</summary>
    static Size PlainSize(FrameworkElement el)
    {
        double w = double.IsNaN(el.Width) || el.Width <= 0 ? el.ActualWidth : el.Width;
        double h = double.IsNaN(el.Height) || el.Height <= 0 ? el.ActualHeight : el.Height;
        return new Size(w, h);
    }

    /// <summary>Riquadro che l'oggetto occupa sulla pagina (con la rotazione).</summary>
    static Rect BoxOf(FrameworkElement el)
    {
        double x = InkCanvas.GetLeft(el), y = InkCanvas.GetTop(el);
        return new Rect(new Point(double.IsNaN(x) ? 0 : x, double.IsNaN(y) ? 0 : y), ElementFactory.RotatedSize(PlainSize(el), ElementFactory.GetAngle(el)));
    }

    static Point BoxCenter(FrameworkElement el)
    {
        var r = BoxOf(el);
        return new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
    }

    static void PlaceAroundCenter(FrameworkElement el, Point center)
    {
        var size = ElementFactory.RotatedSize(PlainSize(el), ElementFactory.GetAngle(el));
        InkCanvas.SetLeft(el, center.X - size.Width / 2);
        InkCanvas.SetTop(el, center.Y - size.Height / 2);
    }

    /// <summary>
    /// Ridimensionamento con oggetti ruotati: InkCanvas darebbe all'oggetto le misure del riquadro che lo contiene
    /// (troppo grande per un oggetto inclinato). Qui si scala tutto come il rettangolo; l'oggetto ruotato cresce in
    /// proporzione (di traverso non si può deformare).
    /// </summary>
    void Ink_SelectionResizing(object? sender, InkCanvasSelectionEditingEventArgs e)
    {
        // Dagli angoli le proporzioni restano quelle di prima (e le caselle di testo ingrandiscono il testo);
        // dai lati si deforma come prima (una casella di testo cambia solo larghezza e va a capo).
        // la maniglia presa (dall'anteprima): un angolo trascinato in orizzontale o in verticale sposta un lato solo
        bool corner = _resizeHandle switch
        {
            InkCanvasSelectionHitResult.TopLeft or InkCanvasSelectionHitResult.TopRight or
                InkCanvasSelectionHitResult.BottomLeft or InkCanvasSelectionHitResult.BottomRight => true,
            InkCanvasSelectionHitResult.None => CornerDrag(e.OldRectangle, e.NewRectangle, out _, out _),
            _ => false,
        };
        bool movesLeft = _resizeHandle is InkCanvasSelectionHitResult.TopLeft or InkCanvasSelectionHitResult.BottomLeft;
        bool movesTop = _resizeHandle is InkCanvasSelectionHitResult.TopLeft or InkCanvasSelectionHitResult.TopRight;
        if (_resizeHandle == InkCanvasSelectionHitResult.None) CornerDrag(e.OldRectangle, e.NewRectangle, out movesLeft, out movesTop);
        if (corner && e.OldRectangle.Width >= 1 && e.OldRectangle.Height >= 1)
        {
            var old = e.OldRectangle;
            double left = movesLeft ? e.NewRectangle.Left : old.Left, right = movesLeft ? old.Right : e.NewRectangle.Right;
            double top = movesTop ? e.NewRectangle.Top : old.Top, bottom = movesTop ? old.Bottom : e.NewRectangle.Bottom;
            var kept = KeepAspect(old, left, top, right, bottom, movesLeft, movesTop);
            e.NewRectangle = kept;
            _cornerResize = true;
            double s = kept.Width / old.Width;
            foreach (var rtb in Ink.GetSelectedElements().OfType<RichTextBox>()) ScaleText(rtb, s);
        }

        else _cornerResize = false;

        var elements = Ink.GetSelectedElements().OfType<FrameworkElement>().ToList();
        if (!elements.Any(el => ElementFactory.GetAngle(el) != 0)) return;
        e.Cancel = true;
        Rect o = e.OldRectangle, n = e.NewRectangle;
        if (o.Width < 1 || o.Height < 1 || n.Width < 1 || n.Height < 1) return;
        double sx = n.Width / o.Width, sy = n.Height / o.Height;
        var m = new Matrix(1, 0, 0, 1, -o.Left, -o.Top);
        m.Scale(sx, sy);
        m.Translate(n.Left, n.Top);
        var strokes = Ink.GetSelectedStrokes();
        strokes.Transform(m, false);
        foreach (var el in elements)
        {
            var center = m.Transform(BoxCenter(el));
            double angle = ElementFactory.GetAngle(el);
            double quarter = Math.Abs(Math.Round(angle / 90)) % 2; // 1: di traverso di 90°, larghezza e altezza scambiate
            bool straight = Math.Abs(angle - Math.Round(angle / 90) * 90) < 0.5;
            double fw = straight ? (quarter == 1 ? sy : sx) : Math.Sqrt(sx * sy);
            double fh = straight ? (quarter == 1 ? sx : sy) : Math.Sqrt(sx * sy);
            var size = PlainSize(el);
            el.Width = Math.Max(20, size.Width * fw);
            if (el is RichTextBox rtb)
            {
                rtb.Height = double.NaN;
                if (!_cornerResize) ElementFactory.SetAutoWidth(rtb, false);
            }
            else el.Height = Math.Max(20, size.Height * fh);
            el.UpdateLayout();
            PlaceAroundCenter(el, center);
        }
        Ink.UpdateLayout();
        Ink.Select(strokes, elements);
        MarkChanged();
    }

    /// <summary>Vero se tra i due rettangoli si è mosso un angolo (un lato orizzontale e uno verticale, non i loro opposti).</summary>
    static bool CornerDrag(Rect o, Rect n, out bool movesLeft, out bool movesTop)
    {
        static bool Moved(double a, double b) => Math.Abs(a - b) > 0.01;
        bool l = Moved(o.Left, n.Left), r = Moved(o.Right, n.Right), t = Moved(o.Top, n.Top), b = Moved(o.Bottom, n.Bottom);
        movesLeft = l && !r;
        movesTop = t && !b;
        return l != r && t != b;
    }

    /// <summary>
    /// Rettangolo con le proporzioni di <paramref name="o"/>, ancorato all'angolo opposto a quello trascinato, grande
    /// quanto la proiezione di quello proposto sulla diagonale (così segue il dito in ogni direzione).
    /// </summary>
    static Rect KeepAspect(Rect o, double left, double top, double right, double bottom, bool movesLeft, bool movesTop)
    {
        double w = right - left, h = bottom - top; // positivi finché non si supera il lato opposto
        double s = (w * o.Width + h * o.Height) / (o.Width * o.Width + o.Height * o.Height);
        s = Math.Max(s, Math.Max(4 / o.Width, 4 / o.Height)); // niente capovolgimenti né selezioni ridotte a un punto
        double nw = o.Width * s, nh = o.Height * s;
        return new Rect(movesLeft ? o.Right - nw : o.Left, movesTop ? o.Bottom - nh : o.Top, nw, nh);
    }

    /// <summary>Ingrandisce (o rimpicciolisce) tutto il testo della casella, formule comprese, del fattore <paramref name="s"/>.</summary>
    static void ScaleText(RichTextBox rtb, double s)
    {
        if (Math.Abs(s - 1) < 0.001) return;
        static double Scaled(double size, double s) => Math.Clamp(Math.Round(size * s, 1), 4, 400);
        // le dimensioni si ereditano (casella → documento → paragrafi → testo): prima si leggono tutte quelle
        // impostate, poi si cambiano, altrimenti una ereditata verrebbe scalata due volte
        var local = Elements(rtb.Document.Blocks)
            .Where(te => te.ReadLocalValue(TextElement.FontSizeProperty) != DependencyProperty.UnsetValue)
            .Select(te => (te, te.FontSize)).ToList();
        double box = rtb.FontSize, doc = rtb.Document.FontSize;
        bool docLocal = rtb.Document.ReadLocalValue(FlowDocument.FontSizeProperty) != DependencyProperty.UnsetValue;
        rtb.FontSize = Scaled(box, s);
        if (docLocal) rtb.Document.FontSize = Scaled(doc, s);
        foreach (var (te, size) in local) te.FontSize = Scaled(size, s);

        static IEnumerable<TextElement> Elements(IEnumerable<Block> blocks)
        {
            foreach (var b in blocks)
            {
                yield return b;
                var children = b switch
                {
                    Paragraph p => Inlines(p.Inlines),
                    List l => l.ListItems.SelectMany(i => Elements(i.Blocks).Prepend(i)),
                    Section sec => Elements(sec.Blocks),
                    _ => Enumerable.Empty<TextElement>(),
                };
                foreach (var c in children) yield return c;
            }
        }

        static IEnumerable<TextElement> Inlines(InlineCollection inlines)
        {
            foreach (var i in inlines)
            {
                yield return i;
                if (i is Span span)
                    foreach (var c in Inlines(span.Inlines)) yield return c;
            }
        }
    }
}
