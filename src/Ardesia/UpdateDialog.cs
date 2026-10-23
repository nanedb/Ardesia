using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Ardesia.Services;

namespace Ardesia;

/// <summary>
/// «È disponibile Ardesia X.Y.Z»: le novità (le note della release su GitHub, scritte per i docenti) e il pulsante per
/// aggiornare. Se Ardesia è installata si aggiorna da sola; la versione portatile apre la pagina della release.
/// </summary>
public sealed class UpdateDialog : Window
{
    /// <summary>Vero se si è scelto di aggiornare (o di aprire la pagina, per la versione portatile).</summary>
    public bool Accepted { get; private set; }

    public UpdateDialog(ReleaseInfo release, bool installed)
    {
        Title = "Ardesia";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 15;
        Background = Brushes.White;
        UseLayoutRounding = true;
        if (Application.Current.TryFindResource("TextBrush") is Brush fg) Foreground = fg;

        var root = new DockPanel { Margin = new Thickness(28, 24, 28, 20) };

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(head, Dock.Top);
        head.Children.Add(new TextBlock { Text = $"È disponibile Ardesia {release.Version}", FontSize = 21, FontWeight = FontWeights.SemiBold });
        head.Children.Add(new TextBlock
        {
            Text = $"Sul computer c'è la {Updates.Current.ToString(3)}. Ecco le novità:",
            Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 0),
        });
        root.Children.Add(head);

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var update = MakeButton(installed ? "Aggiorna adesso" : "Apri la pagina per scaricarla", "AccentButton");
        update.IsDefault = true;
        update.Click += (_, _) =>
        {
            Accepted = true;
            Close();
        };
        var later = MakeButton("Più tardi", "PlainButton");
        later.IsCancel = true;
        later.Click += (_, _) => Close();
        buttons.Children.Add(update);
        buttons.Children.Add(later);
        root.Children.Add(buttons);

        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, FontSize = 13, Margin = new Thickness(0, 14, 0, 0),
            Text = installed
                ? "Ardesia salva il blocco appunti, si chiude, si aggiorna e si riapre da sola: ci vuole circa un minuto. " +
                  "Non servono le credenziali di amministratore."
                : "Questa è la versione portatile: scarica dalla pagina «Ardesia-portatile» (o l'installazione) e usa il file nuovo al posto di questo.",
        };
        DockPanel.SetDock(hint, Dock.Bottom);
        root.Children.Add(hint);

        var notes = new StackPanel();
        foreach (var block in RenderNotes(release.Notes)) notes.Children.Add(block);
        if (notes.Children.Count == 0) notes.Children.Add(new TextBlock { Text = "(nessuna descrizione)", Foreground = Brushes.Gray });
        root.Children.Add(new Border
        {
            BorderBrush = (Brush?)Application.Current.TryFindResource("LineBrush") ?? Brushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 12, 10, 12),
            Child = new ScrollViewer { Content = notes, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, PanningMode = PanningMode.VerticalOnly },
        });

        Content = root;
        Loaded += (_, _) => update.Focus();
    }

    static Button MakeButton(string text, string style)
    {
        var b = new Button { Content = text, MinWidth = 120, Padding = new Thickness(18, 9, 18, 9), Margin = new Thickness(10, 6, 0, 0), FontSize = 15 };
        if (Application.Current.TryFindResource(style) is Style st) b.Style = st;
        return b;
    }

    /// <summary>
    /// Le note della release sono in Markdown: qui basta poco. Titoli (#), elenchi (- o *), grassetto (**…**) e
    /// codice (`…`); i collegamenti diventano il solo testo.
    /// </summary>
    static IEnumerable<FrameworkElement> RenderNotes(string markdown)
    {
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0) continue;
            var heading = Regex.Match(line, @"^#{1,6}\s+(.*)$");
            if (heading.Success)
            {
                var h = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 16, Margin = new Thickness(0, 8, 0, 4), TextWrapping = TextWrapping.Wrap };
                AddInlines(h, heading.Groups[1].Value);
                yield return h;
                continue;
            }
            var bullet = Regex.Match(line, @"^(\s*)[-*+]\s+(.*)$");
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2), LineHeight = 21 };
            if (!bullet.Success)
            {
                AddInlines(tb, line.Trim());
                yield return tb;
                continue;
            }
            // punto elenco in una colonna a parte, così le righe successive restano allineate al testo
            int indent = bullet.Groups[1].Value.Length >= 2 ? 1 : 0;
            var row = new DockPanel { Margin = new Thickness(4 + indent * 18, 2, 0, 2) };
            var dot = new TextBlock { Text = "•", Width = 16, LineHeight = 21 };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            tb.Margin = new Thickness(0);
            AddInlines(tb, bullet.Groups[2].Value);
            row.Children.Add(tb);
            yield return row;
        }
    }

    static void AddInlines(TextBlock tb, string text)
    {
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1"); // [testo](indirizzo) → testo
        foreach (var part in Regex.Split(text, @"(\*\*[^*]+\*\*|`[^`]+`)"))
        {
            if (part.Length == 0) continue;
            if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4)
                tb.Inlines.Add(new Bold(new Run(part[2..^2])));
            else if (part.StartsWith('`') && part.EndsWith('`') && part.Length > 2)
                tb.Inlines.Add(new Run(part[1..^1]) { FontFamily = new FontFamily("Consolas") });
            else
                tb.Inlines.Add(new Run(part));
        }
    }
}
