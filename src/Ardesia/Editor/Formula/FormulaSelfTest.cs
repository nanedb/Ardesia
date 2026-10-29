using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ardesia.Editor.Formula;

/// <summary>Prova delle formule LaTeX nelle caselle di testo (Ardesia.exe --prova-formule [registro.txt]).</summary>
public static class FormulaSelfTest
{
    /// <summary>Formule da scuola per l'immagine di controllo e per la prova del lettore.</summary>
    public static readonly string[] Samples =
    {
        @"x_{1,2} = \frac{-b \pm \sqrt{b^2 - 4ac}}{2a}",
        @"\lim_{x \to 0} \frac{\sin x}{x} = 1",
        @"\sum_{k=1}^{n} k = \frac{n(n+1)}{2}",
        @"\int_0^1 x^2 \, dx = \frac{1}{3}",
        @"\begin{cases} 2x + y = 3 \\ x - y = 0 \end{cases}",
        @"A = \begin{pmatrix} 1 & 2 \\ 3 & 4 \end{pmatrix}",
        @"\left( \frac{a}{b} \right)^2 = \frac{a^2}{b^2}",
        @"\vec{F} = m \vec{a}, \quad \overline{AB} \perp \overline{CD}",
        @"\sqrt[3]{8} = 2, \quad \alpha + \beta = \pi",
        @"f'(x) = 3x^2 - 2, \quad x \in \mathbb{R}",
        @"\binom{n}{k} = \frac{n!}{k!(n-k)!}",
        @"\begin{aligned} (a+b)^2 &= a^2 + 2ab + b^2 \\ &\geq 4ab \end{aligned}",
        @"\log_2 8 = 3, \quad e^{i\pi} + 1 = 0",
        @"|x - 1| < 3 \iff -2 < x < 4",
    };

