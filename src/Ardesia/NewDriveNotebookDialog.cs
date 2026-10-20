using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ardesia.Controls;
using Ardesia.Models;
using Ardesia.Services.Google;

namespace Ardesia;

/// <summary>
/// «Nuovo blocco appunti» su Google Drive (docs/google-drive.md §2.1): classe e materia scritte a mano, con quelle già
/// usate da toccare (lette dalle cartelle su Drive, quindi uguali su ogni PC), e nome proposto «2BO Fisica 26-27».
/// Il blocco va in «Il mio Drive/Ardesia/{classe}/{materia}/».
/// </summary>
public sealed class NewDriveNotebookDialog : Window
{
    readonly DriveSync _sync;
    readonly TextBox _class, _subject, _name;
    readonly WrapPanel _classChips, _subjectChips;
    readonly TextBlock _where, _error, _loading;
    DriveListing? _listing;
    string _proposed = "";

    /// <summary>ID del blocco creato su Drive (la copia in cache è pronta).</summary>
    public string? ResultId { get; private set; }

    /// <summary>L'utente ha scelto «Crea sul computer…».</summary>
    public bool ResultLocal { get; private set; }

    public NewDriveNotebookDialog(DriveSync sync, DriveListing? listing = null)
    {
        _sync = sync;
        _listing = listing;
        Title = "Nuovo blocco appunti – Ardesia";
        Width = 620;
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
        var accent = (Brush?)Application.Current.TryFindResource("AccentBrush") ?? Brushes.Teal;

        var root = new StackPanel { Margin = new Thickness(28, 22, 28, 20) };
        root.Children.Add(new TextBlock { Text = "Nuovo blocco appunti su Google Drive", FontSize = 22, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock
        {
            Text = "Ardesia lo salva nel tuo Drive, nella cartella Ardesia, divisa per classe e materia.",
            Foreground = subtle, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 16),
        });

        _class = Field(root, "Classe", "es. 2BO, 1BE-CE");
        _classChips = Chips(root);
        _subject = Field(root, "Materia", "es. Fisica");
        _subjectChips = Chips(root);
        _name = Field(root, "Nome del blocco appunti", "");
        _where = new TextBlock { Foreground = subtle, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        root.Children.Add(_where);
        _loading = new TextBlock { Text = "Leggo da Drive le classi e le materie già usate…", Foreground = subtle, FontSize = 13, Margin = new Thickness(0, 8, 0, 0) };
        root.Children.Add(_loading);
        _error = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        root.Children.Add(_error);

        var buttons = new DockPanel { Margin = new Thickness(0, 20, 0, 0), LastChildFill = false };
        var local = new Button
        {
            Content = "Crea invece sul computer…",
            Cursor = Cursors.Hand,
            Foreground = accent,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 7, 0, 7),
            VerticalAlignment = VerticalAlignment.Center,
        };
        local.Click += (_, _) =>
        {
            ResultLocal = true;
            DialogResult = true;
        };
        DockPanel.SetDock(local, Dock.Left);
        buttons.Children.Add(local);
        var cancel = MakeButton("Annulla", "PlainButton");
        cancel.IsCancel = true;
        var create = MakeButton("Crea", "AccentButton");
        create.IsDefault = true;
        create.Click += (_, _) => Create();
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(create, Dock.Right);
        buttons.Children.Add(cancel);
        buttons.Children.Add(create);
        root.Children.Add(buttons);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _class.TextChanged += (_, _) => Refresh();
        _subject.TextChanged += (_, _) => Refresh();
        _name.TextChanged += (_, _) => UpdateWhere();
        Loaded += async (_, _) =>
        {
            _class.Focus();
            if (_listing == null)
            {
                try
                {
                    _listing = await _sync.ListAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _loading.Text = "Non riesco a leggere da Drive le classi già usate: " + ex.Message;
                    return;
                }
            }
            _loading.Visibility = Visibility.Collapsed;
            Refresh();
        };
        Refresh();
    }

    TextBox Field(Panel root, string label, string hint)
    {
        root.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
        var box = new TextBox { FontSize = 17, Padding = new Thickness(6, 5, 6, 5), ToolTip = hint.Length > 0 ? hint : null };
        root.Children.Add(box);
        return box;
    }

    static WrapPanel Chips(Panel root)
    {
        var w = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        root.Children.Add(w);
        return w;
    }

    static Button MakeButton(string text, string style)
    {
        var b = new Button { Content = text, MinWidth = 120, Padding = new Thickness(18, 9, 18, 9), Margin = new Thickness(10, 0, 0, 0), FontSize = 15 };
        if (Application.Current.TryFindResource(style) is Style st) b.Style = st;
        return b;
    }

