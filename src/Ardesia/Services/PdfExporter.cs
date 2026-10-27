using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ardesia.Editor;
using Ardesia.Models;

namespace Ardesia.Services;

public static class PdfExporter
{
    const double A4Width = 794, A4Height = 1123; // in DIP (1/96")
    static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>Esporta le pagine in un PDF vettoriale: una pagina PDF per ogni pagina del blocco appunti, con segnalibri.</summary>
    /// <remarks>Le pagine non cambiate dall'ultima volta si prendono già pronte dalla cache (<see cref="PdfPageCache"/>).</remarks>
    public static void Export(Notebook nb, IReadOnlyList<NotebookNode> pages, string path, string title)
    {
        var parts = new List<(PdfPagePart Part, string Title)>();
        foreach (var p in pages)
        {
            var key = PdfPageCache.Key(nb, p);
            var part = PdfPageCache.Load(key);
            if (part == null)
            {
                part = RenderPage(nb, p);
                PdfPageCache.Store(key, part);
            }
            parts.Add((part, p.DisplayTitle));
        }
        PdfWriter.Write(path, parts, title);
        PdfPageCache.CleanUp();
    }

    /// <summary>
    /// Prova (<c>--prova-pdf blocco.ardesia uscita.pdf</c>): esporta tutte le pagine senza cache e poi con la cache,
    /// e scrive tempi e pesi in <c>uscita.log</c>. Falso se qualcosa non va.
    /// </summary>
    public static bool SelfTest(string notebook, string pdf)
    {
        var log = new StringBuilder();
        bool ok = true;
        try
        {
            bool synthetic = notebook == "sintetico";
            var nb = synthetic ? SyntheticNotebook() : NotebookStore.Load(notebook);
            var pages = nb.AllPages().ToList();
            log.AppendLine(synthetic ? $"{pages.Count} pagine di prova" : $"{pages.Count} pagine, file {new FileInfo(notebook).Length / 1024} KB");
            if (synthetic)
            {
                // per confronto: le pagine come le disegna WPF (riferimento-N.png, 96 dpi)
                for (int i = 0; i < pages.Count; i++)
                {
                    var visual = Build(nb, pages[i], out var size);
                    var rtb = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(visual);
                    using var fs = File.Create(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(pdf))!, $"riferimento-{i + 1}.png"));
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    enc.Save(fs);
                }
            }
            foreach (var p in pages)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var part = RenderPage(nb, p);
                long bytes = part.Objects.Sum(o => (long)(o.Stream?.Length ?? 0) + o.Dict.Length);
                log.AppendLine($"  «{p.DisplayTitle}»: {part.WidthPt:0}×{part.HeightPt:0} pt, {part.Objects.Count} oggetti, " +
                               $"{bytes / 1024} KB, {sw.ElapsedMilliseconds} ms");
                PdfPageCache.Store(PdfPageCache.Key(nb, p), part);
            }
            var t = System.Diagnostics.Stopwatch.StartNew();
            Export(nb, pages, pdf, nb.Title);
            log.AppendLine($"PDF con la cache: {new FileInfo(pdf).Length / 1024} KB in {t.ElapsedMilliseconds} ms");
            // una pagina cambiata: solo quella si rifà
            if (pages.Count > 0 && pages[0].Page is { } first)
            {
                first.Created = first.Created.AddMinutes(1);
                t.Restart();
                Export(nb, pages, pdf, nb.Title);
                log.AppendLine($"Con una pagina cambiata: {t.ElapsedMilliseconds} ms");
            }
        }
        catch (Exception ex)
        {
            log.AppendLine("ERRORE " + ex);
            ok = false;
        }
        File.WriteAllText(Path.ChangeExtension(pdf, ".log"), log.ToString());
        return ok;
    }

    /// <summary>Blocco appunti di prova con tutto ciò che il PDF deve saper disegnare.</summary>
    static Notebook SyntheticNotebook()
    {
        var nb = new Notebook { Title = "Prova PDF" };
        var section = NotebookNode.NewSection("Prova", "#1E5A68");
        nb.Insert(section, null);

        static StylusPointCollection Wave(double x0, double y0, double len, double amp, bool pressure)
        {
            var pts = new StylusPointCollection();
            for (double t = 0; t <= len; t += 4)
                pts.Add(new StylusPoint(x0 + t, y0 + amp * Math.Sin(t / 30), pressure ? (float)(0.25 + 0.7 * t / len) : 0.5f));
            return pts;
        }
        static byte[] Isf(StrokeCollection sc)
        {
            using var ms = new MemoryStream();
            sc.Save(ms);
            return ms.ToArray();
        }

        // pagina 1: penne, pressione, matite, evidenziatore, tratteggi, punta quadrata, punto
        var p1 = NotebookNode.NewPage("Tratti");
        var sc = new StrokeCollection();
        sc.Add(new Stroke(Wave(60, 160, 500, 20, false), new DrawingAttributes { Color = Colors.Black, Width = 3, Height = 3, FitToCurve = true }));
        sc.Add(new Stroke(Wave(60, 230, 500, 20, true), new DrawingAttributes { Color = Colors.Blue, Width = 6, Height = 6, FitToCurve = true }));
        sc.Add(new Stroke(Wave(60, 300, 500, 20, false), new DrawingAttributes { Color = Colors.Red, Width = 2, Height = 2, IgnorePressure = true }));
        sc.Add(Pencil.Create(Wave(60, 370, 500, 20, true), Pencil.Attributes(0, 4)));
        sc.Add(Pencil.Create(Wave(60, 440, 500, 20, true), Pencil.Attributes(6, 6)));
        var hl = new DrawingAttributes { Color = Colors.Yellow, Width = 10, Height = 24, IsHighlighter = true, StylusTip = StylusTip.Rectangle };
        sc.Add(new Stroke(Wave(60, 160, 500, 0, false), hl));
        sc.Add(new Stroke(Wave(300, 175, 300, 0, false), hl.Clone()));
        var dashed = new DrawingAttributes { Color = Colors.DarkGreen, Width = 3, Height = 3 };
        Dash.Set(dashed, true);
        sc.Add(Pencil.Create(Wave(60, 510, 500, 20, false), dashed));
        var dashedPencil = Pencil.Attributes(4, 3);
        Dash.Set(dashedPencil, true);
        sc.Add(Pencil.Create(Wave(60, 570, 500, 20, false), dashedPencil));
        sc.Add(new Stroke(Wave(60, 640, 500, 20, false), new DrawingAttributes { Color = Colors.Purple, Width = 8, Height = 3, StylusTip = StylusTip.Rectangle }));
        sc.Add(new Stroke(new StylusPointCollection { new StylusPoint(620, 160) }, new DrawingAttributes { Color = Colors.Black, Width = 8, Height = 8 }));
        sc.Add(new Stroke(Wave(60, 710, 500, 20, false), new DrawingAttributes { Color = Color.FromArgb(0x80, 0xFF, 0, 0), Width = 12, Height = 12 }));
        p1.Page!.Ink = Isf(sc);
        p1.Page.Background = PageBackground.LinesNarrow;
        nb.Insert(p1, section);

        // pagina 2: lavagna scura a puntini, testo formattato, immagini (trasparente e «foto»)
        var p2 = NotebookNode.NewPage("Lavagna, testi e immagini");
        p2.Page!.Background = PageBackground.Dots;
        p2.Page.BackgroundColor = "#1F3B2D";
        var white = new StrokeCollection
        {
            new Stroke(Wave(60, 600, 500, 30, true), new DrawingAttributes { Color = Colors.White, Width = 5, Height = 5, FitToCurve = true }),
        };
        p2.Page.Ink = Isf(white);
        p2.Page.Elements.Add(new ElementData
        {
            Type = "text", X = 60, Y = 120, Width = 520,
            Content = "<Section xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Paragraph>Testo <Bold>grassetto</Bold>, " +
                      "<Italic>corsivo</Italic>, <Run Foreground=\"#FFE04040\">rosso</Run>, à è ì ò ù, x² √2 π ≠ ≤</Paragraph></Section>",
        });

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xC0, 0x1E, 0x5A, 0x68)), new Pen(Brushes.Orange, 6), new Point(100, 100), 90, 90);
            dc.DrawText(new FormattedText("PNG", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 40, Brushes.White, 1), new Point(62, 75));
        }
        var png = new RenderTargetBitmap(200, 200, 96, 96, PixelFormats.Pbgra32);
        png.Render(dv);
        nb.Media["png"] = ElementFactory.EncodePng(png);
        p2.Page.Elements.Add(new ElementData { Type = "image", X = 620, Y = 120, Width = 200, Height = 200, MediaId = "png" });

        var rnd = new Random(1);
        int pw = 1600, ph = 1200;
        var px = new byte[pw * ph * 3];
        for (int y = 0; y < ph; y++)
            for (int x = 0; x < pw; x++)
            {
                int i = (y * pw + x) * 3;
                px[i] = (byte)(128 + 100 * Math.Sin(x / 50.0) + rnd.Next(20));
                px[i + 1] = (byte)(128 + 100 * Math.Cos(y / 70.0) + rnd.Next(20));
                px[i + 2] = (byte)((x + y) / 12 % 256);
            }
        var photo = BitmapSource.Create(pw, ph, 96, 96, PixelFormats.Rgb24, null, px, pw * 3);
        var jenc = new JpegBitmapEncoder { QualityLevel = 90 };
        jenc.Frames.Add(BitmapFrame.Create(photo));
        using (var ms = new MemoryStream())
        {
            jenc.Save(ms);
            nb.Media["foto"] = ms.ToArray();
        }
        p2.Page.Elements.Add(new ElementData { Type = "image", X = 620, Y = 360, Width = 320, Height = 240, MediaId = "foto" });
        nb.Insert(p2, section);

        // pagina 3: quadretti grandi su una pagina lunga
        var p3 = NotebookNode.NewPage("Pagina lunga");
        p3.Page!.Background = PageBackground.GridXLarge;
        p3.Page.Ink = Isf(new StrokeCollection { new Stroke(Wave(60, 2400, 600, 40, false), new DrawingAttributes { Color = Colors.Black, Width = 3, Height = 3 }) });
        nb.Insert(p3, section);
        nb.FixParents();
        return nb;
    }

    /// <summary>La pagina in PDF vettoriale, senza cache.</summary>
    public static PdfPagePart RenderPage(Notebook nb, NotebookNode node)
    {
        var visual = Build(nb, node, out var size);
        return PdfVectorPage.Render(visual, size.Width, size.Height);
    }

    /// <summary>Ricostruisce la pagina fuori schermo, con le stesse proporzioni dell'editor.</summary>
    static FrameworkElement Build(Notebook nb, NotebookNode node, out Size size)
    {
        var data = node.Page ?? new PageData();
        var bg = BackgroundFactory.ParseColor(data.BackgroundColor);
        bool dark = BackgroundFactory.IsDark(bg);

        var ink = new InkCanvas
        {
            Background = BackgroundFactory.Create(data.Background, bg),
            EditingMode = InkCanvasEditingMode.None,
        };
        if (data.Ink is { Length: > 0 })
        {
            try
            {
                ink.Strokes = new StrokeCollection(new MemoryStream(data.Ink));
                Pencil.UpgradeAll(ink.Strokes);
            }
            catch
            {
                // tratti danneggiati: pagina senza disegni
            }
        }

        var header = new StackPanel();
        header.Children.Add(new TextBlock
        {
            Text = node.Title,
            FontFamily = new FontFamily("Calibri Light, Segoe UI Light"),
            FontSize = 30,
            Foreground = dark ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x32, 0x31, 0x30)),
            Margin = new Thickness(1, 3, 0, 1),
        });
        header.Children.Add(new TextBlock
        {
            Text = data.Created.ToString("dddd d MMMM yyyy  ·  HH:mm", It),
            FontSize = 13,
            Foreground = new SolidColorBrush(dark ? Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF) : Color.FromRgb(0x76, 0x76, 0x76)),
            Margin = new Thickness(0, 6, 0, 0),
        });
        InkCanvas.SetLeft(header, 48);
        InkCanvas.SetTop(header, 24);
        ink.Children.Add(header);

        foreach (var el in data.Elements)
            if (ElementFactory.Create(el, nb, export: true) is { } ui) ink.Children.Add(ui);

        var bounds = ink.Strokes.Count > 0 ? ink.Strokes.GetBounds() : Rect.Empty;
        foreach (UIElement c in ink.Children)
        {
            c.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double x = InkCanvas.GetLeft(c), y = InkCanvas.GetTop(c);
            if (double.IsNaN(x)) x = 0;
            if (double.IsNaN(y)) y = 0;
            bounds.Union(new Rect(x, y, c.DesiredSize.Width, c.DesiredSize.Height));
        }

        double w = Math.Max(A4Width, Math.Ceiling(bounds.Right + 48));
        double h = Math.Max(Math.Ceiling(bounds.Bottom + 48), w <= A4Width ? A4Height : Math.Ceiling(w * 0.7071));
        ink.Width = w;
        ink.Height = h;
        ink.Measure(new Size(w, h));
        ink.Arrange(new Rect(0, 0, w, h));
        ink.UpdateLayout();
        size = new Size(w, h);
        return ink;
    }
}

