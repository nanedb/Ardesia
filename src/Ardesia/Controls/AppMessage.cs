using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Ardesia.Controls;

public enum MessageIcon { Info, Question, Warning, Error }

/// <summary>
/// Messaggi e domande nello stile di Ardesia, al posto dei MessageBox di Windows: icona, titolo, testo e pulsanti che
/// dicono che cosa fanno («Elimina», «Resta qui»…), grandi abbastanza per il dito sulla LIM. Il pulsante predefinito
/// (Invio, colore d'accento) per le scelte che cancellano è quello che non cancella; Esc o la chiusura della finestra
/// valgono come l'ultimo pulsante.
/// </summary>
public sealed class AppMessage : Window
{
    int _result = -1;

    AppMessage(string text, string? heading, MessageIcon icon, IReadOnlyList<string> buttons, int defaultIndex)
    {
        Title = "Ardesia";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 15;
        Background = Brushes.White;
        UseLayoutRounding = true;
        if (Application.Current.TryFindResource("TextBrush") is Brush fg) Foreground = fg;
        var accent = (Brush?)Application.Current.TryFindResource("AccentBrush") ?? Brushes.Teal;

        var (glyph, color) = icon switch
        {
            MessageIcon.Warning => ("", (Brush)new SolidColorBrush(Color.FromRgb(0xC7, 0x77, 0x00))),
            MessageIcon.Error => ("", new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28))),
            MessageIcon.Question => ("", accent),
            _ => ("", accent),
        };

        var root = new Grid { Margin = new Thickness(26, 24, 26, 20) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var symbol = new TextBlock
        {
            Text = glyph, FontSize = 30, Foreground = color, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 18, 0),
            FontFamily = (FontFamily?)Application.Current.TryFindResource("IconFont") ?? new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        };
        root.Children.Add(symbol);

        var body = new StackPanel { MaxWidth = 500, MinWidth = 280 };
        if (heading != null)
            body.Children.Add(new TextBlock { Text = heading, FontSize = 19, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 21 });
        Grid.SetColumn(body, 1);
        root.Children.Add(body);

        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        for (int i = 0; i < buttons.Count; i++)
        {
            int index = i;
            var b = new Button
            {
                Content = buttons[i], MinWidth = 110, Padding = new Thickness(18, 9, 18, 9), Margin = new Thickness(10, 6, 0, 0),
                FontSize = 15, IsDefault = i == defaultIndex, IsCancel = i == buttons.Count - 1,
            };
            if (Application.Current.TryFindResource(i == defaultIndex ? "AccentButton" : "PlainButton") is Style st) b.Style = st;
            b.Click += (_, _) =>
            {
                _result = index;
                Close();
            };
            if (i == defaultIndex) Loaded += (_, _) => b.Focus();
            row.Children.Add(b);
        }
        Grid.SetRow(row, 1);
        Grid.SetColumnSpan(row, 2);
        root.Children.Add(row);
        Content = root;
        // Ctrl+C copia il testo, come nei messaggi di Windows (utile per mandare un errore a chi aiuta)
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                try { Clipboard.SetText((heading != null ? heading + "\n\n" : "") + text); } catch { /* appunti occupati */ }
            }
        };
    }

    /// <summary>
    /// Mostra il messaggio e restituisce l'indice del pulsante premuto (-1 se la finestra è stata chiusa: per chi
    /// chiama vale come l'ultimo pulsante).
    /// </summary>
    public static int Choose(Window? owner, string text, string? heading, MessageIcon icon, int defaultIndex, params string[] buttons)
    {
        var dlg = new AppMessage(text, heading, icon, buttons, defaultIndex);
        owner ??= Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        if (owner != null && owner != dlg && owner.IsLoaded && owner.IsVisible)
        {
            dlg.Owner = owner;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        dlg.ShowDialog();
        return dlg._result;
    }

    /// <summary>Solo per l'istantanea di prova (--tab Messaggio): la finestrella non modale.</summary>
    internal static AppMessage ShowForSnapshot(Window owner, string text, string heading, MessageIcon icon, params string[] buttons)
    {
        var dlg = new AppMessage(text, heading, icon, buttons, buttons.Length - 1) { Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dlg.Show();
        return dlg;
    }

    /// <summary>Un avviso con il solo «OK».</summary>
    public static void Show(Window? owner, string text, MessageIcon icon = MessageIcon.Info, string? heading = null) =>
        Choose(owner, text, heading, icon, 0, "OK");

    /// <summary>
    /// Domanda con due risposte: vero se si preme <paramref name="yes"/>. Con <paramref name="safeDefault"/> il
    /// pulsante predefinito è <paramref name="no"/> (contro i tocchi per sbaglio sulla LIM, per le scelte che cancellano).
    /// </summary>
    public static bool Ask(Window? owner, string text, string yes, string no, string? heading = null,
        MessageIcon icon = MessageIcon.Question, bool safeDefault = false) =>
        Choose(owner, text, heading, icon, safeDefault ? 1 : 0, yes, no) == 0;
}
