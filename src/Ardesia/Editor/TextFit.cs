using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Ardesia.Editor;

/// <summary>
/// Caselle di testo «larghe quanto il testo»: una casella nuova si allarga mentre si scrive, fino a un massimo (il
/// bordo destro della parte visibile del foglio, almeno <see cref="ElementFactory.DefaultTextWidth"/>), poi va a capo.
/// Quando la si ridimensiona a mano la larghezza resta fissa (<see cref="ElementFactory.AutoWidthProperty"/> spento).
/// Le caselle salvate fino alla 0.8.2 restano larghe come erano.
/// </summary>
public static class TextFit
{
    /// <summary>Larghezza minima di una casella vuota, in multipli della dimensione del carattere.</summary>
    const double EmptyEms = 2;

    /// <summary>Adatta la larghezza della casella al testo, senza superare <paramref name="maxWidth"/>.</summary>
    public static void Fit(RichTextBox rtb, double maxWidth)
    {
        var doc = rtb.Document;
        double natural = BlocksWidth(doc.Blocks, rtb);
        double chrome = rtb.Padding.Left + rtb.Padding.Right + rtb.BorderThickness.Left + rtb.BorderThickness.Right
                        + doc.PagePadding.Left + doc.PagePadding.Right;
        // margine interno della RichTextBox (5 px per lato, misurato), il cursore e le differenze di misura tra un
        // pezzo di testo e l'altro (crenatura)
        double slack = 14 + doc.FontSize * 0.25;
        double width = Math.Max(natural + chrome + slack, doc.FontSize * EmptyEms + chrome);
        width = Math.Max(rtb.MinWidth, Math.Min(width, Math.Max(rtb.MinWidth, maxWidth)));
        width = Math.Ceiling(width);
        if (double.IsNaN(rtb.Width) || Math.Abs(rtb.Width - width) >= 1) rtb.Width = width;
    }

    static double BlocksWidth(BlockCollection blocks, Visual dpiSource)
    {
        double max = 0;
        foreach (var b in blocks) max = Math.Max(max, BlockWidth(b, dpiSource));
        return max;
    }

    static double BlockWidth(Block b, Visual dpiSource)
    {
        double outer = b.Margin.Left + b.Margin.Right + b.Padding.Left + b.Padding.Right
                       + b.BorderThickness.Left + b.BorderThickness.Right;
        return outer + b switch
        {
            Paragraph p => Math.Max(0, p.TextIndent) + InlinesWidth(p.Inlines, dpiSource),
            List l => (double.IsNaN(l.MarkerOffset) ? 0 : l.MarkerOffset) + l.FontSize +l.ListItems.Select(i => BlocksWidth(i.Blocks, dpiSource) + i.Margin.Left + i.Padding.Left)
                .DefaultIfEmpty(0).Max(),
            Section s => BlocksWidth(s.Blocks, dpiSource),
            BlockUIContainer c => ChildWidth(c.Child),
            _ => double.PositiveInfinity, // tabelle e altro: si va al massimo
        };
    }

    /// <summary>Riga più lunga di un paragrafo (le righe si spezzano solo ai LineBreak).</summary>
    static double InlinesWidth(InlineCollection inlines, Visual dpiSource)
    {
        double max = 0, line = 0;
        Walk(inlines);
        return Math.Max(max, line);

        void Walk(InlineCollection items)
        {
            foreach (var inline in items)
            {
                switch (inline)
                {
                    case Run r:
                        line += RunWidth(r, dpiSource);
                        break;
                    case LineBreak:
                        max = Math.Max(max, line);
                        line = 0;
                        break;
                    case InlineUIContainer c:
                        line += ChildWidth(c.Child);
                        break;
                    case Span s:
                        Walk(s.Inlines);
                        break;
                }
            }
        }
    }

    static double RunWidth(Run r, Visual dpiSource)
    {
        if (string.IsNullOrEmpty(r.Text)) return 0;
        var typeface = new Typeface(r.FontFamily, r.FontStyle, r.FontWeight, r.FontStretch);
        var ft = new FormattedText(r.Text, CultureInfo.CurrentUICulture, r.FlowDirection, typeface, r.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(dpiSource).PixelsPerDip);
        return ft.WidthIncludingTrailingWhitespace;
    }

    static double ChildWidth(UIElement? child)
    {
        if (child == null) return 0;
        if (child is FrameworkElement { Width: > 0 } fe && !double.IsNaN(fe.Width)) return fe.Width + fe.Margin.Left + fe.Margin.Right;
        child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return child.DesiredSize.Width;
    }
}