/// <summary>Scrittore PDF minimale: monta le pagine già pronte, numerando i loro oggetti; i titoli diventano segnalibri.</summary>
public static class PdfWriter
{
    public static void Write(string path, IReadOnlyList<(PdfPagePart Part, string Title)> pages, string title)
    {
        if (pages.Count == 0) throw new ArgumentException("Nessuna pagina da esportare.");
        int n = pages.Count;

        // 1 catalogo, 2 albero delle pagine, 3 informazioni; poi per ogni pagina il suo oggetto e i suoi oggetti
        var pageObj = new int[n];
        var firstLocal = new int[n];
        int next = 4;
        for (int i = 0; i < n; i++)
        {
            pageObj[i] = next++;
            firstLocal[i] = next;
            next += pages[i].Part.Objects.Count;
        }
        int outlines = next;
        int OutlineItem(int i) => outlines + 1 + i;
        int total = outlines + n;

        var offsets = new long[total + 1];
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        {
            void Raw(string s)
            {
                var b = Encoding.ASCII.GetBytes(s);
                fs.Write(b, 0, b.Length);
            }

            void Obj(int num, string body)
            {
                offsets[num] = fs.Position;
                Raw($"{num} 0 obj\n{body}\nendobj\n");
            }

            Raw("%PDF-1.4\n");
            fs.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });

