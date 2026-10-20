using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ardesia.Controls;
using Ardesia.Services;
using Ardesia.Services.Google;
using Microsoft.Win32;

namespace Ardesia;

/// <summary>
/// «Apri da Drive…»: i blocchi appunti creati da Ardesia su Google Drive (con il permesso `drive.file` si vedono
/// solo quelli), con classe e materia, ultima modifica, «Apri», «Elimina…» e «Nuovo…».
/// </summary>
public sealed class DriveBrowserWindow : Window
{
    readonly DriveSync _sync;
    readonly string? _openId;
    readonly ListBox _list;
    readonly TextBox _filter;
    readonly TextBlock _status;
    readonly Button _open, _delete;
    List<DriveNotebook> _all = new();

    /// <summary>ID del blocco da aprire.</summary>
    public string? ResultId { get; private set; }

    /// <summary>L'utente ha scelto «Nuovo blocco appunti…».</summary>
    public bool ResultNew { get; private set; }

    /// <summary>Elimina un blocco (conferme comprese); vero se è andato nel cestino. Di regola <see cref="DriveUi.ConfirmAndTrash"/>.</summary>
    public Func<DriveNotebook, bool>? Delete { get; init; }

    public DriveBrowserWindow(DriveSync sync, string? openId)
    {
        _sync = sync;
        _openId = openId;
        Title = "Blocchi appunti su Google Drive – Ardesia";
        Width = 760;
        Height = Math.Min(680, SystemParameters.WorkArea.Height - 20);
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 15;
        Background = Brushes.White;
        if (Application.Current.TryFindResource("TextBrush") is Brush fg) Foreground = fg;
        var subtle = (Brush?)Application.Current.TryFindResource("SubtleTextBrush") ?? Brushes.DimGray;

        var root = new DockPanel { Margin = new Thickness(28, 22, 28, 20) };
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "I tuoi blocchi appunti su Google Drive", FontSize = 22, FontWeight = FontWeights.SemiBold });
        head.Children.Add(new TextBlock
        {
            Text = $"Qui ci sono i blocchi creati da Ardesia su Drive ({sync.Email}). Quelli su Google Drive per desktop (G:) " +
                   "o sul computer si aprono da «Apri dal computer».",
            Foreground = subtle, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12),
        });
        _filter = new TextBox { FontSize = 16, Padding = new Thickness(6, 5, 6, 5), ToolTip = "Cerca per nome, classe o materia" };
        _filter.TextChanged += (_, _) => Fill();
        var filterRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var glass = new TextBlock { Text = "\uE721", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Foreground = subtle };
        if (Application.Current.TryFindResource("IconFont") is FontFamily icons) glass.FontFamily = icons;
        filterRow.Children.Add(glass);
        filterRow.Children.Add(_filter);
        head.Children.Add(filterRow);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        var create = MakeButton("Nuovo blocco appunti…", "PlainButton");
        create.Click += (_, _) =>
        {
            ResultNew = true;
            DialogResult = true;
        };
        _delete = MakeButton("Elimina…", "PlainButton");
        _delete.Click += (_, _) => DeleteSelected();
        DockPanel.SetDock(create, Dock.Left);
        DockPanel.SetDock(_delete, Dock.Left);
        create.Margin = new Thickness(0);
        buttons.Children.Add(create);
        buttons.Children.Add(_delete);
        var close = MakeButton("Chiudi", "PlainButton");
        close.IsCancel = true;
        _open = MakeButton("Apri", "AccentButton");
        _open.IsDefault = true;
        _open.Click += (_, _) => OpenSelected();
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(_open, Dock.Right);
        buttons.Children.Add(close);
        buttons.Children.Add(_open);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        _status = new TextBlock { Foreground = subtle, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        _list = new ListBox { BorderBrush = (Brush?)Application.Current.TryFindResource("LineBrush") ?? Brushes.LightGray, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetPanningMode(_list, PanningMode.VerticalOnly);
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(_list, d) is ListBoxItem) OpenSelected();
        };
        root.Children.Add(_list);
        Content = root;

        Loaded += async (_, _) => await LoadAsync();
        UpdateButtons();
    }

    static Button MakeButton(string text, string style)
    {
        var b = new Button { Content = text, MinWidth = 110, Padding = new Thickness(18, 9, 18, 9), Margin = new Thickness(10, 0, 0, 0), FontSize = 15 };
        if (Application.Current.TryFindResource(style) is Style st) b.Style = st;
        return b;
    }

    async Task LoadAsync()
    {
        _status.Text = "Leggo l'elenco da Google Drive…";
        try
        {
            var listing = await _sync.ListAsync(CancellationToken.None);
            _all = listing.Notebooks;
            _status.Text = listing.Offline
                ? "Senza connessione: questi sono i blocchi già aperti su questo PC."
                : _all.Count == 0 ? "Non ci sono ancora blocchi appunti di Ardesia sul tuo Drive." : "";
        }
        catch (Exception ex)
        {
            _status.Text = "Non riesco a leggere l'elenco da Google Drive: " + ex.Message;
        }
        Fill();
        _filter.Focus();
    }

    void Fill()
    {
        var q = _filter.Text.Trim();
        var subtle = (Brush?)Application.Current.TryFindResource("SubtleTextBrush") ?? Brushes.DimGray;
        _list.Items.Clear();
        foreach (var n in _all.Where(n => q.Length == 0 || n.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                          || n.Folder.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
        {
            var name = new TextBlock { Text = n.Title + (n.Id == _openId ? "   (aperto)" : ""), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var detail = string.Join(" · ", new[]
            {
                n.Folder.Length > 0 ? n.Folder : null,
                n.OpenOn != null && n.Id != _openId ? "aperto su " + n.OpenOn : null,
                n.Modified is { } m ? "modificato " + DriveUi.When(m) : null,
                n.Size is { } s ? DriveUi.Size(s) : null,
            }.Where(s => s != null));
            var row = new StackPanel { Margin = new Thickness(8, 8, 8, 8) };
            row.Children.Add(name);
            row.Children.Add(new TextBlock { Text = detail, FontSize = 13, Foreground = subtle, TextTrimming = TextTrimming.CharacterEllipsis });
            _list.Items.Add(new ListBoxItem { Content = row, Tag = n });
        }
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        UpdateButtons();
    }

    DriveNotebook? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as DriveNotebook;

    void UpdateButtons() => _open.IsEnabled = _delete.IsEnabled = Selected != null;

    void OpenSelected()
    {
        if (Selected is not { } n) return;
        ResultId = n.Id;
        DialogResult = true;
    }

    void DeleteSelected()
    {
        if (Selected is not { } n) return;
        if (!(Delete ?? (x => DriveUi.ConfirmAndTrash(this, _sync, x.Id, x.Title, x.ClassPdfId)))(n)) return;
        if (n.Id == _openId)
        {
            // eliminato il blocco aperto: l'elenco si chiude, poi chi l'ha aperto mostra la schermata iniziale
            DialogResult = false;
            return;
        }
        _all.Remove(n);
        Fill();
        _status.Text = $"«{n.Title}» è nel cestino di Google Drive.";
    }
}

/// <summary>Pezzi d'interfaccia comuni per i blocchi su Drive (schermata iniziale, menu File, elenco).</summary>
public static class DriveUi
{
    /// <summary>«oggi alle 10:32», «ieri alle 8:05», «12 ottobre 2026».</summary>
    public static string When(DateTime t)
    {
        if (t.Date == DateTime.Today) return $"oggi alle {t:H:mm}";
        if (t.Date == DateTime.Today.AddDays(-1)) return $"ieri alle {t:H:mm}";
        return t.Year == DateTime.Today.Year ? t.ToString("d MMMM") : t.ToString("d MMMM yyyy");
    }

    public static string Size(long bytes) =>
        bytes < 1024 * 1024 ? $"{Math.Max(1, bytes / 1024)} kB" : $"{bytes / (1024.0 * 1024):0.0} MB";

    /// <summary>
    /// Eliminare un blocco (§2.2): va nel cestino di Drive, le cartelle restano. Se ha un PDF per la classe si chiede se
    /// eliminare anche quello. Il pulsante predefinito è quello che non cancella. Senza rete non si elimina. Vero se è
    /// andato nel cestino.
    /// </summary>
    public static bool ConfirmAndTrash(Window owner, DriveSync sync, string id, string title, string? classPdfId = null)
    {
        if (MessageBox.Show(owner,
                $"Eliminare il blocco appunti «{title}»?\n\nVa nel cestino di Google Drive: lo puoi recuperare da lì per 30 giorni. " +
                "Le cartelle di classe e materia restano.\n\nSì = elimina    No = annulla",
                "Ardesia – Elimina", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return false;
        classPdfId = sync.Entry(id)?.ClassPdfId ?? classPdfId;
        bool withPdf = classPdfId != null && MessageBox.Show(owner,
            "Eliminare anche il PDF per la classe? Gli alunni che hanno il link non potranno più aprirlo.\n\n" +
            "Sì = elimina anche il PDF    No = tieni il PDF",
            "Ardesia – PDF per la classe", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        try
        {
            BusyDialog.Run(owner, $"Sposto «{title}» nel cestino di Google Drive…", async (_, ct) =>
            {
                await sync.TrashAsync(id, ct, withPdf ? classPdfId : null);
                return true;
            });
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, (DriveApi.IsTransient(ex)
                    ? "Senza connessione a Google Drive non si può eliminare un blocco appunti. Riprova quando c'è la rete."
                    : "Non è stato possibile eliminare il blocco appunti.") + "\n\n" + ex.Message,
                "Ardesia", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    /// <summary>
    /// Il blocco non c'è più su Drive: si toglie da recenti e cache. Se aveva modifiche non caricate, si offre di
    /// salvarne una copia sul computer prima.
    /// </summary>
    public static void HandleGone(Window? owner, DriveSync sync, string id, DriveGoneException ex, AppSettings settings)
    {
        var cache = sync.CachePath(id);
        if (ex.HadPendingChanges && File.Exists(cache))
        {
            var r = MessageBox.Show(ex.Message + "\n\nLe ultime modifiche fatte su questo PC non erano ancora state caricate. " +
                                    "Vuoi salvarne una copia sul computer?",
                "Ardesia", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);
            if (r == MessageBoxResult.Yes)
            {
                var dlg = new SaveFileDialog
                {
                    Title = "Salva una copia sul computer",
                    Filter = NotebookStore.FileFilter,
                    FileName = DriveSync.SafeName(sync.Entry(id)?.Title ?? "Blocco appunti") + NotebookStore.Extension,
                    InitialDirectory = StartWindow.DefaultFolder,
                };
                DriveLocator.AddPlaces(dlg);
                if ((owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog()) == true)
                {
                    File.Copy(cache, dlg.FileName, overwrite: true);
                    settings.AddRecent(dlg.FileName);
                }
            }
        }
        else
        {
            MessageBox.Show(ex.Message, "Ardesia", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        settings.RecentFiles.RemoveAll(p => DriveSync.TryParseCachePath(p, out _, out var other) && other == id);
        settings.Save();
        sync.Forget(id);
    }

    /// <summary>«Accedi con Google» (browser). Restituisce un messaggio d'errore, o null se l'accesso è riuscito.</summary>
    public static async Task<string?> SignInAsync(Window owner)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await GoogleAccountService.Current.SignInAsync(cts.Token);
            owner.Activate(); // il browser era davanti
            return null;
        }
        catch (OperationCanceledException)
        {
            return "Accesso non completato.";
        }
        catch (Exception ex)
        {
            owner.Activate();
            return ex.Message;
        }
    }
}
