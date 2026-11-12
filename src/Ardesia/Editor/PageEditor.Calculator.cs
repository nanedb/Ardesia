using System.Windows;
using System.Windows.Controls;

namespace Ardesia.Editor;

public partial class PageEditor
{
    /// <summary>Quante caselle ha già messo la calcolatrice da quando si è sulla pagina: le successive vanno sotto.</summary>
    int _calculatorInserts;
    object? _calculatorPage;

    /// <summary>
    /// Scrive un testo (il risultato della calcolatrice) in una casella nuova nella parte visibile della pagina,
    /// senza cursore: la calcolatrice resta la finestra attiva. Più risultati di fila vanno uno sotto l'altro.
    /// </summary>
    public void InsertCalculatorText(string text)
    {
        if (!ReferenceEquals(_calculatorPage, Page)) { _calculatorPage = Page; _calculatorInserts = 0; }
        var p = VisibleOrigin() + new Vector(40, 40 + _calculatorInserts++ % 12 * TextFontSize * 1.9);
        var rtb = ElementFactory.CreateTextBox(TextFontFamily, TextFontSize, IsDarkBackground ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Black);
        InkCanvas.SetLeft(rtb, p.X);
        InkCanvas.SetTop(rtb, p.Y);
        ElementFactory.SetAutoWidth(rtb, true);
        ElementFactory.SetPlainText(rtb, text);
        AddElement(rtb);
        FitTextBox(rtb);
        MarkChanged();
    }
}
