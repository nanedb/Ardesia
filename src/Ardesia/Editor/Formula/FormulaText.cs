using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using MathBox = Ardesia.Editor.Graph.MathLayout.MathBox;

namespace Ardesia.Editor.Formula;

/// <summary>
/// Una formula dentro una casella di testo: disegna il LaTeX con <see cref="LatexLayout"/>, con il corpo e il colore
/// del testo intorno. Sta in un <see cref="InlineUIContainer"/>, centrata sull'asse della riga.
/// </summary>
public sealed class FormulaView : FrameworkElement
{
    static FormulaView()
    {
        TextElement.FontSizeProperty.OverrideMetadata(typeof(FormulaView),
            new FrameworkPropertyMetadata(ElementFactory.DefaultFontSize, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));
        TextElement.ForegroundProperty.OverrideMetadata(typeof(FormulaView),
            new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    }

    public FormulaView()
    {
    }

    public FormulaView(string latex) => Latex = latex;

    public static readonly DependencyProperty LatexProperty = DependencyProperty.Register(nameof(Latex), typeof(string), typeof(FormulaView),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Il sorgente LaTeX, senza i dollari.</summary>
    public string Latex
    {
        get => (string)GetValue(LatexProperty);
        set => SetValue(LatexProperty, value);
    }

    /// <summary>Vero se nel LaTeX c'è qualcosa che non si è capito (resta scritto in rosso).</summary>
    public bool HasErrors { get; private set; }

    MathBox? _box;
    double _baseline;

    double Size => TextElement.GetFontSize(this);

    MathBox Box(Brush brush)
    {
        var box = LatexLayout.Build(Latex, Size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip, out bool errors);
        HasErrors = errors;
        return box;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _box = Box(TextElement.GetForeground(this) ?? Brushes.Black);
        // Alta uguale sopra e sotto l'asse: la riga la centra (BaselineAlignment.Center) e l'asse coincide con quello del testo.
        double axis = LatexLayout.Axis(Size);
        double half = Math.Max(_box.Ascent - axis, _box.Descent + axis) + Size * 0.04;
        _baseline = half + axis;
        return new Size(_box.Width + Size * 0.08, 2 * half);
    }

    protected override void OnRender(DrawingContext dc)
    {
        // il colore può cambiare senza cambiare le misure (testo selezionato e ricolorato)
        var box = Box(TextElement.GetForeground(this) ?? Brushes.Black);
        box.Draw(dc, Size * 0.04, _baseline);
        // trasparente ma colpibile: il doppio clic (vedi FormulaText.FormulaAt) deve trovarla anche tra i segni
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
    }
}

/// <summary>
/// Le formule nelle caselle di testo: all'Invio il LaTeX tra dollari ($…$ o $$…$$, oppure tutta la riga se contiene
/// comandi LaTeX) diventa una <see cref="FormulaView"/>; Backspace subito dopo una formula, o il doppio clic,
/// la riporta al testo per correggerla. Nel file la formula è un Run col carattere finto <see cref="MarkerFont"/>
/// e il LaTeX come testo: le versioni vecchie di Ardesia mostrano il sorgente invece di perderlo.
/// </summary>
public static class FormulaText
{
    public const string MarkerFont = "Ardesia Formula";

    /// <summary>
    /// Converte le formule del paragrafo e dice quante sono. <c>CaretInside</c> è vero se il cursore era dentro
    /// una formula (si stava correggendo): allora l'Invio conferma la formula e non va a capo.
    /// </summary>
    public static (int Count, bool CaretInside) ConvertParagraph(Paragraph para, RichTextBox? rtb = null)
    {
        int count = 0;
        var caret = rtb?.CaretPosition;
        int caretFromEnd = caret != null && caret.Paragraph == para ? caret.GetOffsetToPosition(para.ContentEnd) : -1;
        TextPointer? caretAfter = null;
        bool caretInside = false;

        var pos = para.ContentStart;
        while (FindDollar(pos, para.ContentEnd) is { } open)
        {
            bool dbl = CharAfter(open, 1) == '$';
            var contentStart = open.GetPositionAtOffset(dbl ? 2 : 1)!;
            var close = FindDollar(contentStart, para.ContentEnd, dbl);
            if (close == null) break;
            var end = close.GetPositionAtOffset(dbl ? 2 : 1)!;
            var latex = new TextRange(contentStart, close).Text.Trim();
            if (latex.Length == 0 || ContainsElement(open, end))
            {
                pos = end;
                continue;
            }
            bool inside = caretFromEnd >= 0 && caret!.CompareTo(open) > 0 && caret.CompareTo(end) < 0;
            var container = Replace(open, end, latex);
            count++;
            if (inside)
            {
                caretInside = true;
                caretAfter = container.ElementEnd;
            }
            pos = container.ElementEnd;
        }

        // Riga senza dollari ma scritta in LaTeX (\frac, x^{2}…): diventa tutta una formula.
        if (count == 0 && !ContainsElement(para.ContentStart, para.ContentEnd))
        {
            var text = new TextRange(para.ContentStart, para.ContentEnd).Text;
            if (!text.Contains('$') && LatexLayout.LooksLikeLatex(text))
            {
                var trimmed = text.Trim();
                Replace(para.ContentStart, para.ContentEnd, trimmed);
                count = 1;
                if (rtb != null) rtb.CaretPosition = para.ContentEnd;
                return (count, false);
            }
        }

        if (count > 0 && rtb != null && caretFromEnd >= 0)
        {
            if (caretInside && caretAfter != null) rtb.CaretPosition = caretAfter;
            else if (para.ContentEnd.GetPositionAtOffset(-caretFromEnd) is { } p) rtb.CaretPosition = p;
        }
        return (count, caretInside);
    }

    /// <summary>Sostituisce il testo tra <paramref name="start"/> ed <paramref name="end"/> con la formula, col corpo e il colore che aveva.</summary>
    static InlineUIContainer Replace(TextPointer start, TextPointer end, string latex)
    {
        var run = start.GetAdjacentElement(LogicalDirection.Forward) as Run ?? start.Parent as TextElement;
        double size = run?.FontSize ?? ElementFactory.DefaultFontSize;
        var brush = run?.Foreground;
        new TextRange(start, end).Text = "";
        var view = new FormulaView(latex);
        var container = new InlineUIContainer(view, start) { BaselineAlignment = BaselineAlignment.Center };
        container.FontSize = size;
        if (brush != null) container.Foreground = brush;
        return container;
    }

    static char? CharAfter(TextPointer p, int offset)
    {
        if (p.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text) return null;
        var s = p.GetTextInRun(LogicalDirection.Forward);
        return offset < s.Length ? s[offset] : null;
    }

    /// <summary>Il prossimo «$» (o «$$») non preceduto da «\», dentro lo stesso paragrafo.</summary>
    static TextPointer? FindDollar(TextPointer from, TextPointer limit, bool dbl = false)
    {
        var p = from;
        while (p != null && p.CompareTo(limit) < 0)
        {
            if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var s = p.GetTextInRun(LogicalDirection.Forward);
                for (int i = 0; i < s.Length; i++)
                {
                    if (s[i] != '$' || i > 0 && s[i - 1] == '\\') continue;
                    if (dbl && (i + 1 >= s.Length || s[i + 1] != '$')) continue;
                    var at = p.GetPositionAtOffset(i);
                    if (at == null || at.CompareTo(limit) >= 0) return null;
                    return at;
                }
            }
            p = p.GetNextContextPosition(LogicalDirection.Forward);
        }
        return null;
    }

    static bool ContainsElement(TextPointer start, TextPointer end)
    {
        for (var p = start; p != null && p.CompareTo(end) < 0; p = p.GetNextContextPosition(LogicalDirection.Forward))
            if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.EmbeddedElement) return true;
        return false;
    }

