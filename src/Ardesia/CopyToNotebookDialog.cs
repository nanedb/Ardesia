using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Ardesia;

/// <summary>
/// «Copia in un altro blocco appunti…»: in uno nuovo, oppure in uno che esiste già (su Drive o sul computer),
/// aggiungendo in fondo o sostituendo tutto il suo contenuto. Il blocco di destinazione lo sceglie chi apre la
/// finestra (<see cref="PickDrive"/>, <see cref="PickComputer"/>), che fa anche la copia.
/// </summary>
public sealed class CopyToNotebookDialog : Window
{
    readonly RadioButton _new, _existing, _append, _replace;
    readonly TextBlock _chosen, _error, _replaceNote;
    readonly StackPanel _existingPanel;

    /// <summary>Scelta del blocco su Drive: (percorso da passare a chi copia, nome da mostrare), o null.</summary>
    public Func<(string Target, string Name)?>? PickDrive { get; init; }

    /// <summary>Scelta del file sul computer: (percorso, nome), o null.</summary>
    public Func<(string Target, string Name)?>? PickComputer { get; init; }

    /// <summary>Vero = in un blocco nuovo; falso = in <see cref="Target"/>.</summary>
    public bool ToNew => _new.IsChecked == true;

    /// <summary>Il blocco esistente scelto (percorso sul computer, o «drive:{id}»).</summary>
    public string? Target { get; private set; }

    public string? TargetName { get; private set; }

    /// <summary>Sostituire tutto il contenuto del blocco esistente (invece di aggiungere in fondo).</summary>
    public bool Replace => _existing.IsChecked == true && _replace.IsChecked == true;

    /// <param name="what">Che cosa si copia («la sezione «Algebra»»).</param>
    /// <param name="drive">Vero se si è collegati a Google (allora si può scegliere un blocco su Drive).</param>
    public CopyToNotebookDialog(string what, bool drive)
    {
        Title = "Copia in un altro blocco appunti – Ardesia";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 20;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 15;
        Background = Brushes.White;
        if (Application.Current.TryFindResource("TextBrush") is Brush fg) Foreground = fg;
        var subtle = (Brush?)Application.Current.TryFindResource("SubtleTextBrush") ?? Brushes.DimGray;
        var red = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));

        var root = new StackPanel { Margin = new Thickness(28, 22, 28, 20) };
        root.Children.Add(new TextBlock { Text = "Copia in un altro blocco appunti", FontSize = 22, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock
        {
            Text = $"Copio {what}. Il blocco appunti aperto resta com'è.",
            Foreground = subtle, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 14),
        });

        _new = Radio("In un blocco appunti nuovo", "dove", root);
        root.Children.Add(Note(drive ? "Poi scegli classe, materia e nome su Google Drive (o «Crea invece sul computer…»)."
            : "Poi scegli dove salvarlo sul computer.", subtle));
        _existing = Radio("In un blocco appunti che esiste già", "dove", root);

        _existingPanel = new StackPanel { Margin = new Thickness(30, 4, 0, 0) };
        var pick = new WrapPanel();
        if (drive) pick.Children.Add(PickButton("Scegli su Google Drive…", () => PickDrive?.Invoke()));
        pick.Children.Add(PickButton("Scegli sul computer…", () => PickComputer?.Invoke()));
        _existingPanel.Children.Add(pick);
        _chosen = new TextBlock { Text = "Nessun blocco appunti scelto.", Foreground = subtle, FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) };
        _existingPanel.Children.Add(_chosen);
        _append = Radio("Aggiungi in fondo", "come", _existingPanel);
        _existingPanel.Children.Add(Note("Le sezioni vanno in fondo; una pagina va nella sezione con lo stesso nome della sua (se manca, la creo).", subtle));
        _replace = Radio("Sostituisci tutto il suo contenuto", "come", _existingPanel);
        _replaceNote = Note("Tutte le sezioni e le pagine che ha ora spariscono (il nome resta).", red);
        _existingPanel.Children.Add(_replaceNote);
        root.Children.Add(_existingPanel);

        _error = new TextBlock { Foreground = red, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        root.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var ok = MakeButton("Copia", "AccentButton");
        ok.IsDefault = true;
        ok.Click += (_, _) => Confirm();
        var cancel = MakeButton("Annulla", "PlainButton");
        cancel.IsCancel = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _new.IsChecked = true;
        _append.IsChecked = true;
        foreach (var r in new[] { _new, _existing, _append, _replace }) r.Checked += (_, _) => Update();
        Update();
    }

    static RadioButton Radio(string text, string group, Panel parent)
    {
        var r = new RadioButton
        {
            Content = text, GroupName = group, FontSize = 16, Margin = new Thickness(0, 8, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
        };
        parent.Children.Add(r);
        return r;
    }

    static TextBlock Note(string text, Brush color) =>
        new() { Text = text, Foreground = color, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(26, 2, 0, 0) };

    Button PickButton(string text, Func<(string Target, string Name)?> pick)
    {
        var b = MakeButton(text, "PlainButton");
        b.Margin = new Thickness(0, 0, 10, 0);
        b.Click += (_, _) =>
        {
            if (pick() is not { } chosen) return;
            Target = chosen.Target;
            TargetName = chosen.Name;
            _chosen.Text = $"Scelto: {chosen.Name}";
            _chosen.FontWeight = FontWeights.SemiBold;
            ShowError(null);
        };
        return b;
    }

    static Button MakeButton(string text, string style)
    {
        var b = new Button { Content = text, MinWidth = 110, Padding = new Thickness(18, 9, 18, 9), Margin = new Thickness(10, 0, 0, 0), FontSize = 15 };
        if (Application.Current.TryFindResource(style) is Style st) b.Style = st;
        return b;
    }

    void Update()
    {
        _existingPanel.IsEnabled = _existing.IsChecked == true;
        _existingPanel.Opacity = _existingPanel.IsEnabled ? 1 : 0.5;
        _replaceNote.Visibility = _replace.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    void ShowError(string? text)
    {
        _error.Text = text ?? "";
        _error.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Solo per l'istantanea di prova (--tab Copia): blocco esistente già scelto, «Sostituisci» acceso.</summary>
    internal void ShowForSnapshot(string chosenName)
    {
        _existing.IsChecked = true;
        _replace.IsChecked = true;
        _chosen.Text = $"Scelto: {chosenName}";
        _chosen.FontWeight = FontWeights.SemiBold;
        Show();
    }

    void Confirm()
    {
        if (_existing.IsChecked == true && Target == null)
        {
            ShowError("Scegli prima il blocco appunti in cui copiare.");
            return;
        }
        if (Replace && !Controls.AppMessage.Ask(this, "Le sezioni e le pagine che ha ora spariscono.",
                "Sostituisci", "Torna indietro", $"Sostituire tutto il contenuto di {TargetName}?", Controls.MessageIcon.Warning, safeDefault: true))
            return;
        DialogResult = true;
    }
}
