using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ardesia.Editor.Graph;

/// <summary>Finestrella «Funzioni del grafico»: una funzione per riga, con i simboli più usati a portata di dito.</summary>
public sealed class GraphFunctionsDialog : Window
{
    readonly TextBox _box;
    readonly TextBlock _check;

    public GraphFunctionsDialog(string text)
    {
        Title = "Funzioni del grafico";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;
        if (Application.Current.TryFindResource("TextBrush") is Brush fg) Foreground = fg;

        var root = new StackPanel { Margin = new Thickness(26, 20, 26, 20), Width = 560 };
        root.Children.Add(new TextBlock { Text = "Funzioni del grafico", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        root.Children.Add(new TextBlock
        {
            Text = "Una per riga, per esempio  y = x² − 4,  f(x) = 2x + 1,  y = a·sen(x),  x = 3.\n" +
                   "Le lettere diverse da x (a, b, k…) diventano parametri.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 10),
        });

        _box = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            FontSize = 22,
            FontFamily = new FontFamily("Cambria Math, Segoe UI"),
            MinHeight = 150,
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(6),
        };
        _box.TextChanged += (_, _) => Validate();
        root.Children.Add(_box);

        // simboli difficili da scrivere con la tastiera della LIM
        var keys = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (label, insert) in new[] { ("x²", "²"), ("x³", "³"), ("^", "^"), ("√", "√("), ("π", "π"), ("|x|", "|"), ("·", "·"), ("( )", "()"), ("sen", "sen("), ("cos", "cos("), ("ln", "ln(") })
        {
            var b = new Button { Content = label, MinWidth = 46, Height = 38, FontSize = 17, Margin = new Thickness(0, 0, 6, 6), Focusable = false };
            if (Application.Current.TryFindResource("PlainButton") is Style st) b.Style = st;
            b.Click += (_, _) => Insert(insert);
            keys.Children.Add(b);
        }
        root.Children.Add(keys);

        _check = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), MinHeight = 20 };
        root.Children.Add(_check);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "Disegna", IsDefault = false, Margin = new Thickness(0, 0, 8, 0), MinWidth = 90 };
        if (Application.Current.TryFindResource("AccentButton") is Style accent) ok.Style = accent;
        ok.Click += (_, _) => { DialogResult = true; };
        var cancel = new Button { Content = "Annulla", IsCancel = true, MinWidth = 90 };
        if (Application.Current.TryFindResource("PlainButton") is Style plain) cancel.Style = plain;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) =>
        {
            _box.Focus();
            _box.CaretIndex = _box.Text.Length;
        };
        Validate();
    }

    /// <summary>Le righe scritte, così come sono (la legenda le mostra uguali).</summary>
    public IReadOnlyList<string> Lines =>
        _box.Text.Replace("\r\n", "\n").Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    void Insert(string s)
    {
        int at = _box.CaretIndex;
        _box.SelectedText = "";
        _box.Text = _box.Text.Insert(at, s);
        // tra le parentesi appena scritte
        _box.CaretIndex = at + (s.EndsWith("()") ? s.Length - 1 : s.Length);
        _box.Focus();
    }

    void Validate()
    {
        var errors = new List<string>();
        int n = 0;
        foreach (var line in Lines)
        {
            n++;
            try
            {
                MathParser.ParseLine(MathText.Normalize(line));
            }
            catch (MathSyntaxException ex)
            {
                errors.Add($"Riga {n}: {ex.Message}");
            }
        }
        _check.Text = errors.Count == 0 ? (n == 0 ? "" : "✓ Tutto a posto") : string.Join("\n", errors);
        _check.Foreground = errors.Count == 0 ? new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)) : new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
    }
}
