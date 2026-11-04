using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ardesia.Editor.Formula;
using Ardesia.Editor.Graph;
using Ardesia.Models;

namespace Ardesia.Editor;

/// <summary>Grafico GeoGebra sulla pagina: un'immagine che ricorda lo stato di GeoGebra, per riaprirlo modificabile.</summary>
public class GraphImage : Image
{
    /// <summary>Costruzione GeoGebra in formato .ggb codificato in Base64.</summary>
    public string State { get; set; } = "";
}

/// <summary>
/// Oggetto di un tipo che questa versione di Ardesia non conosce (salvato da una versione più nuova):
/// non si vede, ma si riscrive invariato al salvataggio invece di andare perso.
/// </summary>
public sealed class PreservedElement : FrameworkElement
{
    public PreservedElement(ElementData data)
    {
        Data = data;
        Visibility = Visibility.Collapsed;
    }

    public ElementData Data { get; }
}

/// <summary>Crea gli oggetti WPF della pagina a partire dai dati salvati, e viceversa.</summary>
public static class ElementFactory
{
    /// <summary>Carattere dei testi nuovi: Latin Modern, quello di LaTeX (le caselle già scritte tengono il loro).</summary>
    public const string DefaultFont = ArdesiaFonts.LatinModernName;
    public static FontFamily DefaultFontFamily => ArdesiaFonts.LatinModern;
    public const double DefaultFontSize = 24;
    public const double DefaultTextWidth = 520;