    /// <summary>La formula subito prima del cursore, se c'è.</summary>
    public static InlineUIContainer? FormulaBefore(TextPointer caret)
    {
        if (caret.GetAdjacentElement(LogicalDirection.Backward) is InlineUIContainer { Child: FormulaView } c) return c;
        // il cursore all'inizio del testo che segue la formula
        if (caret.GetPointerContext(LogicalDirection.Backward) == TextPointerContext.ElementStart &&
            caret.Parent is Run { PreviousInline: InlineUIContainer { Child: FormulaView } prev }) return prev;
        return null;
    }

    /// <summary>
    /// La formula sotto il punto. Le formule non ricevono il mouse da sole (in una RichTextBox modificabile
    /// WPF disattiva gli oggetti incorporati), quindi la si cerca col disegno.
    /// </summary>
    public static InlineUIContainer? FormulaAt(RichTextBox rtb, Point p)
    {
        DependencyObject? hit = (VisualTreeHelper.HitTest(rtb, p))?.VisualHit;
        while (hit != null && hit != rtb)
        {
            if (hit is FormulaView { Parent: InlineUIContainer c }) return c;
            hit = VisualTreeHelper.GetParent(hit);
        }
        return null;
    }

    /// <summary>Riporta la formula a testo «$…$», col cursore prima dell'ultimo dollaro. Restituisce la casella.</summary>
    public static RichTextBox? Unfold(InlineUIContainer c)
    {
        if (c.Child is not FormulaView view || FindTextBox(c) is not { } rtb) return null;
        var run = new Run("$" + view.Latex + "$") { FontSize = c.FontSize, Foreground = c.Foreground };
        if (!Swap(c, run)) return null;
        rtb.CaretPosition = run.ContentEnd.GetPositionAtOffset(-1) ?? run.ContentEnd;
        return rtb;
    }

