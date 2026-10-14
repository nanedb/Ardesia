using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ardesia.Models;

namespace Ardesia.Editor;

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
                if (d.MediaId != null && nb.Media.TryGetValue(d.MediaId, out var bytes))
                {
                    try
                    {
                        var bmp = LoadBitmap(bytes);
                        ui = CreateImage(bmp, d.MediaId,
                            d.Width > 0 ? d.Width : bmp.PixelWidth,
                            d.Height > 0 ? d.Height : bmp.PixelHeight);
                    }
                    catch
                    {
                        // immagine danneggiata
                    }
                }
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
            RichTextBox r => new ElementData
            {
                Type = "text", X = x, Y = y,
                Width = double.IsNaN(r.Width) ? r.ActualWidth : r.Width,
                Content = GetXaml(r),
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