            Obj(1, $"<< /Type /Catalog /Pages 2 0 R /Outlines {outlines} 0 R /PageMode /UseOutlines >>");
            Obj(2, $"<< /Type /Pages /Kids [{string.Join(" ", pageObj.Select(p => $"{p} 0 R"))}] /Count {n} >>");
            Obj(3, $"<< /Title {Text(title)} /Creator (Ardesia) /Producer (Ardesia) /CreationDate (D:{DateTime.Now:yyyyMMddHHmmss}) >>");

            for (int i = 0; i < n; i++)
            {
                var p = pages[i].Part;
                int b = firstLocal[i];
                string Refs(string dict) => LocalRef.Replace(dict, m => $"{b + int.Parse(m.Groups[1].Value)} 0 R");

                Obj(pageObj[i], $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(p.WidthPt)} {F(p.HeightPt)}] " +
                                $"/Resources {b + p.Resources} 0 R /Contents {b + p.Contents} 0 R >>");
                for (int k = 0; k < p.Objects.Count; k++)
                {
                    var o = p.Objects[k];
                    if (o.Stream == null)
                    {
                        Obj(b + k, Refs(o.Dict));
                        continue;
                    }
                    offsets[b + k] = fs.Position;
                    // «<< …» diventa «<< /Length n …»
                    Raw($"{b + k} 0 obj\n<< /Length {o.Stream.Length} {Refs(o.Dict)[2..].TrimStart()}\nstream\n");
                    fs.Write(o.Stream, 0, o.Stream.Length);
                    Raw("\nendstream\nendobj\n");
                }
            }

            Obj(outlines, $"<< /Type /Outlines /First {OutlineItem(0)} 0 R /Last {OutlineItem(n - 1)} 0 R /Count {n} >>");
            for (int i = 0; i < n; i++)
            {
                var sb = new StringBuilder($"<< /Title {Text(pages[i].Title)} /Parent {outlines} 0 R ");
                if (i > 0) sb.Append($"/Prev {OutlineItem(i - 1)} 0 R ");
                if (i < n - 1) sb.Append($"/Next {OutlineItem(i + 1)} 0 R ");
                sb.Append($"/Dest [{pageObj[i]} 0 R /XYZ 0 {F(pages[i].Part.HeightPt)} 0] >>");
                Obj(OutlineItem(i), sb.ToString());
            }

            long xref = fs.Position;
            var x = new StringBuilder($"xref\n0 {total + 1}\n0000000000 65535 f \n");
            for (int k = 1; k <= total; k++) x.Append($"{offsets[k]:D10} 00000 n \n");
            Raw(x.ToString());
            Raw($"trailer\n<< /Size {total + 1} /Root 1 0 R /Info 3 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        }

        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    static readonly Regex LocalRef = new(@"@(\d+)", RegexOptions.Compiled);

    static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Stringa PDF in UTF-16BE esadecimale (accenti e simboli inclusi).</summary>
    static string Text(string s)
    {
        var sb = new StringBuilder("<FEFF");
        foreach (var b in Encoding.BigEndianUnicode.GetBytes(s)) sb.Append(b.ToString("X2"));
        return sb.Append('>').ToString();
    }
}