    static RichTextBox? FindTextBox(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is RichTextBox r) return r;
            if (d is FlowDocument doc) return doc.Parent as RichTextBox ?? LogicalTreeHelper.GetParent(doc) as RichTextBox;
            d = LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    public static bool HasFormulas(FlowDocument doc) => Containers(doc).Any();

    static IEnumerable<InlineUIContainer> Containers(FlowDocument doc)
    {
        for (var p = doc.ContentStart; p != null && p.CompareTo(doc.ContentEnd) < 0; p = p.GetNextContextPosition(LogicalDirection.Forward))
            if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementStart &&
                p.GetAdjacentElement(LogicalDirection.Forward) is InlineUIContainer { Child: FormulaView } c)
                yield return c;
    }

    // ───────────────────────────── File ─────────────────────────────

    /// <summary>
    /// XAML della casella con le formule al posto giusto: il documento si copia (con XamlWriter, che conserva le formule),
    /// nella copia le formule diventano Run col carattere segnaposto e la copia si salva come fa Ardesia col testo.
    /// </summary>
    public static string SaveXaml(RichTextBox rtb, Func<FlowDocument, string> save)
    {
        if (!HasFormulas(rtb.Document)) return save(rtb.Document);
        var doc = rtb.Document;
        var copy = (FlowDocument)XamlReader.Parse(XamlWriter.Save(doc));
        // valori che la RichTextBox dà al suo documento senza scriverli: la copia avrebbe quelli predefiniti (testo giustificato)
        copy.TextAlignment = doc.TextAlignment;
        copy.FlowDirection = doc.FlowDirection;
        copy.LineHeight = doc.LineHeight;
        copy.IsHyphenationEnabled = doc.IsHyphenationEnabled;
        copy.Language = doc.Language;
        foreach (var key in rtb.Resources.Keys) copy.Resources[key] = rtb.Resources[key]; // paragrafi senza margini
        foreach (var c in Containers(copy).ToList())
            Swap(c, new Run(((FormulaView)c.Child).Latex) { FontFamily = new FontFamily(MarkerFont), FontSize = c.FontSize, Foreground = c.Foreground });
        return save(copy);
    }

    /// <summary>Mette <paramref name="replacement"/> al posto di <paramref name="old"/> nello stesso paragrafo o span.</summary>
    static bool Swap(Inline old, Inline replacement)
    {
        switch (old.Parent)
        {
            case Paragraph p:
                p.Inlines.InsertBefore(old, replacement);
                p.Inlines.Remove(old);
                return true;
            case Span s:
                s.Inlines.InsertBefore(old, replacement);
                s.Inlines.Remove(old);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Dopo il caricamento: i Run segnaposto tornano formule.</summary>
    public static void RestoreFormulas(FlowDocument doc)
    {
        var markers = new List<Run>();
        for (var p = doc.ContentStart; p != null && p.CompareTo(doc.ContentEnd) < 0; p = p.GetNextContextPosition(LogicalDirection.Forward))
            if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementStart &&
                p.GetAdjacentElement(LogicalDirection.Forward) is Run r && r.FontFamily.Source == MarkerFont)
                markers.Add(r);
        foreach (var r in markers)
        {
            var container = new InlineUIContainer(new FormulaView(r.Text)) { BaselineAlignment = BaselineAlignment.Center };
            if (r.ReadLocalValue(TextElement.FontSizeProperty) != DependencyProperty.UnsetValue) container.FontSize = r.FontSize;
            if (r.ReadLocalValue(TextElement.ForegroundProperty) != DependencyProperty.UnsetValue) container.Foreground = r.Foreground;
            Swap(r, container);
        }
    }

    /// <summary>Il testo della casella con le formule scritte in LaTeX tra dollari (per cercare, copiare, contare).</summary>
    public static string PlainText(FlowDocument doc)
    {
        var sb = new StringBuilder();
        for (var p = doc.ContentStart; p != null && p.CompareTo(doc.ContentEnd) < 0; p = p.GetNextContextPosition(LogicalDirection.Forward))
        {
            switch (p.GetPointerContext(LogicalDirection.Forward))
            {
                case TextPointerContext.Text:
                    sb.Append(p.GetTextInRun(LogicalDirection.Forward));
                    break;
                case TextPointerContext.EmbeddedElement when p.GetAdjacentElement(LogicalDirection.Forward) is FormulaView f:
                    sb.Append('$').Append(f.Latex).Append('$');
                    break;
                case TextPointerContext.ElementEnd when p.GetAdjacentElement(LogicalDirection.Forward) is Paragraph:
                    sb.AppendLine();
                    break;
            }
        }
        return sb.ToString();
    }
}