    public static RichTextBox CreateTextBox(FontFamily family, double size, Brush foreground,
        double width = DefaultTextWidth, bool export = false)
    {
        var rtb = new RichTextBox
        {
            Width = width,
            MinWidth = 60,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            FontFamily = family,
            FontSize = size,
            Foreground = foreground,
            CaretBrush = foreground,
            AcceptsTab = true,
        };
        rtb.Document.PagePadding = new Thickness(0);
        rtb.Document.FontFamily = family;
        rtb.Document.FontSize = size;
        rtb.Document.Foreground = foreground;

        var paragraph = new Style(typeof(Paragraph));
        paragraph.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0)));
        rtb.Resources.Add(typeof(Paragraph), paragraph);

        if (export)
        {
            rtb.IsReadOnly = true;
            rtb.BorderThickness = new Thickness(0);
        }
        else
        {
            rtb.Language = XmlLanguage.GetLanguage("it-IT");
            SpellCheck.SetIsEnabled(rtb, true);
        }
        return rtb;
    }

    public static string GetXaml(RichTextBox rtb) => FormulaText.SaveXaml(rtb, doc =>
    {
        var range = new TextRange(doc.ContentStart, doc.ContentEnd);
        using var ms = new MemoryStream();
        range.Save(ms, DataFormats.Xaml);
        return Encoding.UTF8.GetString(ms.ToArray());
    });

    public static void SetXaml(RichTextBox rtb, string xaml)
    {
        try
        {
            var range = new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd);
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xaml));
            range.Load(ms, DataFormats.Xaml);
            ArdesiaFonts.FixAfterLoad(rtb.Document);
            FormulaText.RestoreFormulas(rtb.Document);
        }
        catch
        {
            // contenuto non valido: casella vuota
        }
    }

    public static void SetPlainText(RichTextBox rtb, string text)
    {
        rtb.Document.Blocks.Clear();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            rtb.Document.Blocks.Add(new Paragraph(new Run(line)));
    }

    public static bool IsEmpty(RichTextBox rtb) =>
        string.IsNullOrWhiteSpace(new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd).Text) &&
        !FormulaText.HasFormulas(rtb.Document);

    public static BitmapSource LoadBitmap(byte[] bytes)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = new MemoryStream(bytes);
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    public static Image CreateImage(BitmapSource source, string mediaId, double width, double height) => new()
    {
        Source = source,
        Stretch = Stretch.Fill,
        Width = width,
        Height = height,
        Tag = mediaId,
    };

    public static GraphImage CreateGraph(BitmapSource source, string mediaId, string state, double width, double height) => new()
    {
        Source = source,
        Stretch = Stretch.Fill,
        Width = width,
        Height = height,
        Tag = mediaId,
        State = state,
    };

    public static FunctionGraph CreateFunctionGraph(GraphState state, double width, double height) => new()
    {
        State = state,
        Width = width,
        Height = height,
    };

    public static UIElement? Create(ElementData d, Notebook nb, bool export = false)
    {
        UIElement? ui = null;
        switch (d.Type)
        {
            case "text":
                var rtb = CreateTextBox(DefaultFontFamily, DefaultFontSize, Brushes.Black,
                    d.Width > 0 ? d.Width : DefaultTextWidth, export);
                if (!string.IsNullOrEmpty(d.Content)) SetXaml(rtb, d.Content);
                ui = rtb;
                break;
            case "image":
            case "geogebra":
                if (d.MediaId != null && nb.Media.TryGetValue(d.MediaId, out var bytes))
                {
                    try
                    {
                        var bmp = LoadBitmap(bytes);
                        double w = d.Width > 0 ? d.Width : bmp.PixelWidth, h = d.Height > 0 ? d.Height : bmp.PixelHeight;
                        ui = d.Type == "geogebra"
                            ? CreateGraph(bmp, d.MediaId, d.Content ?? "", w, h)
                            : CreateImage(bmp, d.MediaId, w, h);
                    }
                    catch
                    {
                        // immagine danneggiata
                    }
                }
                break;
            case "graph":
                var graph = CreateFunctionGraph(GraphState.FromJson(d.Content), d.Width > 0 ? d.Width : 600, d.Height > 0 ? d.Height : 450);
                graph.Export = export;
                ui = graph;
                break;
            default:
                ui = new PreservedElement(d with { });
                break;
        }
        if (ui is RichTextBox box && d.AutoWidth) SetAutoWidth(box, true);
        if (ui is FrameworkElement rotated && d.Angle != 0) SetAngle(rotated, d.Angle);
        if (ui != null)
        {
            InkCanvas.SetLeft(ui, d.X);
            InkCanvas.SetTop(ui, d.Y);
        }
        return ui;
    }

    public static ElementData? ToData(UIElement e)
    {
        double x = InkCanvas.GetLeft(e), y = InkCanvas.GetTop(e);
        if (double.IsNaN(x)) x = 0;
        if (double.IsNaN(y)) y = 0;
        var data = e switch
        {
            PreservedElement p => p.Data with { X = x, Y = y },
            FunctionGraph f => new ElementData
            {
                Type = "graph", X = x, Y = y,
                Width = double.IsNaN(f.Width) ? f.ActualWidth : f.Width,
                Height = double.IsNaN(f.Height) ? f.ActualHeight : f.Height,
                Content = f.State.ToJson(),
            },
            RichTextBox r => new ElementData
            {
                Type = "text", X = x, Y = y,
                Width = double.IsNaN(r.Width) ? r.ActualWidth : r.Width,
                Content = GetXaml(r),
            },
            GraphImage { Tag: string gid } g => new ElementData
            {
                Type = "geogebra", X = x, Y = y,
                Width = double.IsNaN(g.Width) ? g.ActualWidth : g.Width,
                Height = double.IsNaN(g.Height) ? g.ActualHeight : g.Height,
                MediaId = gid,
                Content = g.State,
            },
            Image { Tag: string id } i => new ElementData
            {
                Type = "image", X = x, Y = y,
                Width = double.IsNaN(i.Width) ? i.ActualWidth : i.Width,
                Height = double.IsNaN(i.Height) ? i.ActualHeight : i.Height,
                MediaId = id,
            },
            _ => null,
        };
        if (data == null || e is PreservedElement || e is not FrameworkElement fe) return data;
        data.Angle = GetAngle(fe);
        data.AutoWidth = e is RichTextBox r2 && GetAutoWidth(r2);
        return data;
    }

    // ───── Caselle larghe quanto il testo (Editor/TextFit.cs) ─────

    public static readonly DependencyProperty AutoWidthProperty =
        DependencyProperty.RegisterAttached("AutoWidth", typeof(bool), typeof(ElementFactory), new PropertyMetadata(false));

    public static bool GetAutoWidth(RichTextBox rtb) => (bool)rtb.GetValue(AutoWidthProperty);
    public static void SetAutoWidth(RichTextBox rtb, bool value) => rtb.SetValue(AutoWidthProperty, value);

    // ───── Rotazione degli oggetti ─────
    // È una LayoutTransform: per InkCanvas l'oggetto occupa il riquadro che contiene la figura ruotata, quindi
    // selezione, lazo e spostamento funzionano come per gli altri. X e Y salvati sono l'angolo di quel riquadro.

    /// <summary>Angolo in gradi (senso orario, tra −180 e 180) dell'oggetto.</summary>
    public static double GetAngle(FrameworkElement fe) => fe.LayoutTransform is RotateTransform r ? r.Angle : 0;

    public static void SetAngle(FrameworkElement fe, double angle)
    {
        angle = NormalizeAngle(angle);
        fe.LayoutTransform = Math.Abs(angle) < 0.01 ? Transform.Identity : new RotateTransform(angle);
    }

    public static double NormalizeAngle(double a)
    {
        a %= 360;
        if (a > 180) a -= 360;
        if (a <= -180) a += 360;
        return Math.Round(a, 2);
    }

    /// <summary>Riquadro occupato da un oggetto di dimensioni <paramref name="size"/> ruotato di <paramref name="angle"/> gradi.</summary>
    public static Size RotatedSize(Size size, double angle)
    {
        double r = angle * Math.PI / 180, c = Math.Abs(Math.Cos(r)), s = Math.Abs(Math.Sin(r));
        return new Size(size.Width * c + size.Height * s, size.Width * s + size.Height * c);
    }

    public static byte[] EncodePng(BitmapSource source)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