    /// <summary>Pulsanti delle classi/materie già usate (grandi abbastanza per il dito).</summary>
    void FillChips(WrapPanel panel, IEnumerable<string> names, TextBox target)
    {
        panel.Children.Clear();
        foreach (var n in names.Take(14))
        {
            var b = new Button
            {
                Content = n,
                MinWidth = 64,
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 0, 8, 8),
                FontSize = 15,
                Cursor = Cursors.Hand,
            };
            if (Application.Current.TryFindResource("PlainButton") is Style st) b.Style = st;
            if (string.Equals(n, target.Text.Trim(), StringComparison.CurrentCultureIgnoreCase))
            {
                b.Background = (Brush?)Application.Current.TryFindResource("AccentLightBrush") ?? Brushes.LightCyan;
                b.FontWeight = FontWeights.SemiBold;
            }
            b.Click += (_, _) =>
            {
                target.Text = n;
                target.CaretIndex = n.Length;
                (target == _class ? _subject : _name).Focus();
            };
            panel.Children.Add(b);
        }
    }

    void Refresh()
    {
        var cls = _class.Text.Trim();
        var subj = _subject.Text.Trim();
        if (_listing != null)
        {
            FillChips(_classChips, _listing.Classes, _class);
            // prima le materie della classe scritta, poi le altre
            var own = cls.Length > 0 && _listing.SubjectsByClass.TryGetValue(cls, out var list) ? list : new List<string>();
            FillChips(_subjectChips, own.Concat(_listing.Subjects.Where(s => !own.Contains(s, StringComparer.CurrentCultureIgnoreCase))), _subject);
        }
        // Nome proposto «2BO Fisica 26-27», finché l'utente non lo cambia a mano.
        var proposal = string.Join(" ", new[] { cls, subj, DriveSync.SchoolYear(DateTime.Today) }.Where(s => s.Length > 0));
        if (_name.Text == _proposed || _name.Text.Trim().Length == 0) _name.Text = proposal;
        _proposed = proposal;
        UpdateWhere();
    }

    void UpdateWhere()
    {
        // le cartelle esistenti si riusano senza distinguere maiuscole: si mostra il loro nome vero
        string Existing(string typed, IEnumerable<string>? names) =>
            names?.FirstOrDefault(n => string.Equals(n, typed, StringComparison.CurrentCultureIgnoreCase)) ?? typed;
        var cls = Existing(DriveSync.SafeName(_class.Text.Trim().Length > 0 ? _class.Text.Trim() : "classe"), _listing?.Classes);
        var subj = Existing(DriveSync.SafeName(_subject.Text.Trim().Length > 0 ? _subject.Text.Trim() : "materia"),
            _listing != null && _listing.SubjectsByClass.TryGetValue(cls, out var own) ? own : null);
        var name = DriveSync.SafeName(_name.Text.Trim().Length > 0 ? _name.Text.Trim() : "nome");
        _where.Text = $"Su Drive: Il mio Drive › Ardesia › {cls} › {subj} › {name}";
        bool exists = _listing?.Notebooks.Any(n => string.Equals(n.Class, _class.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)
                                                   && string.Equals(n.Subject, _subject.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)
                                                   && string.Equals(n.Title, name, StringComparison.CurrentCultureIgnoreCase)) == true;
        ShowError(exists ? "Attenzione: in questa cartella c'è già un blocco appunti con questo nome." : null);
    }

    void ShowError(string? text)
    {
        _error.Text = text ?? "";
        _error.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    void Create()
    {
        var cls = _class.Text.Trim();
        var subj = _subject.Text.Trim();
        var name = _name.Text.Trim();
        if (cls.Length == 0 || subj.Length == 0 || name.Length == 0)
        {
            ShowError("Scrivi classe, materia e nome.");
            (cls.Length == 0 ? _class : subj.Length == 0 ? _subject : _name).Focus();
            return;
        }
        var nb = new Notebook();
        var section = NotebookNode.NewSection("Sezione 1", MainWindow.SectionColors[0]);
        nb.Insert(section, null);
        nb.Insert(NotebookNode.NewPage(), section);
        try
        {
            ResultId = BusyDialog.Run(this, $"Creo «{name}» su Google Drive…",
                (_, ct) => _sync.CreateNotebookAsync(cls, subj, name, nb, ct));
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            // annullato: si resta nella finestra
        }
        catch (Exception ex)
        {
            ShowError(DriveApi.IsTransient(ex)
                ? "Senza connessione a Google Drive non si può creare un blocco appunti: riprova quando c'è la rete, " +
                  "oppure crealo sul computer («Crea invece sul computer…»). (" + ex.Message + ")"
                : "Non è stato possibile creare il blocco appunti: " + ex.Message);
        }
    }
}
