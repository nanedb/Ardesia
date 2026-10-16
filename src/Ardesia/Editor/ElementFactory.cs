using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    public const string DefaultFont = "Calibri";
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

    public static string GetXaml(RichTextBox rtb)
    {
        var range = new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd);
        using var ms = new MemoryStream();
        range.Save(ms, DataFormats.Xaml);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static void SetXaml(RichTextBox rtb, string xaml)
    {
        try
        {
            var range = new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd);
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xaml));
            range.Load(ms, DataFormats.Xaml);
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
        string.IsNullOrWhiteSpace(new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd).Text);

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
                var rtb = CreateTextBox(new FontFamily(DefaultFont), DefaultFontSize, Brushes.Black,
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
                ui = CreateFunctionGraph(GraphState.FromJson(d.Content), d.Width > 0 ? d.Width : 600, d.Height > 0 ? d.Height : 450);
                break;
            default:
                ui = new PreservedElement(d with { });
                break;
        }
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
        return e switch
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