    public static string Run(out int failures)
    {
        var log = new StringBuilder();
        int fail = 0;

        void Check(bool ok, string what)
        {
            log.AppendLine((ok ? "ok    " : "ERRORE") + "  " + what);
            if (!ok) fail++;
        }

        // ── il carattere di LaTeX incorporato si trova, in tutte e quattro le forme ──
        foreach (var family in Fonts.GetFontFamilies(new Uri("pack://application:,,,/Assets/Fonts/")))
            log.AppendLine("        carattere incorporato: " + family.Source + " = " + string.Join(", ", family.FamilyNames.Values));
        foreach (var (style, weight, file) in new[]
                 {
                     (FontStyles.Normal, FontWeights.Normal, "lmroman10-regular"), (FontStyles.Italic, FontWeights.Normal, "lmroman10-italic"),
                     (FontStyles.Normal, FontWeights.Bold, "lmroman10-bold"), (FontStyles.Italic, FontWeights.Bold, "lmroman10-bolditalic"),
                 })
        {
            bool found = new Typeface(LatexLayout.MathFont, style, weight, FontStretches.Normal).TryGetGlyphTypeface(out var g);
            Check(found && g.FontUri.ToString().Contains(file, StringComparison.OrdinalIgnoreCase),
                $"Latin Modern {style} {weight}: {(found ? g.FontUri.ToString() : "non trovato")}");
        }

        // Latin Modern Math: si trova e dà davvero i simboli che Latin Modern Roman non ha (si confrontano le larghezze)
        var mathFamily = new FontFamily(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Latin Modern Math");
        bool mathFound = new Typeface(mathFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out var lmMath) &&
                         lmMath.FontUri.ToString().Contains("latinmodern-math", StringComparison.OrdinalIgnoreCase);
        Check(mathFound, "Latin Modern Math incorporato");
        if (mathFound)
            foreach (var sample in new[] { "∑", "∫", "≤", ArdesiaFonts.MathItalic("x"), ArdesiaFonts.MathItalic("α") })
            {
                int cp = char.ConvertToUtf32(sample, 0);
                bool has = lmMath.CharacterToGlyphMap.TryGetValue(cp, out var glyph);
                double expected = has ? lmMath.AdvanceWidths[glyph] * 100 : -1;
                double actual = new FormattedText(sample, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(ArdesiaFonts.LatinModern, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 100, Brushes.Black, 1).WidthIncludingTrailingWhitespace;
                Check(has && Math.Abs(actual - expected) < 0.5, $"«{sample}» (U+{cp:X4}) da Latin Modern Math: larghezza {actual:0.0} (attesa {expected:0.0})");
            }

        // ── il lettore capisce le formule di scuola, e segnala quelle sbagliate ──
        foreach (var s in Samples)
        {
            var box = LatexLayout.Build(s, 24, Brushes.Black, 1, out bool errors);
            Check(!errors && box.Width > 0 && box.Height > 0, $"si compone senza errori: {s}");
        }
        LatexLayout.Build(@"\fracc{1}{2}", 24, Brushes.Black, 1, out bool unknown);
        Check(unknown, "comando sconosciuto segnalato");
        LatexLayout.Build(@"\frac{1}{2", 24, Brushes.Black, 1, out bool open);
        Check(open, "graffa non chiusa segnalata (ma si disegna lo stesso)");
        var frac = LatexLayout.Build(@"\frac{1}{2}", 24, Brushes.Black, 1, out _);
        var plain = LatexLayout.Build("x", 24, Brushes.Black, 1, out _);
        Check(frac.Height > plain.Height * 1.4, "la frazione è più alta di una lettera");
        var paren = LatexLayout.Build(@"(\frac{1}{2})", 24, Brushes.Black, 1, out _);
        Check(paren.Height >= frac.Height, "le parentesi crescono intorno alla frazione anche senza \\left");

        Check(LatexLayout.LooksLikeLatex(@"\frac{a}{b}"), "\\frac si riconosce come LaTeX");
        Check(LatexLayout.LooksLikeLatex("x^{2}+1"), "x^{2} si riconosce come LaTeX");
        Check(!LatexLayout.LooksLikeLatex("Teorema di Pitagora: a² + b² = c²"), "il testo normale non è LaTeX");
        Check(!LatexLayout.LooksLikeLatex(@"C:\cartella\file"), "una barra rovesciata qualsiasi non è LaTeX");

        // ── formule LaTeX come funzioni del grafico (Grafico e GeoGebra da una casella di testo) ──
        (string Latex, double X, double Expected)[] graphs =
        {
            (@"y = \frac{1}{x}", 4, 0.25),
            (@"$y = \frac{x^2 - 1}{x + 1}$", 3, 2),
            (@"$$f(x) = \sqrt{x + 7}$$", 9, 4),
            (@"y = \sqrt[3]{x}", 8, 2),
            (@"y = x^{2} - 4", 3, 5),
            (@"y = \sin x + \cos x", 0, 1),
            (@"y = \sen(2x)", Math.PI / 4, 1),
            (@"y = 2 \cdot x", 3, 6),
            (@"y = \left| x - 1 \right|", -2, 3),
            (@"y = e^{-x}", 0, 1),
            (@"y = \ln x", Math.E, 1),
            (@"y = 1{,}5x", 2, 3),
            (@"y = \pi x", 1, Math.PI),
            (@"y = \dfrac{1}{2}x^{2}", 2, 2),
        };
        foreach (var (latex, x, expected) in graphs)
        {
            var linear = LatexToPlain.Line(latex);
            try
            {
                var c = Graph.MathParser.ParseLine(Graph.MathText.Normalize(linear));
                double y = c.Compile(c.Parameters)(x, c.Parameters.Select(_ => 1.0).ToArray());
                Check(Math.Abs(y - expected) < 1e-9, $"grafico da «{latex}» → «{linear}»: in x = {x:0.###} vale {y:0.######} (atteso {expected:0.######})");
            }
            catch (Graph.MathSyntaxException ex)
            {
                Check(false, $"grafico da «{latex}» → «{linear}»: {ex.Message}");
            }
        }
        Check(LatexToPlain.Line("y = x² − 4") == "y = x² − 4", "una funzione scritta senza LaTeX resta com'è");

        // casella con una formula già convertita: il grafico la legge
        var graphBox = ElementFactory.CreateTextBox(ElementFactory.DefaultFontFamily, 24, Brushes.Black);
        ElementFactory.SetPlainText(graphBox, "$y = \\frac{1}{x}$\ny = x + 1");
        FormulaText.ConvertParagraph((Paragraph)graphBox.Document.Blocks.FirstBlock);
        var fnText = FormulaText.FunctionText(graphBox.Document).Trim();
        Check(FormulaText.HasFormulas(graphBox.Document) && fnText == "y = ((1)/(x))\ny = x + 1", $"funzioni dalla casella con formule: «{fnText.Replace("\n", " | ")}»");

        // ── conversione in una casella vera ──
        var rtb = ElementFactory.CreateTextBox(ElementFactory.DefaultFontFamily, 24, Brushes.Black);
        ElementFactory.SetPlainText(rtb, @"L'area è $\pi r^2$ e il volume $$\frac{4}{3}\pi r^3$$.");
        var para = (Paragraph)rtb.Document.Blocks.FirstBlock;
        int n = FormulaText.ConvertParagraph(para, rtb).Count;
        Check(n == 2, $"due formule convertite (convertite: {n})");
        Check(FormulaText.HasFormulas(rtb.Document), "la casella contiene formule");
        var text = FormulaText.PlainText(rtb.Document).Trim();
        Check(text == @"L'area è $\pi r^2$ e il volume $\frac{4}{3}\pi r^3$.", $"testo con le formule: «{text}»");

        // senza dollari ma con comandi: tutta la riga
        var rtb2 = ElementFactory.CreateTextBox(ElementFactory.DefaultFontFamily, 24, Brushes.Black);
        ElementFactory.SetPlainText(rtb2, @"\frac{a}{b} + \sqrt{2}");
        Check(FormulaText.ConvertParagraph((Paragraph)rtb2.Document.Blocks.FirstBlock, rtb2).Count == 1 &&
              FormulaText.PlainText(rtb2.Document).Trim() == @"$\frac{a}{b} + \sqrt{2}$", "riga tutta in LaTeX senza dollari");

        // testo normale: niente
        var rtb3 = ElementFactory.CreateTextBox(ElementFactory.DefaultFontFamily, 24, Brushes.Black);
        ElementFactory.SetPlainText(rtb3, "Costa 5 $ o 4 €");
        Check(FormulaText.ConvertParagraph((Paragraph)rtb3.Document.Blocks.FirstBlock, rtb3).Count == 0, "un dollaro solo non fa una formula");

        // ── salvataggio e ricaricamento ──
        var xaml = ElementFactory.GetXaml(rtb);
        Check(xaml.Contains(FormulaText.MarkerFont) && xaml.Contains(@"\pi r^2"), "nel file la formula è testo segnaposto");
        Check(!xaml.Contains("FormulaView"), "nel file non finisce l'oggetto WPF");
        static string Head(string x) => x[..x.IndexOf("<Paragraph", StringComparison.Ordinal)];
        var plainHead = Head(ElementFactory.GetXaml(rtb3));
        Check(Head(xaml) == plainHead, "con le formule il documento si salva con le stesse impostazioni del testo normale");
        if (Head(xaml) != plainHead) log.AppendLine("        testo normale: " + plainHead);
        var back = ElementFactory.CreateTextBox(ElementFactory.DefaultFontFamily, 24, Brushes.Black);
        ElementFactory.SetXaml(back, xaml);
        Check(FormulaText.PlainText(back.Document).Trim() == text, "ricaricata, la casella è uguale");
        Check(FormulaText.HasFormulas(back.Document), "ricaricata, le formule sono di nuovo formule");
        var firstRun = back.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<Run>().First();
        Check(xaml.Contains("#Latin Modern Roman 10") &&
              new Typeface(firstRun.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out var reloaded) &&
              reloaded.FontUri.ToString().Contains("lmroman10", StringComparison.OrdinalIgnoreCase),
            "ricaricato, il testo è ancora in Latin Modern (il carattere incorporato)");
        Check(ElementFactory.GetXaml(back) == xaml, "risalvata, il file è identico");
        var onlyFormula = ElementFactory.CreateTextBox(ElementFactory.DefaultFontFamily, 24, Brushes.Black);
        ElementFactory.SetPlainText(onlyFormula, "$x$");
        FormulaText.ConvertParagraph((Paragraph)onlyFormula.Document.Blocks.FirstBlock);
        Check(!ElementFactory.IsEmpty(onlyFormula), "una casella con solo una formula non è vuota");
        Check(GetLocalParagraphMargin(back), "i paragrafi restano senza margini");

        // ── ritorno a testo ──
        var first = FirstFormula(back);
        Check(first != null && FormulaText.Unfold(first) == back, "la formula torna testo nella sua casella");
        Check(FormulaText.PlainText(back.Document).Trim() == text && FirstFormula(back) != null && CountFormulas(back) == 1,
            "tornata testo, il LaTeX è intatto e resta l'altra formula");
        var para2 = (Paragraph)back.Document.Blocks.FirstBlock;
        Check(FormulaText.ConvertParagraph(para2, back).Count == 1 && CountFormulas(back) == 2, "e col nuovo Invio ridiventa formula");

        log.AppendLine();
        log.AppendLine(xaml);
        failures = fail;
        log.Insert(0, fail == 0 ? "TUTTO BENE\r\n\r\n" : $"{fail} PROVE ANDATE MALE\r\n\r\n");
        return log.ToString();
    }

    static bool GetLocalParagraphMargin(RichTextBox rtb) =>
        rtb.Document.Blocks.OfType<Paragraph>().All(p => p.Margin == new Thickness(0));

    static InlineUIContainer? FirstFormula(RichTextBox rtb) =>
        rtb.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<InlineUIContainer>().FirstOrDefault();

    static int CountFormulas(RichTextBox rtb) =>
        rtb.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<InlineUIContainer>().Count();

    /// <summary>Immagine di controllo: le formule di prova dentro una casella di testo, tra parole normali.</summary>
    public static void RenderSample(string path)
    {
        var rtb = ElementFactory.CreateTextBox(ElementFactory.DefaultFontFamily, 24, Brushes.Black, 900, export: true);
        var lines = new List<string> { "Formule nel testo: l'area del cerchio è $\\pi r^2$, la diagonale $d = \\sqrt{2}\\,l$ e $\\frac{1}{2}$ di torta." };
        lines.AddRange(Samples.Select(s => "$" + s + "$"));
        ElementFactory.SetPlainText(rtb, string.Join("\n", lines));
        foreach (var p in rtb.Document.Blocks.OfType<Paragraph>().ToList()) FormulaText.ConvertParagraph(p);
        var host = new Border { Background = Brushes.White, Padding = new Thickness(16), Child = rtb };
        host.Measure(new Size(940, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(host);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
