using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ardesia.Editor;
using Ardesia.Models;

namespace Ardesia.Services;

public static class PdfExporter
{
    const double A4Width = 794, A4Height = 1123; // in DIP (1/96")
    static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>Esporta le pagine in un PDF: una pagina PDF per ogni pagina del blocco appunti, con segnalibri.</summary>
    public static void Export(Notebook nb, IReadOnlyList<NotebookNode> pages, string path, string title)
    {
        var rendered = pages.Select(p => RenderPage(nb, p)).ToList();
        PdfWriter.Write(path, rendered, title);
    }

    static PdfWriter.ImagePage RenderPage(Notebook nb, NotebookNode node)
    {
        var visual = Build(nb, node, out var size);
        double scale = 2.0;
        const double maxPixels = 36_000_000;
        if (size.Width * size.Height * scale * scale > maxPixels)
            scale = Math.Sqrt(maxPixels / (size.Width * size.Height));
        int pw = (int)Math.Ceiling(size.Width * scale), ph = (int)Math.Ceiling(size.Height * scale);

        var rtb = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var bgr = new FormatConvertedBitmap(rtb, PixelFormats.Bgr24, null, 0);
        var enc = new JpegBitmapEncoder { QualityLevel = 92 };
        enc.Frames.Add(BitmapFrame.Create(bgr));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return new PdfWriter.ImagePage(ms.ToArray(), pw, ph, size.Width * 0.75, size.Height * 0.75, node.DisplayTitle);
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

/// <summary>Scrittore PDF minimale: ogni pagina è un'immagine JPEG a tutta pagina; i titoli diventano segnalibri.</summary>
public static class PdfWriter
{
    public sealed record ImagePage(byte[] Jpeg, int PixelWidth, int PixelHeight, double WidthPt, double HeightPt, string Title);

    public static void Write(string path, IReadOnlyList<ImagePage> pages, string title)
    {
        if (pages.Count == 0) throw new ArgumentException("Nessuna pagina da esportare.");
        int n = pages.Count;
        int PageObj(int i) => 4 + i * 3;
        int ContentObj(int i) => 5 + i * 3;
        int ImageObj(int i) => 6 + i * 3;
        int outlines = 4 + n * 3;
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
            Obj(2, $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, n).Select(i => $"{PageObj(i)} 0 R"))}] /Count {n} >>");
            Obj(3, $"<< /Title {Text(title)} /Creator (Ardesia) /Producer (Ardesia) /CreationDate (D:{DateTime.Now:yyyyMMddHHmmss}) >>");

            for (int i = 0; i < n; i++)
            {
                var p = pages[i];
                Obj(PageObj(i), $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(p.WidthPt)} {F(p.HeightPt)}] " +
                                $"/Resources << /XObject << /Im0 {ImageObj(i)} 0 R >> >> /Contents {ContentObj(i)} 0 R >>");
                var content = $"q {F(p.WidthPt)} 0 0 {F(p.HeightPt)} 0 0 cm /Im0 Do Q";
                Obj(ContentObj(i), $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");

                offsets[ImageObj(i)] = fs.Position;
                Raw($"{ImageObj(i)} 0 obj\n<< /Type /XObject /Subtype /Image /Width {p.PixelWidth} /Height {p.PixelHeight} " +
                    $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {p.Jpeg.Length} >>\nstream\n");
                fs.Write(p.Jpeg, 0, p.Jpeg.Length);
                Raw("\nendstream\nendobj\n");
            }

            Obj(outlines, $"<< /Type /Outlines /First {OutlineItem(0)} 0 R /Last {OutlineItem(n - 1)} 0 R /Count {n} >>");
            for (int i = 0; i < n; i++)
            {
                var sb = new StringBuilder($"<< /Title {Text(pages[i].Title)} /Parent {outlines} 0 R ");
                if (i > 0) sb.Append($"/Prev {OutlineItem(i - 1)} 0 R ");
                if (i < n - 1) sb.Append($"/Next {OutlineItem(i + 1)} 0 R ");
                sb.Append($"/Dest [{PageObj(i)} 0 R /XYZ 0 {F(pages[i].HeightPt)} 0] >>");
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

    static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Stringa PDF in UTF-16BE esadecimale (accenti e simboli inclusi).</summary>
    static string Text(string s)
    {
        var sb = new StringBuilder("<FEFF");
        foreach (var b in Encoding.BigEndianUnicode.GetBytes(s)) sb.Append(b.ToString("X2"));
        return sb.Append('>').ToString();
    }
}
