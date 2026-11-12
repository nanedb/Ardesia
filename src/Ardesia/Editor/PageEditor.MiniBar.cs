using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Ardesia.Controls;
using Ardesia.Editor.Graph;

namespace Ardesia.Editor;

/// <summary>
/// Mini barra della selezione (come quella di OneNote): compare sotto la selezione con lo strumento «Seleziona» e
/// porta i comandi più usati del menu contestuale, più «In testo» (scrittura a mano → casella di testo, col
/// riconoscitore di Windows) e «Apri il link» (indirizzo web scritto in una casella o a mano → browser).
/// Sta in <c>FloatLayer</c>, sopra il foglio ma fuori dallo zoom: resta della stessa misura sullo schermo e i suoi
/// clic non passano dai gestori del foglio (tratti, lazo, pressione prolungata).
/// </summary>
public partial class PageEditor
{
    const double MiniBarGap = 16; // distanza dalla selezione, sullo schermo (oltre le maniglie del ridimensionamento)

    readonly StackPanel _miniBarItems = new() { Orientation = Orientation.Horizontal };
    Border _miniBar = null!;
    bool _miniBarDirty = true;
    Uri? _miniBarUrl;

    void SetupMiniBar()
    {
        _miniBar = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xC6, 0xC4)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(3),
            Visibility = Visibility.Collapsed,
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.25 },
            Child = _miniBarItems,
        };
        FloatLayer.Children.Add(_miniBar);

        Ink.SelectionChanged += (_, _) =>
        {
            _miniBarDirty = true;
            _miniBarUrl = null;
            UpdateMiniBar();
        };
        Ink.SelectionMoving += (_, _) => _miniBar.Visibility = Visibility.Collapsed;
        Ink.SelectionResizing += (_, _) => _miniBar.Visibility = Visibility.Collapsed;
        Scroller.ScrollChanged += (_, _) => UpdateMiniBar();
        FloatLayer.SizeChanged += (_, _) => UpdateMiniBar();
    }

    bool MiniBarWanted => _node != null && _tool == EditorTool.Select && _drag == DragMode.None && !_rotating && HasSelection;

    /// <summary>Mostra, nasconde e mette a posto la mini barra; il contenuto si rifà solo se è cambiata la selezione.</summary>
    void UpdateMiniBar()
    {
        if (_miniBar == null) return;
        if (!MiniBarWanted)
        {
            _miniBar.Visibility = Visibility.Collapsed;
            return;
        }
        if (_miniBarDirty)
        {
            _miniBarDirty = false;
            BuildMiniBar();
        }

        var bounds = Ink.GetSelectionBounds();
        if (bounds.IsEmpty || FloatLayer.ActualWidth <= 0)
        {
            _miniBar.Visibility = Visibility.Collapsed;
            return;
        }
        var r = Ink.TransformToVisual(FloatLayer).TransformBounds(bounds);
        // spazio utile: senza le barre di scorrimento
        double areaW = Scroller.ViewportWidth > 0 ? Math.Min(FloatLayer.ActualWidth, Scroller.ViewportWidth) : FloatLayer.ActualWidth;
        double areaH = Scroller.ViewportHeight > 0 ? Math.Min(FloatLayer.ActualHeight, Scroller.ViewportHeight) : FloatLayer.ActualHeight;
        if (r.Right < 0 || r.Bottom < 0 || r.Left > areaW || r.Top > areaH)
        {
            _miniBar.Visibility = Visibility.Collapsed; // selezione fuori dalla vista
            return;
        }

        _miniBar.Visibility = Visibility.Visible;
        _miniBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _miniBar.DesiredSize;

        // la maniglia della rotazione sta sopra la selezione (sotto, se in cima alla pagina non c'è posto)
        double handle = RotateHandleGap + RotateHandleSize;
        bool handleBelow = bounds.Top - (RotateHandleGap + RotateHandleSize / 2) / _zoom < 0;
        double below = r.Bottom + MiniBarGap + (handleBelow ? handle : 0);
        double above = r.Top - MiniBarGap - size.Height - (handleBelow ? 0 : handle);
        double y;
        if (below + size.Height <= areaH - 4) y = below;                 // di solito sotto: la mano che scrive non la copre
        else if (above >= 4) y = above;                                  // in fondo alla vista: sopra
        else y = Math.Clamp(r.Bottom - size.Height - MiniBarGap, 4, Math.Max(4, areaH - size.Height - 4)); // selezione enorme: dentro
        double x = Math.Clamp(r.Left, 4, Math.Max(4, areaW - size.Width - 4));

        if (!(Math.Abs(Canvas.GetLeft(_miniBar) - x) <= 0.5)) Canvas.SetLeft(_miniBar, Math.Round(x));
        if (!(Math.Abs(Canvas.GetTop(_miniBar) - y) <= 0.5)) Canvas.SetTop(_miniBar, Math.Round(y));
    }

    void BuildMiniBar()
    {
        _miniBarItems.Children.Clear();
        _miniBarUrl = null;
        var strokes = Ink.GetSelectedStrokes();
        var elements = Ink.GetSelectedElements();
        bool onlyInk = strokes.Count > 0 && elements.Count == 0;

        if (onlyInk && TextRecognition.IsAvailable)
            AddMiniButton("", "In testo", "Trasforma la scrittura a mano in una casella di testo", ConvertSelectionToText);

        // indirizzo web: in una casella di testo, oppure scritto a mano (si legge dopo, per non rallentare la selezione)
        if (elements.OfType<RichTextBox>().Select(TextOf).Select(TextRecognition.FindUrl).FirstOrDefault(u => u != null) is { } url)
            AddLinkButton(url);
        else if (onlyInk && TextRecognition.IsAvailable && strokes.Count <= 300)
        {
            var selected = strokes;
            Dispatcher.BeginInvoke(() =>
            {
                // nel frattempo la selezione potrebbe essere cambiata
                if (!SameStrokes(selected, Ink.GetSelectedStrokes()) || Ink.GetSelectedElements().Count > 0) return;
                if (TextRecognition.FindHandwrittenUrl(selected) is { } handwritten)
                {
                    AddLinkButton(handwritten);
                    UpdateMiniBar();
                }
            }, DispatcherPriority.Background);
        }

        if (_miniBarItems.Children.Count > 0) AddMiniSeparator();
        AddMiniButton("", null, "Taglia (Ctrl+X)", () => Copy(cut: true));
        AddMiniButton("", null, "Copia (Ctrl+C)", () => Copy(cut: false));
        AddMiniButton("", null, "Duplica", DuplicateSelection);
        AddMiniButton("", null, "Ruota di 90° a destra", () => RotateSelection(90));
        AddMiniButton("", null, "Elimina (Canc)", () => DeleteSelection());
        AddMiniSeparator();
        AddMiniButton("", null, "Altri comandi", ShowMiniBarMenu);
    }

    static bool SameStrokes(StrokeCollection a, StrokeCollection b) => a.Count == b.Count && a.All(b.Contains);

    static string TextOf(RichTextBox rtb) => new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd).Text;

    void AddLinkButton(Uri url)
    {
        if (_miniBarUrl != null) return;
        _miniBarUrl = url;
        string host = url.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? url.Host[4..] : url.Host;
        if (host.Length > 28) host = host[..27] + "…";
        var button = MakeMiniButton("", "Apri " + host, "Apri nel browser: " + url.AbsoluteUri, () => OpenLink(url));
        // dopo «In testo», se c'è; altrimenti per primo
        int at = _miniBarItems.Children.Count > 0 && _miniBarItems.Children[0] is Button { Tag: "testo" } ? 1 : 0;
        _miniBarItems.Children.Insert(at, button);
        // separatore tra i comandi speciali e quelli soliti
        if (at == 0 && _miniBarItems.Children.Count > 1 && _miniBarItems.Children[1] is not Border)
            _miniBarItems.Children.Insert(1, MiniSeparator());
    }

    void OpenLink(Uri url)
    {
        if (!TextRecognition.Open(url))
            AppMessage.Show(Window.GetWindow(this), "Non riesco ad aprire il browser per " + url.AbsoluteUri, MessageIcon.Warning);
    }

    void AddMiniButton(string glyph, string? label, string tip, Action action)
    {
        var b = MakeMiniButton(glyph, label, tip, action);
        if (label == "In testo") b.Tag = "testo";
        _miniBarItems.Children.Add(b);
    }

    Button MakeMiniButton(string glyph, string? label, string tip, Action action)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 18,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (label != null)
            content.Children.Add(new TextBlock { Text = label, Margin = new Thickness(7, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = content, ToolTip = tip, Style = (Style)FindResource("MiniBarButton") };
        AutomationProperties.SetName(b, label ?? tip);
        b.Click += (_, _) => action();
        return b;
    }

    void AddMiniSeparator() => _miniBarItems.Children.Add(MiniSeparator());

    static Border MiniSeparator() => new()
    {
        Width = 1,
        Margin = new Thickness(4, 7, 4, 7),
        Background = new SolidColorBrush(Color.FromRgb(0xE1, 0xDF, 0xDD)),
    };

    /// <summary>«⋯»: il menu contestuale completo, aperto accanto alla mini barra.</summary>
    void ShowMiniBarMenu()
    {
        if (!HasSelection) return;
        var b = Ink.GetSelectionBounds();
        ShowContextMenu(new Point(b.Left + b.Width / 2, b.Top + b.Height / 2));
        if (_openMenu != null)
        {
            _openMenu.PlacementTarget = _miniBar;
            _openMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            _openMenu.HorizontalOffset = 0;
            _openMenu.VerticalOffset = 2;
        }
    }

    // ───────────── scrittura a mano → testo ─────────────

    /// <summary>
    /// Legge la scrittura a mano selezionata e la sostituisce con una casella di testo nello stesso punto, col colore
    /// dell'inchiostro e una dimensione simile a quella della scrittura. Si annulla con Ctrl+Z.
    /// </summary>
    public void ConvertSelectionToText()
    {
        var owner = Window.GetWindow(this);
        var strokes = Ink.GetSelectedStrokes();
        if (strokes.Count == 0) return;
        if (!TextRecognition.IsAvailable)
        {
            AppMessage.Show(owner, TextRecognition.NotAvailableMessage);
            return;
        }
        CommitPending();
        string text;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            text = TextRecognition.Recognize(strokes);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            text = "";
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
        if (text.Length == 0)
        {
            AppMessage.Show(owner, "Non sono riuscito a leggere la scrittura: prova a scrivere un po' più grande e staccato.");
            return;
        }

        var b = strokes.GetBounds();
        int lines = Math.Max(1, HandwritingMath.SplitLines(strokes).Count);
        // una riga scritta a mano (con le gambe di g, p, l…) è alta circa quanto il corpo del carattere
        double size = Math.Clamp(Math.Round(b.Height / lines * 0.7 / 2) * 2, 14, 96);
        var color = strokes.Select(s => s.DrawingAttributes).FirstOrDefault(d => !d.IsHighlighter && d.Color.A > 200)?.Color;
        Brush foreground = color is { } c && !(IsDarkBackground ? IsDark(c) : IsLight(c))
            ? new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B))
            : IsDarkBackground ? Brushes.White : Brushes.Black;

        ClearSelection();
        var rtb = ElementFactory.CreateTextBox(TextFontFamily, size, foreground);
        ElementFactory.SetPlainText(rtb, text);
        InkCanvas.SetLeft(rtb, Math.Max(0, b.Left - 6));
        InkCanvas.SetTop(rtb, Math.Max(0, b.Top));
        ElementFactory.SetAutoWidth(rtb, true);
        Ink.Strokes.Remove(strokes);
        AddElement(rtb);
        FitTextBox(rtb);
        MarkChanged();
        CommitPending();
        Tool = EditorTool.Select;
        Ink.Select(new StrokeCollection(), new UIElement[] { rtb });
    }

    /// <summary>Per l'istantanea <c>--tab "Mini barra"</c>: scrittura a mano selezionata, o una casella con un link.</summary>
    public void MiniBarDemo(bool link)
    {
        var o = VisibleOrigin();
        Tool = EditorTool.Select;
        if (link)
        {
            var rtb = ElementFactory.CreateTextBox(TextFontFamily, TextFontSize, Brushes.Black);
            ElementFactory.SetPlainText(rtb, "Esercizi interattivi su www.geogebra.org");
            InkCanvas.SetLeft(rtb, o.X + 80);
            InkCanvas.SetTop(rtb, o.Y + 330);
            ElementFactory.SetAutoWidth(rtb, true);
            AddElement(rtb);
            FitTextBox(rtb);
            UpdateLayout();
            Ink.Select(new StrokeCollection(), new UIElement[] { rtb });
        }
        else
        {
            var ink = GraphSelfTest.StrokeFont.Write("sei fine", 44, o.X + 80, o.Y + 330);
            foreach (var s in ink) s.DrawingAttributes = Ink.DefaultDrawingAttributes.Clone();
            Ink.Strokes.Add(ink);
            Ink.Select(ink);
        }
    }

    static double Luma(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
    static bool IsLight(Color c) => Luma(c) > 0.85;
    static bool IsDark(Color c) => Luma(c) < 0.2;
}
