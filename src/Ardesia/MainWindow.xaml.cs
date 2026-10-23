using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ardesia.Controls;
using Ardesia.Editor;
using Ardesia.Models;
using Ardesia.Services;
using Ardesia.Services.Google;
using Microsoft.Win32;

namespace Ardesia;

public partial class MainWindow : Window
{
    public static readonly string[] SectionColors =
    {
        "#1E5A68", "#0F6CBD", "#2E7D32", "#D83B01", "#C239B3",
        "#008272", "#E3008C", "#986F0B", "#4F6BED", "#A4262C",
    };

    static readonly (string Name, string Hex)[] TextColors =
    {
        ("Nero", "#000000"), ("Blu", "#1E5AC8"), ("Rosso", "#D32F2F"), ("Verde", "#2E7D32"),
        ("Viola", "#7B1FA2"), ("Arancione", "#EF6C00"), ("Bianco", "#FFFFFF"),
    };

    readonly AppSettings _settings;
    readonly DispatcherTimer _autosaveTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly List<ToggleButton> _toggles = new();
    Notebook _nb = new();
    string? _path;
    bool _dirty, _fullScreen, _navHidden, _syncing;
    bool _classPdfStale; // modifiche non ancora nel PDF per la classe
    NotebookNode? _section, _page;
    GridLength _sectionsWidth, _pagesWidth;
    WindowState _restoreState;
    Point _dragStart;
    NotebookNode? _dragCandidate;

    public MainWindow(string? fileToOpen, AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        foreach (var t in FindLogical<ToggleButton>(Ribbon).Concat(FindLogical<ToggleButton>(SimpleBar)))
        {
            if (t.Tag is not string) continue;
            _toggles.Add(t);
            t.Click += RibbonToggle_Click;
        }

        FontCombo.ItemsSource = new[]
        {
            "Calibri", "Segoe UI", "Arial", "Verdana", "Times New Roman", "Georgia",
            "Cambria", "Comic Sans MS", "Segoe Print", "Consolas",
        };
        FontCombo.SelectedItem = ElementFactory.DefaultFont;
        SizeCombo.ItemsSource = new double[] { 12, 14, 16, 18, 20, 24, 28, 32, 40, 48, 60, 72 };
        SizeCombo.SelectedItem = ElementFactory.DefaultFontSize;

        Editor.Modified += (_, _) => SetDirty();
        Editor.ToolChanged += (_, _) => SyncRibbon();
        BuildPens();
        Editor.EraserSize = PageEditor.EraserSizes.Contains(_settings.EraserSize) ? _settings.EraserSize : 22;
        Editor.FingerPans = _settings.FingerPans;
        ActivatePen(0);
        SetSimpleRibbon(_settings.SimpleRibbon);
        VersionText.Text = "Ardesia " + AppVersion;
        PreviewKeyDown += Window_PreviewKeyDown;
        SourceInitialized += (_, _) => FitToScreen();
        Closing += Window_Closing;

        _autosaveTimer.Tick += (_, _) => AutoSave();

        if (_settings.Persist) // nelle prove automatiche niente account
        {
            EventHandler onAccount = (_, _) => Dispatcher.BeginInvoke(UpdateAccountButton);
            GoogleAccountService.Current.Changed += onAccount;
            Closed += (_, _) => GoogleAccountService.Current.Changed -= onAccount;
            UpdateAccountButton();
            InitDrive();
            StartUpdateChecks();
        }

        Loaded += (_, _) =>
        {
            if (!_settings.Persist) OpenInMemoryNotebook(); // modalità di test
            else if (fileToOpen == null || !TryOpen(fileToOpen)) ShowStart(null);
            SyncRibbon();
        };
    }

    /// <summary>Mostra la schermata iniziale; se l'utente sceglie «Esci» l'app si chiude.</summary>
    void ShowStart(string? message)
    {
        while (true)
        {
            var start = new StartWindow(_settings, message) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
            if (start.ShowDialog() != true || start.ResultPath == null)
            {
                Close();
                return;
            }
            if (TryOpen(start.ResultPath)) return;
            message = null;
        }
    }

    /// <summary>
    /// Sulle LIM con ridimensionamento alto (es. 150%) lo spazio utile è piccolo: la finestra "normale"
    /// (non ingrandita) non deve mai superare lo schermo, altrimenti la barra del titolo finisce fuori.
    /// </summary>
    void FitToScreen()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width - 20);
        Height = Math.Min(Height, area.Height - 20);
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    static IEnumerable<T> FindLogical<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T t) yield return t;
            foreach (var d in FindLogical<T>(child)) yield return d;
        }
    }

    // ═══════════════════════════ Blocco appunti e file ═══════════════════════════

    void SetNotebook(Notebook nb, string? path)
    {
        _nb.PropertyChanged -= Notebook_PropertyChanged;
        _nb = nb;
        _path = path;
        _driveId = DriveIdOf(path);
        DataContext = nb;
        nb.PropertyChanged += Notebook_PropertyChanged;
        _section = null;
        _page = null;
        ShowSection(nb.Sections.FirstOrDefault());
        _dirty = false;
        _classPdfStale = false;
        UpdateTitle();
        DriveNotebookChanged();
    }

    /// <summary>Nuovo blocco appunti: prima si sceglie dove salvarlo (su Drive se si è collegati), poi si apre.</summary>
    void NewNotebook(bool onComputer = false)
    {
        if (!ConfirmClose()) return;
        if ((onComputer ? StartWindow.CreateNotebookFile(this, _settings) : StartWindow.CreateNotebook(this, _settings)) is { } path)
            TryOpen(path);
    }

    /// <summary>Blocco appunti non salvato, usato solo nelle prove automatiche.</summary>
    void OpenInMemoryNotebook()
    {
        var nb = new Notebook { Title = "Nuovo blocco appunti" };
        var section = NotebookNode.NewSection("Sezione 1", SectionColors[0]);
        nb.Insert(section, null);
        nb.Insert(NotebookNode.NewPage(), section);
        SetNotebook(nb, null);
    }

    bool TryOpen(string path, bool quiet = false)
    {
        // blocco su Google Drive: prima si controlla la versione su Drive (e si scarica se è cambiata)
        if (!PrepareDriveOpen(ref path, quiet, out var driveOpen)) return false;
        try
        {
            var nb = NotebookStore.Load(path);
            if (nb.Sections.Count == 0)
            {
                var s = NotebookNode.NewSection("Sezione 1", SectionColors[0]);
                nb.Insert(s, null);
                nb.Insert(NotebookNode.NewPage(), s);
            }
            SetNotebook(nb, path);
            AfterDriveOpen(driveOpen);
            _settings.AddRecent(path);
            _settings.Save();
            return true;
        }
        catch (Exception ex)
        {
            if (!quiet)
                AppMessage.Show(this, $"{path}\n\n{ex.Message}", MessageIcon.Error, "Impossibile aprire il file");
            return false;
        }
    }

    /// <summary>
    /// Un secondo avvio di Ardesia (Services/SingleInstance): la finestra viene in primo piano e, se è stato aperto un
    /// file .ardesia, lo si apre qui (salvando prima quello aperto, come sempre). Con una finestrella aperta si porta
    /// davanti solo quella.
    /// </summary>
    public void BringForward(string? file)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Maximized;
        Activate();
        if (OwnedWindows.OfType<Window>().LastOrDefault(w => w.IsVisible) is { } dialog)
        {
            dialog.Activate();
            return;
        }
        if (file == null || (_path != null && string.Equals(Path.GetFullPath(_path), file, StringComparison.OrdinalIgnoreCase))) return;
        if (ConfirmClose()) TryOpen(file);
    }

    void Open()
    {
        if (StartWindow.PickNotebookFile(this, _settings) is not { } path) return;
        if (!ConfirmClose()) return;
        TryOpen(path);
    }

    bool Save(bool quiet = false)
    {
        if (_path == null) return !quiet && SaveAs();
        try
        {
            _autosaveTimer.Stop();
            Editor.Flush();
            NotebookStore.Save(_nb, _path);
            _dirty = false;
            UpdateTitle();
            StatusText.Text = $"Salvato alle {DateTime.Now:HH:mm}";
            StatusText.ToolTip = _path;
            if (_driveId != null) DriveSaved();
            _settings.AddRecent(_path);
            _settings.Save();
            return true;
        }
        catch (Exception ex)
        {
            if (quiet)
                StatusText.Text = "Salvataggio automatico non riuscito";
            else
                AppMessage.Show(this, $"{_path}\n\n{ex.Message}", MessageIcon.Error, "Impossibile salvare il file");
            return false;
        }
    }

    bool SaveAs()
    {
        var dlg = new SaveFileDialog
        {
            Filter = NotebookStore.FileFilter,
            Title = "Salva blocco appunti con nome",
            FileName = SafeFileName(_nb.Title) + NotebookStore.Extension,
            InitialDirectory = ExistingFolder(_path != null ? Path.GetDirectoryName(_path) : _settings.LastSaveFolder),
        };
        DriveLocator.AddPlaces(dlg);
        if (dlg.ShowDialog(this) != true) return false;
        var previous = _path;
        _path = dlg.FileName;
        if (Save()) return true;
        _path = previous;
        return false;
    }

    /// <summary>Salvataggio automatico: parte poco dopo l'ultima modifica.</summary>
    void AutoSave()
    {
        _autosaveTimer.Stop();
        if (!_dirty) return;
        // Non salvare a metà di un tratto o di un trascinamento: riprova tra poco.
        if (Mouse.LeftButton == MouseButtonState.Pressed || Stylus.CurrentStylusDevice?.InAir == false)
        {
            _autosaveTimer.Start();
            return;
        }
        Save(quiet: true);
    }

    /// <summary>
    /// Salva automaticamente prima di chiudere o cambiare blocco appunti, senza chiedere nulla.
    /// Chiede conferma solo se il salvataggio non riesce (es. unità di rete non raggiungibile).
    /// Restituisce false se l'utente sceglie di non procedere.
    /// </summary>
    bool ConfirmClose()
    {
        Editor.Flush();
        if (!_dirty || _path == null || Save(quiet: true))
        {
            if (_driveId != null) DriveSync.Current?.UploadSoon(_driveId); // cambio di blocco: si carica subito
            // Il PDF per la classe si rifà qui: se non riesce bisogna dirlo, perché la barra di stato sparisce con la finestra.
            if (UpdateClassPdf() is not { } error) return true;
            return AppMessage.Ask(this,
                error + "\n\nIl blocco appunti è salvato; manca solo il PDF per la classe. Per riprovare: File › PDF per la classe… › Aggiorna ora.",
                "Continua lo stesso", "Resta qui", "PDF per la classe non aggiornato", MessageIcon.Warning);
        }
        var r = AppMessage.Choose(this,
            (_path != null ? _path + "\n\n" : "") + "Puoi salvarlo in un'altra posizione.",
            $"Non è stato possibile salvare «{_nb.Title}»", MessageIcon.Warning, 0,
            "Salva in un'altra posizione…", "Continua senza salvare", "Resta qui");
        return r switch
        {
            0 => SaveAs(),
            1 => true,
            _ => false,
        };
    }

    /// <summary>
    /// Rifà il PDF per la classe se ci sono modifiche (o se il file manca). Si scrive prima un file temporaneo,
    /// così Google Drive non carica mai un PDF a metà. Restituisce un messaggio di errore, o null.
    /// </summary>
    string? UpdateClassPdf(bool force = false)
    {
        // blocco su Drive: il PDF si fa accanto alla copia in cache e va su Drive nella coda (§6)
        if ((_driveId != null ? DriveClassPdfFile() : _nb.ClassPdfFile(_path)) is not { } pdf) return null;
        if (!force && !_classPdfStale && (File.Exists(pdf) || DriveClassPdfExists())) return null;
        var dir = Path.GetDirectoryName(pdf);
        if (dir == null || !Directory.Exists(dir))
        {
            StatusText.Text = "PDF per la classe non aggiornato: cartella non trovata";
            return $"Non trovo la cartella del PDF per la classe:\n{dir}\n\nSe è su Google Drive, controlla che Google Drive per desktop sia avviato.";
        }
        var pages = _nb.ClassPdfPages().ToList();
        // All'uscita un blocco appunti senza pagine non è un errore: niente avviso.
        if (pages.Count == 0) return !force ? null : "Nessuna pagina da mettere nel PDF: includi almeno una sezione con delle pagine.";
        var tmp = Path.Combine(dir, "~" + Path.GetFileName(pdf) + ".tmp");
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            StatusText.Text = "Aggiorno il PDF per la classe…";
            Editor.Flush();
            PdfExporter.Export(_nb, pages, tmp, _nb.Title);
            File.Move(tmp, pdf, overwrite: true);
            _classPdfStale = false;
            StatusText.Text = $"PDF per la classe aggiornato alle {DateTime.Now:HH:mm}";
            StatusText.ToolTip = pdf;
            if (_driveId != null) DriveClassPdfWritten();
            return null;
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { /* già assente */ }
            StatusText.Text = "PDF per la classe non aggiornato";
            return $"Non è stato possibile aggiornare il PDF per la classe:\n{pdf}\n\n{ex.Message}";
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    void ClassPdfSettings()
    {
        if (_path == null)
        {
            AppMessage.Show(this, "Salva prima il blocco appunti.");
            return;
        }
        var before = (_nb.ClassPdfDisabled, _nb.ClassPdfPath, string.Join(",", _nb.ClassPdfExcluded));
        bool updatedNow = false;
        var dlg = new ClassPdfDialog(_nb, _path)
        {
            Owner = this,
            UpdateNow = () =>
            {
                var error = UpdateClassPdf(force: true);
                updatedNow = error == null;
                return error;
            },
        };
        dlg.ShowDialog();
        if (before == (_nb.ClassPdfDisabled, _nb.ClassPdfPath, string.Join(",", _nb.ClassPdfExcluded))) return;
        // Impostazioni cambiate: si salvano col blocco appunti; il PDF va rifatto, se non è appena stato fatto.
        SetDirty();
        _classPdfStale = !updatedNow;
    }

    /// <summary>Finestra Impostazioni: «Questo PC» (settings.json) e «Questo blocco appunti» (nel file).</summary>
    SettingsWindow OpenSettings(bool notebookTab = false, bool modal = true, string? path = null)
    {
        var before = (_nb.ClassPdfDisabled, _nb.ClassPdfPath, string.Join(",", _nb.ClassPdfExcluded));
        var dlg = new SettingsWindow(_settings, _nb, path ?? _path)
        {
            Owner = this,
            UpdateNow = () => UpdateClassPdf(force: true),
            SignOut = SignOutGoogleAsync,
        };
        if (notebookTab) dlg.ShowNotebookTab();
        if (!modal)
        {
            dlg.Show();
            return dlg;
        }
        if (dlg.ShowDialog() == true)
        {
            Editor.FingerPans = _settings.FingerPans;
            SyncRibbon();
        }
        // «Aggiorna ora il PDF» applica subito le scelte del PDF, anche se poi si preme Annulla.
        if (before != (_nb.ClassPdfDisabled, _nb.ClassPdfPath, string.Join(",", _nb.ClassPdfExcluded)))
        {
            SetDirty();
            _classPdfStale = !dlg.ClassPdfUpdated;
        }
        CloseDriveNotebookIfSignedOut();
        return dlg;
    }

    void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmClose())
        {
            e.Cancel = true;
            return;
        }
        FinishDriveOnExit();
        _settings.Save();
    }

    /// <summary>Salvataggio di emergenza dopo un errore imprevisto.</summary>
    public string? EmergencySave()
    {
        try
        {
            Editor.Flush();
            var dir = Path.Combine(AppSettings.Folder, "Recupero");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{SafeFileName(_nb.Title)} {DateTime.Now:yyyy-MM-dd HH-mm-ss}{NotebookStore.Extension}");
            NotebookStore.Save(_nb, file);
            return file;
        }
        catch
        {
            return null;
        }
    }

    void SetDirty()
    {
        // Ogni modifica fa ripartire il conto alla rovescia del salvataggio automatico.
        _autosaveTimer.Stop();
        if (_path != null) _autosaveTimer.Start();
        _classPdfStale = true;
        if (_dirty) return;
        _dirty = true;
        UpdateTitle();
    }

    void UpdateTitle()
    {
        // Percorso completo: si vede subito in quale cartella (es. di Google Drive) si sta lavorando.
        var name = DriveWindowTitle() ?? _path ?? "non salvato";
        Title = $"{(_dirty ? "● " : "")}{_nb.Title} – {name} – Ardesia {AppVersion}";
    }

    /// <summary>Versione dell'app (da &lt;Version&gt; nel .csproj), es. «0.6.0».</summary>
    static readonly string AppVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";

    void NotebookTitleBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateTitle();

    void Notebook_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Notebook.Title)) SetDirty();
    }

    internal static string SafeFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return clean.Length == 0 ? "Blocco appunti" : clean;
    }

    static string ExistingFolder(string? folder) =>
        folder != null && Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    // ═══════════════════════════ Sezioni e pagine ═══════════════════════════

    void ShowSection(NotebookNode? section)
    {
        if (section != null && section.Kind != NodeKind.Section) section = section.OwningSection;
        _section = section;
        if (section != null) section.IsSelected = true;
        PageTree.ItemsSource = section?.Pages;
        PagesHeader.Text = section?.DisplayTitle ?? "Pagine";
        SectionColorBar.Background = new SolidColorBrush(BackgroundFactory.ParseColor(section?.Color ?? "#1E5A68"));

        var target = section?.LastOpenedPage;
        if (target == null || section == null || target.OwningSection != section)
            target = section?.Pages.FirstOrDefault();
        OpenPage(target);
    }

    void OpenPage(NotebookNode? page)
    {
        if (page != null && page == _page)
        {
            page.IsSelected = true;
            return;
        }
        Editor.Flush();
        _page = page;
        if (page != null)
        {
            page.IsSelected = true;
            var owner = page.OwningSection;
            if (owner != null) owner.LastOpenedPage = page;
            for (var p = page.Parent; p != null && p.Kind == NodeKind.Page; p = p.Parent) p.IsExpanded = true;
        }
        Editor.Load(_nb, page);
        Editor.Visibility = page != null ? Visibility.Visible : Visibility.Hidden;
        EmptyState.Visibility = page == null ? Visibility.Visible : Visibility.Collapsed;
        SyncRibbon();
    }

    void SectionTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is NotebookNode n && n != _section) ShowSection(n);
    }

    void PageTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is NotebookNode n && n != _page) OpenPage(n);
    }

    string NextSectionColor() => SectionColors[_nb.AllSections().Count() % SectionColors.Length];

    void AddSection(NotebookNode? parent = null, NotebookNode? after = null)
    {
        var s = NotebookNode.NewSection("Nuova sezione", NextSectionColor());
        if (after != null) _nb.Insert(s, after.Parent, _nb.SiblingsOf(after).IndexOf(after) + 1);
        else _nb.Insert(s, parent);
        if (parent != null) parent.IsExpanded = true;
        var page = NotebookNode.NewPage();
        if (_page?.Page is { } current)
        {
            page.Page!.Background = current.Background;
            page.Page.BackgroundColor = current.BackgroundColor;
        }
        _nb.Insert(page, s);
        SetDirty();
        ShowSection(s);
        s.IsEditing = true;
    }

    void AddPage(NotebookNode? after = null, bool asSubpage = false)
    {
        if (_section == null)
        {
            AddSection();
            return;
        }
        var p = NotebookNode.NewPage();
        if (_page?.Page is { } current)
        {
            p.Page!.Background = current.Background;
            p.Page.BackgroundColor = current.BackgroundColor;
        }
        if (asSubpage && (after ?? _page) is { } parent)
        {
            _nb.Insert(p, parent);
            parent.IsExpanded = true;
        }
        else if (after != null)
        {
            _nb.Insert(p, after.Parent, _nb.SiblingsOf(after).IndexOf(after) + 1);
        }
        else
        {
            _nb.Insert(p, _section);
        }
        SetDirty();
        OpenPage(p);
        Editor.FocusTitle();
    }

    void DuplicatePage(NotebookNode page)
    {
        Editor.Flush();
        var copy = NotebookNode.NewPage(page.Title + " (copia)");
        copy.Page = page.Page?.CloneData() ?? new PageData();
        _nb.Insert(copy, page.Parent, _nb.SiblingsOf(page).IndexOf(page) + 1);
        SetDirty();
        OpenPage(copy);
    }

    void DeleteNode(NotebookNode n)
    {
        bool isSection = n.Kind == NodeKind.Section;
        int count = n.DescendantsAndSelf().Count(x => x.Kind == NodeKind.Page) - (isSection ? 0 : 1);
        string what = isSection
            ? $"la sezione «{n.DisplayTitle}» e tutte le sue pagine"
            : count > 0 ? $"la pagina «{n.DisplayTitle}» e le sue {count} sottopagine" : $"la pagina «{n.DisplayTitle}»";
        if (!AppMessage.Ask(this, "L'operazione non si può annullare.", isSection ? "Elimina la sezione" : "Elimina la pagina", "Annulla",
                $"Vuoi eliminare {what}?", MessageIcon.Warning, safeDefault: true)) return;

        var siblings = _nb.SiblingsOf(n);
        int index = siblings.IndexOf(n);
        bool containsCurrent = n == _page || n.IsAncestorOf(_page) || n == _section || n.IsAncestorOf(_section);

        if (isSection && _nb.AllSections().Count() == 1)
        {
            AppMessage.Show(this, "Il blocco appunti deve contenere almeno una sezione.");
            return;
        }

        var parent = n.Parent;
        _nb.Detach(n);
        SetDirty();
        if (!containsCurrent) return;

        NotebookNode? next = siblings.Count > 0 ? siblings[Math.Min(index, siblings.Count - 1)] : parent;
        if (isSection)
        {
            _section = null;
            _page = null;
            Editor.Load(_nb, null);
            ShowSection(next ?? _nb.Sections.FirstOrDefault());
        }
        else
        {
            _page = null;
            Editor.Load(_nb, null);
            OpenPage(next is { Kind: NodeKind.Page } ? next : _section?.Pages.FirstOrDefault());
        }
    }

    void StartRename(NotebookNode n) => n.IsEditing = true;

    void EditBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox tb && tb.IsVisible)
            Dispatcher.BeginInvoke(() =>
            {
                tb.Focus();
                tb.SelectAll();
            }, DispatcherPriority.Input);
    }

    void EditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape && sender is TextBox { DataContext: NotebookNode n })
        {
            n.IsEditing = false;
            e.Handled = true;
        }
    }

    void EditBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: NotebookNode n } && n.IsEditing)
        {
            n.IsEditing = false;
            SetDirty();
            if (n == _section) PagesHeader.Text = n.DisplayTitle;
        }
    }

    // ───── Menu contestuali ─────

    static MenuItem Item(string header, Action action, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        mi.Click += (_, _) => action();
        return mi;
    }

    void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not NotebookNode n) return;
        e.Handled = true;
        if (n.Kind == NodeKind.Section) ShowSection(n);
        else OpenPage(n);

        var menu = n.Kind == NodeKind.Section ? SectionMenu(n) : PageMenu(n);
        menu.PlacementTarget = item;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    ContextMenu SectionMenu(NotebookNode s)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Nuova pagina", () => AddPage()));
        menu.Items.Add(Item("Nuova sezione", () => AddSection(after: s)));
        menu.Items.Add(Item("Nuova sottosezione", () => AddSection(parent: s)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Rinomina", () => StartRename(s)));
        var colors = new MenuItem { Header = "Colore della sezione" };
        foreach (var hex in SectionColors)
        {
            var mi = new MenuItem
            {
                Header = new Border
                {
                    Width = 80, Height = 16, CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(BackgroundFactory.ParseColor(hex)),
                },
            };
            mi.Click += (_, _) =>
            {
                s.Color = hex;
                if (s == _section) SectionColorBar.Background = new SolidColorBrush(BackgroundFactory.ParseColor(hex));
                SetDirty();
            };
            colors.Items.Add(mi);
        }
        menu.Items.Add(colors);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Sposta su", () => Structure(() => _nb.MoveUp(s))));
        menu.Items.Add(Item("Sposta giù", () => Structure(() => _nb.MoveDown(s))));
        menu.Items.Add(Item("Rendi sottosezione della precedente", () => Structure(() => _nb.Indent(s))));
        menu.Items.Add(Item("Porta al livello superiore", () => Structure(() => _nb.Outdent(s)), s.Parent != null));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Esporta la sezione in PDF…", () => ExportPdf(ExportScope.Section)));
        menu.Items.Add(Item("Copia la sezione in un altro blocco appunti…", () => CopyToNotebook(s)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Elimina la sezione", () => DeleteNode(s)));
        return menu;
    }

    ContextMenu PageMenu(NotebookNode p)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Nuova pagina", () => AddPage(after: p)));
        menu.Items.Add(Item("Nuova sottopagina", () => AddPage(after: p, asSubpage: true)));
        menu.Items.Add(Item("Duplica la pagina", () => DuplicatePage(p)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Rinomina", () => StartRename(p)));
        menu.Items.Add(Item("Sposta su", () => Structure(() => _nb.MoveUp(p))));
        menu.Items.Add(Item("Sposta giù", () => Structure(() => _nb.MoveDown(p))));
        menu.Items.Add(Item("Rendi sottopagina", () => Structure(() => _nb.Indent(p))));
        menu.Items.Add(Item("Promuovi pagina", () => Structure(() => _nb.Outdent(p)), p.Parent?.Kind == NodeKind.Page));

        var moveTo = new MenuItem { Header = "Sposta nella sezione" };
        foreach (var s in _nb.AllSections())
        {
            int depth = 0;
            for (var q = s.Parent; q != null; q = q.Parent) depth++;
            var target = s;
            moveTo.Items.Add(Item(new string(' ', depth * 4) + s.DisplayTitle, () =>
            {
                _nb.MoveInto(p, target);
                SetDirty();
                ShowSection(target);
                OpenPage(p);
            }, s != p.OwningSection || p.Parent != s));
        }
        menu.Items.Add(moveTo);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Esporta la pagina in PDF…", () => ExportPdf(ExportScope.Page)));
        menu.Items.Add(Item(p.Pages.Count > 0 ? "Copia la pagina (con le sottopagine) in un altro blocco appunti…" : "Copia la pagina in un altro blocco appunti…",
            () => CopyToNotebook(p)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Elimina la pagina", () => DeleteNode(p)));
        return menu;
    }

    void Structure(Func<bool> op)
    {
        if (!op()) return;
        SetDirty();
        if (_section != null) _section.IsSelected = true;
        if (_page != null) _page.IsSelected = true;
    }

    void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        var n = (sender as TreeView)?.SelectedItem as NotebookNode;
        if (n == null || n.IsEditing) return;
        switch (e.Key)
        {
            case Key.F2:
                StartRename(n);
                e.Handled = true;
                break;
            case Key.Delete:
                DeleteNode(n);
                e.Handled = true;
                break;
        }
    }

    // ───── Trascinamento nell'albero ─────

    static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    void Tree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        var src = e.OriginalSource as DependencyObject;
        _dragCandidate = FindAncestor<TextBox>(src) == null && FindAncestor<ToggleButton>(src) == null
            ? FindAncestor<TreeViewItem>(src)?.DataContext as NotebookNode
            : null;
    }

    void Tree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate == null || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _dragStart;
        if (Math.Abs(d.X) < 8 && Math.Abs(d.Y) < 8) return;
        var node = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(NotebookNode), node), DragDropEffects.Move);
    }

    (NotebookNode? dragged, NotebookNode? target, bool into) DropInfo(object sender, DragEventArgs e)
    {
        var dragged = e.Data.GetData(typeof(NotebookNode)) as NotebookNode;
        var target = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as NotebookNode;
        if (dragged == null) return (null, null, false);
        if (target == null)
        {
            if (sender == PageTree && dragged.Kind == NodeKind.Page && _section != null) return (dragged, _section, true);
            return (dragged, null, false);
        }
        bool into = dragged.Kind == NodeKind.Page && target.Kind == NodeKind.Section;
        return (dragged, target, into);
    }

    void Tree_DragOver(object sender, DragEventArgs e)
    {
        var (dragged, target, into) = DropInfo(sender, e);
        bool ok = dragged != null && target != null &&
                  (into ? _nb.CanMoveInto(dragged, target) : _nb.CanMoveAfter(dragged, target));
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    void Tree_Drop(object sender, DragEventArgs e)
    {
        var (dragged, target, into) = DropInfo(sender, e);
        if (dragged == null || target == null) return;
        if (into)
        {
            if (!_nb.CanMoveInto(dragged, target)) return;
            _nb.MoveInto(dragged, target);
        }
        else
        {
            if (!_nb.CanMoveAfter(dragged, target)) return;
            _nb.MoveAfter(dragged, target);
        }
        SetDirty();
        if (dragged.Kind == NodeKind.Page && dragged.OwningSection != _section) ShowSection(dragged.OwningSection);
        Structure(() => true);
        e.Handled = true;
    }

    // ═══════════════════════════ Barra multifunzione ═══════════════════════════

    void RibbonToggle_Click(object sender, RoutedEventArgs e)
    {
        var t = (ToggleButton)sender;
        var parts = ((string)t.Tag).Split(':', 2);
        switch (parts[0])
        {
            case "Tool":
                var tool = Enum.Parse<EditorTool>(parts[1]);
                // Ricliccando compasso / sposta pagina si torna allo strumento precedente.
                if (Editor.Tool == tool && t.IsChecked == false) Editor.ExitTemporaryTool();
                else Editor.Tool = tool;
                break;
            case "Shape":
                var shape = Enum.Parse<ShapeKind>(parts[1]);
                if (Editor.Tool == EditorTool.Shape && Editor.ShapeKind == shape && t.IsChecked == false)
                {
                    Editor.ExitTemporaryTool();
                    break;
                }
                Editor.ShapeKind = shape;
                Editor.Tool = EditorTool.Shape;
                ShapesPopup.IsOpen = false;
                break;
            case "Color":
                if (Editor.Tool is not (EditorTool.Pen or EditorTool.Highlighter or EditorTool.Shape or EditorTool.Compass or EditorTool.Select))
                    Editor.Tool = EditorTool.Pen;
                Editor.SetColor(BackgroundFactory.ParseColor(parts[1]));
                break;
            case "Width":
                if (Editor.Tool is not (EditorTool.Pen or EditorTool.Highlighter or EditorTool.Shape or EditorTool.Compass or EditorTool.Select))
                    Editor.Tool = EditorTool.Pen;
                Editor.SetWidth(double.Parse(parts[1], CultureInfo.InvariantCulture));
                break;
            case "Bg":
                Editor.SetBackground(Enum.Parse<PageBackground>(parts[1]), null);
                break;
            case "BgColor":
                Editor.SetBackground(null, parts[1]);
                AdaptPenToBackground();
                break;
            case "Ruler":
                Editor.RulerVisible = t.IsChecked == true;
                break;
            case "Protractor":
                Editor.ProtractorVisible = t.IsChecked == true;
                break;
            case "Dashed":
                // con gli strumenti che non disegnano si passa alla penna, come per colori e spessori
                if (Editor.Tool is not (EditorTool.Pen or EditorTool.Pencil or EditorTool.Shape or EditorTool.Compass or EditorTool.Select))
                    Editor.Tool = EditorTool.Pen;
                Editor.Dashed = t.IsChecked == true;
                StatusText.Text = Editor.Dashed ? "Linea tratteggiata" : "Linea continua";
                break;
            case "FingerPan":
                Editor.FingerPans = _settings.FingerPans = t.IsChecked == true;
                _settings.Save();
                StatusText.Text = Editor.FingerPans
                    ? "Il dito sposta la pagina, la penna scrive"
                    : "Il dito scrive come la penna";
                break;
            case "Nav":
                ToggleNav();
                break;
        }
        SyncRibbon();
    }

    /// <summary>Su sfondo scuro la penna nera diventa bianca, e viceversa.</summary>
    void AdaptPenToBackground()
    {
        if (_page?.Page is not { } d) return;
        bool dark = BackgroundFactory.IsDark(BackgroundFactory.ParseColor(d.BackgroundColor));
        if (dark && Editor.PenColor == Colors.Black) Editor.SetPenColor(Colors.White);
        else if (!dark && Editor.PenColor == Colors.White) Editor.SetPenColor(Colors.Black);
    }

    void SyncRibbon()
    {
        if (_syncing) return;
        SyncPens();
        _syncing = true;
        try
        {
            var data = _page?.Page;
            foreach (var t in _toggles)
            {
                var parts = ((string)t.Tag).Split(':', 2);
                t.IsChecked = parts[0] switch
                {
                    "Tool" => Editor.Tool.ToString() == parts[1],
                    "Shape" => Editor.Tool == EditorTool.Shape && Editor.ShapeKind.ToString() == parts[1],
                    "Color" => Editor.Tool != EditorTool.Select && Editor.CurrentColor == BackgroundFactory.ParseColor(parts[1]),
                    "Width" => Editor.Tool != EditorTool.Select &&
                               Math.Abs(Editor.CurrentWidth - double.Parse(parts[1], CultureInfo.InvariantCulture)) < 0.01,
                    "Bg" => data != null && data.Background.ToString() == parts[1],
                    "BgColor" => data != null && string.Equals(data.BackgroundColor, parts[1], StringComparison.OrdinalIgnoreCase),
                    "Ruler" => Editor.RulerVisible,
                    "Protractor" => Editor.ProtractorVisible,
                    "Nav" => !_navHidden,
                    "FingerPan" => Editor.FingerPans,
                    "Dashed" => Editor.Dashed,
                    _ => false,
                };
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    void FontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FontCombo.SelectedItem is not string name) return;
        var family = new FontFamily(name);
        Editor.TextFontFamily = family;
        Editor.ApplyTextProperty(TextElement.FontFamilyProperty, family);
    }

    void SizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SizeCombo.SelectedItem is not double size) return;
        Editor.TextFontSize = size;
        Editor.ApplyTextProperty(TextElement.FontSizeProperty, size);
    }

    void Bold_Click(object sender, RoutedEventArgs e) => Editor.ToggleBold();
    void Italic_Click(object sender, RoutedEventArgs e) => Editor.ToggleItalic();
    void Underline_Click(object sender, RoutedEventArgs e) => Editor.ToggleUnderline();

    void TextColor_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = TextColorButton, Placement = PlacementMode.Bottom };
        foreach (var (name, hex) in TextColors)
        {
            var brush = new SolidColorBrush(BackgroundFactory.ParseColor(hex));
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new Border
            {
                Width = 16, Height = 16, CornerRadius = new CornerRadius(3), Background = brush,
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5), Margin = new Thickness(0, 0, 8, 0),
            });
            header.Children.Add(new TextBlock { Text = name });
            menu.Items.Add(Item2(header, () => Editor.ApplyTextProperty(TextElement.ForegroundProperty, brush)));
        }
        menu.IsOpen = true;
    }

    static MenuItem Item2(object header, Action action)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => action();
        return mi;
    }

    void Paste_Click(object sender, RoutedEventArgs e) => Editor.Paste();
    void Cut_Click(object sender, RoutedEventArgs e) => Editor.Copy(cut: true);
    void Copy_Click(object sender, RoutedEventArgs e) => Editor.Copy(cut: false);
    void SelectAll_Click(object sender, RoutedEventArgs e) => Editor.SelectAll();
    void Delete_Click(object sender, RoutedEventArgs e) => Editor.DeleteSelection();
    void Undo_Click(object sender, RoutedEventArgs e) => Editor.Undo();
    void Redo_Click(object sender, RoutedEventArgs e) => Editor.Redo();
    void Save_Click(object sender, RoutedEventArgs e) => Save();
    void InsertText_Click(object sender, RoutedEventArgs e) => Editor.BeginTextAtVisibleArea();
    void NewPage_Click(object sender, RoutedEventArgs e) => AddPage();
    void NewSubpage_Click(object sender, RoutedEventArgs e) => AddPage(asSubpage: true);
    void DuplicatePage_Click(object sender, RoutedEventArgs e) { if (_page != null) DuplicatePage(_page); }
    void NewSection_Click(object sender, RoutedEventArgs e) => AddSection(after: TopLevelOf(_section));
    void NewSubsection_Click(object sender, RoutedEventArgs e) => AddSection(parent: _section);
    void ExportPdf_Click(object sender, RoutedEventArgs e) => ExportPdf(ExportScope.Page);
    void ZoomIn_Click(object sender, RoutedEventArgs e) => Editor.ZoomIn();
    void ZoomOut_Click(object sender, RoutedEventArgs e) => Editor.ZoomOut();
    void ZoomReset_Click(object sender, RoutedEventArgs e) => Editor.ZoomReset();
    void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();
    void ToggleNav_Click(object sender, RoutedEventArgs e) => ToggleNav();

    static NotebookNode? TopLevelOf(NotebookNode? s)
    {
        while (s?.Parent != null) s = s.Parent;
        return s;
    }

    void InsertImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Inserisci immagine",
            Filter = "Immagini|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|Tutti i file|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            Editor.InsertImage(File.ReadAllBytes(dlg.FileName));
        }
        catch (IOException ex)
        {
            AppMessage.Show(this, ex.Message, MessageIcon.Error);
        }
    }

    void InsertGraph_Click(object sender, RoutedEventArgs e) => Editor.InsertGraph();
    void InsertGeoGebra_Click(object sender, RoutedEventArgs e) => Editor.OpenGraph();

    void ApplyBackgroundToSection_Click(object sender, RoutedEventArgs e)
    {
        if (_page?.Page is not { } current || _section == null) return;
        foreach (var p in _section.DescendantsAndSelf().Where(n => n.Kind == NodeKind.Page && n.Page != null))
        {
            p.Page!.Background = current.Background;
            p.Page.BackgroundColor = current.BackgroundColor;
        }
        SetDirty();
        StatusText.Text = "Sfondo applicato a tutta la sezione";
    }

    void ToggleNav()
    {
        if (!_navHidden)
        {
            _sectionsWidth = SectionsColumn.Width;
            _pagesWidth = PagesColumn.Width;
            SectionsColumn.MinWidth = PagesColumn.MinWidth = 0;
            SectionsColumn.Width = PagesColumn.Width = new GridLength(0);
        }
        else
        {
            SectionsColumn.MinWidth = PagesColumn.MinWidth = 120;
            SectionsColumn.Width = _sectionsWidth;
            PagesColumn.Width = _pagesWidth;
        }
        _navHidden = !_navHidden;
        SyncRibbon();
    }

    void ToggleFullScreen()
    {
        if (!_fullScreen)
        {
            _restoreState = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            FullScreenGlyph.Text = "";
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _restoreState;
            FullScreenGlyph.Text = "";
        }
        _fullScreen = !_fullScreen;
    }

    // ═══════════════════════════ Penne ═══════════════════════════

    static readonly (string Name, string Hex)[] PenColors =
    {
        ("Nero", "#000000"), ("Grigio scuro", "#424242"), ("Grigio", "#9E9E9E"), ("Bianco", "#FFFFFF"), ("Marrone", "#795548"), ("Giallo ocra scuro", "#B8860B"),
        ("Blu scuro", "#0D47A1"), ("Blu", "#1E5AC8"), ("Azzurro", "#039BE5"), ("Azzurro chiaro", "#90CAF9"), ("Ciano", "#00ACC1"), ("Verde acqua", "#00897B"),
        ("Verde scuro", "#1B5E20"), ("Verde", "#2E7D32"), ("Verde chiaro", "#7CB342"), ("Verde oliva", "#827717"), ("Giallo", "#FFD600"), ("Arancione", "#EF6C00"),
        ("Rosso", "#D32F2F"), ("Bordeaux", "#880E4F"), ("Rosa", "#E91E63"), ("Rosa chiaro", "#F48FB1"), ("Viola", "#7B1FA2"), ("Indaco", "#5E35B1"),
    };

    static readonly (string Name, string Hex)[] HighlighterColors =
    {
        ("Giallo", "#FFD600"), ("Giallo chiaro", "#FFFF8D"), ("Ocra", "#FFC107"), ("Verde", "#76FF03"), ("Verde menta", "#69F0AE"), ("Ciano", "#18FFFF"),
        ("Azzurro", "#82B1FF"), ("Lilla", "#B388FF"), ("Rosa", "#FF4081"), ("Rosso", "#FF5252"), ("Arancione", "#FFAB40"), ("Grigio", "#BDBDBD"),
    };

    const string PenGlyph = "", HighlighterGlyph = "", PencilGlyph = "";
    const int MaxPens = 12;

    readonly List<(ToggleButton Button, Border Bar, Image Icon, int Index)> _penButtons = new();
    int _activePen;

    static Color PresetColor(PenPreset p) => p.Pencil ? Ardesia.Editor.Pencil.ColorOf(p.Grade) : BackgroundFactory.ParseColor(p.Color);
    static string PresetGlyph(PenPreset p) => p.Pencil ? PencilGlyph : p.Highlighter ? HighlighterGlyph : PenGlyph;
    static string PresetName(PenPreset p) => p.Pencil ? $"Matita {Ardesia.Editor.Pencil.Grades[Ardesia.Editor.Pencil.Clamp(p.Grade)]}" : p.Highlighter ? "Evidenziatore" : "Penna";

    /// <summary>
    /// Icona di Windows (penna, evidenziatore, matita) con l'interno riempito dal colore e il profilo nero:
    /// ogni contorno del simbolo viene riempito separatamente, poi il simbolo originale è disegnato sopra in nero.
    /// </summary>
    static ImageSource ColoredGlyph(string glyph, Color fill)
    {
        var ft = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            48, Brushes.Black, 1.0);
        var outline = ft.BuildGeometry(new Point(0, 0));
        var group = new DrawingGroup();
        var fillBrush = new SolidColorBrush(fill);
        fillBrush.Freeze();
        foreach (var figure in outline.GetFlattenedPathGeometry().Figures)
        {
            var single = new PathGeometry(new[] { figure.Clone() }) { FillRule = FillRule.Nonzero };
            foreach (var f in single.Figures) f.IsFilled = true;
            group.Children.Add(new GeometryDrawing(fillBrush, null, single));
        }
        group.Children.Add(new GeometryDrawing(Brushes.Black, null, outline));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    /// <summary>Crea i pulsanti delle penne (più il «+») nella barra completa e in quella semplificata.</summary>
    void BuildPens()
    {
        _penButtons.Clear();
        PensFull.Children.Clear();
        PensSimple.Children.Clear();
        for (int i = 0; i < _settings.Pens.Count; i++)
        {
            PensFull.Children.Add(MakePenButton(i, compact: false));
            PensSimple.Children.Add(MakePenButton(i, compact: true));
        }
        PensFull.Children.Add(MakeAddPenButton(compact: false));
        PensSimple.Children.Add(MakeAddPenButton(compact: true));
        SyncPens();
    }

    ToggleButton MakePenButton(int index, bool compact)
    {
        var pen = _settings.Pens[index];
        var grid = new Grid { Width = compact ? 42 : 48, Height = compact ? 42 : 66 };
        var icon = new Image
        {
            Width = compact ? 24 : 30,
            Height = compact ? 24 : 30,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, compact ? 4 : 8, 0, 0),
        };
        grid.Children.Add(icon);
        var bar = new Border
        {
            Width = compact ? 20 : 24,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, compact ? 8 : 10, compact ? 4 : 12),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)),
        };
        grid.Children.Add(bar);

        var chevron = new Button { Style = (Style)FindResource("PenChevron") };
        grid.Children.Add(chevron);

        var button = new ToggleButton
        {
            Style = (Style)FindResource("PenToggle"),
            Content = grid,
            ToolTip = PresetName(pen) + " – la freccetta in basso a destra sceglie " +
                      (pen.Pencil ? "durezza" : "colore") + " e spessore.\n" +
                      "Con lo strumento Seleziona attivo, applica colore e spessore ai tratti selezionati.",
        };
        button.Click += (_, _) => SelectPen(index);
        chevron.Click += (_, e) =>
        {
            e.Handled = true;
            OpenPenPopup(index, button);
        };
        _penButtons.Add((button, bar, icon, index));
        UpdatePenVisual(bar, icon, pen);
        return button;
    }

    Button MakeAddPenButton(bool compact)
    {
        var b = new Button
        {
            Style = (Style)FindResource("IconButton"),
            ToolTip = "Aggiungi una penna, un evidenziatore o una matita",
            Margin = new Thickness(2, 0, 0, 0),
            Content = new TextBlock
            {
                Text = "",
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = compact ? 13 : 15,
                Foreground = (Brush)FindResource("SubtleTextBrush"),
            },
        };
        b.Click += (_, _) =>
        {
            if (_settings.Pens.Count >= MaxPens)
            {
                AppMessage.Show(this, $"Puoi tenere al massimo {MaxPens} penne nella barra. Rimuovine una prima di aggiungerne un'altra.");
                return;
            }
            var menu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom, FontSize = 14 };
            menu.Items.Add(Item("Aggiungi una penna", () => AddPen(new PenPreset { Color = "#000000", Width = 2.5 })));
            menu.Items.Add(Item("Aggiungi un evidenziatore", () => AddPen(new PenPreset { Highlighter = true, Color = "#FFD600", Width = 4 })));
            menu.Items.Add(Item("Aggiungi una matita", () => AddPen(new PenPreset { Pencil = true, Grade = Ardesia.Editor.Pencil.DefaultGrade, Width = 2.5 })));
            menu.IsOpen = true;
        };
        return b;
    }

    void AddPen(PenPreset preset)
    {
        // Le nuove penne vanno dopo quelle dello stesso tipo, così il gruppo resta ordinato.
        int insertAt = _settings.Pens.FindLastIndex(p => p.Highlighter == preset.Highlighter && p.Pencil == preset.Pencil) + 1;
        if (insertAt <= 0) insertAt = preset.Highlighter ? _settings.Pens.Count : _settings.Pens.FindIndex(p => p.Highlighter) is >= 0 and var h ? h : _settings.Pens.Count;
        _settings.Pens.Insert(insertAt, preset);
        if (_activePen >= insertAt) _activePen++;
        _settings.Save();
        BuildPens();
        ActivatePen(insertAt);
        // Apre subito la scelta di colore e spessore per la nuova penna.
        var created = _penButtons.FirstOrDefault(p => p.Index == insertAt && (Ribbon.Visibility == Visibility.Visible
            ? PensFull.Children.Contains(p.Button) : PensSimple.Children.Contains(p.Button)));
        if (created.Button != null)
            Dispatcher.BeginInvoke(() => OpenPenPopup(insertAt, created.Button), DispatcherPriority.Loaded);
    }

    void RemovePen(int index)
    {
        if (_settings.Pens.Count <= 1) return;
        _settings.Pens.RemoveAt(index);
        _settings.Save();
        int newActive = _activePen == index ? Math.Min(index, _settings.Pens.Count - 1) : _activePen > index ? _activePen - 1 : _activePen;
        BuildPens();
        ActivatePen(newActive);
    }

    /// <summary>Icona riempita col colore della penna; la barretta sotto, dello stesso colore, ne indica lo spessore.</summary>
    static void UpdatePenVisual(Border bar, Image icon, PenPreset pen)
    {
        var color = PresetColor(pen);
        icon.Source = ColoredGlyph(PresetGlyph(pen), color);
        bar.Background = new SolidColorBrush(color);
        bar.Height = pen.Highlighter ? Math.Clamp(pen.Width * 0.8 + 3, 4, 10) : Math.Clamp(pen.Width * 0.7 + 1, 2, 9);
        // I colori molto chiari (bianco, giallo chiaro…) hanno un sottile bordo per restare visibili.
        double lum = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B;
        bar.BorderThickness = new Thickness(lum > 200 ? 0.6 : 0);
    }

    /// <summary>Clic su una penna: la attiva, oppure (in Seleziona) ne applica stile ai tratti selezionati.</summary>
    void SelectPen(int index)
    {
        var pen = _settings.Pens[index];
        if (Editor.RestyleSelectionWith(PresetColor(pen), pen.Width))
        {
            SyncRibbon();
            return;
        }
        ActivatePen(index);
    }

    void ActivatePen(int index)
    {
        if (_settings.Pens.Count == 0) return;
        index = Math.Clamp(index, 0, _settings.Pens.Count - 1);
        _activePen = index;
        var pen = _settings.Pens[index];
        if (pen.Pencil) Editor.UsePencil(pen.Grade, pen.Width);
        else Editor.UsePen(pen.Highlighter, BackgroundFactory.ParseColor(pen.Color), pen.Width);
        SyncRibbon();
    }

    void SyncPens()
    {
        foreach (var (button, _, _, index) in _penButtons)
        {
            var pen = _settings.Pens[index];
            var tool = pen.Pencil ? EditorTool.Pencil : pen.Highlighter ? EditorTool.Highlighter : EditorTool.Pen;
            button.IsChecked = index == _activePen && Editor.Tool == tool;
        }
    }

    void OpenPenPopup(int index, FrameworkElement target)
    {
        var pen = _settings.Pens[index];
        var popup = new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
        };

        var panel = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        TextBlock Label(string s, double top) => new()
        {
            Text = s, FontSize = 12, Margin = new Thickness(0, top, 0, 4),
            Foreground = (Brush)FindResource("SubtleTextBrush"),
        };

        // Colore (penne ed evidenziatori) oppure durezza (matita).
        var colorButtons = new List<(ToggleButton, string)>();
        var gradeButtons = new List<(ToggleButton, int)>();
        if (pen.Pencil)
        {
            panel.Children.Add(Label("Durezza della mina (da dura e chiara a morbida e scura)", 0));
            var grades = new StackPanel { Orientation = Orientation.Horizontal };
            for (int g = 0; g < Ardesia.Editor.Pencil.Grades.Length; g++)
            {
                var content = new StackPanel();
                content.Children.Add(new Border
                {
                    Width = 26, Height = 12, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 2, 0, 2),
                    Background = Ardesia.Editor.Pencil.BrushFor(Ardesia.Editor.Pencil.ColorOf(g), g),
                });
                content.Children.Add(new TextBlock { Text = Ardesia.Editor.Pencil.Grades[g], FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center });
                var b = new ToggleButton { Style = (Style)FindResource("PenToggle"), Content = content, Padding = new Thickness(4), MinWidth = 38, Margin = new Thickness(1) };
                gradeButtons.Add((b, g));
                grades.Children.Add(b);
            }
            panel.Children.Add(grades);
        }
        else
        {
            panel.Children.Add(Label("Colore", 0));
            var colors = new UniformGrid { Columns = 6, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var (name, hex) in pen.Highlighter ? HighlighterColors : PenColors)
            {
                var b = new ToggleButton
                {
                    Style = (Style)FindResource("Swatch"),
                    ToolTip = name,
                    Width = 36, Height = 36,
                    Content = new System.Windows.Shapes.Ellipse
                    {
                        Style = (Style)FindResource("SwatchDot"),
                        Fill = new SolidColorBrush(BackgroundFactory.ParseColor(hex)),
                    },
                };
                colorButtons.Add((b, hex));
                colors.Children.Add(b);
            }
            panel.Children.Add(colors);
        }

        panel.Children.Add(Label("Spessore", 10));
        var widths = new StackPanel { Orientation = Orientation.Horizontal };
        var widthButtons = new List<(ToggleButton, double)>();
        var sample = new SolidColorBrush(PresetColor(pen));
        foreach (var w in PageEditor.PenWidths)
        {
            var b = new ToggleButton
            {
                Style = (Style)FindResource("Swatch"),
                Width = 36, Height = 36,
                Content = pen.Highlighter
                    ? new Border { Width = 8, Height = w * 1.6 + 6, CornerRadius = new CornerRadius(1), Background = sample, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5) }
                    : new System.Windows.Shapes.Ellipse { Width = w * 1.4 + 1, Height = w * 1.4 + 1, Fill = sample, Stroke = Brushes.Gray, StrokeThickness = 0.5 },
            };
            widthButtons.Add((b, w));
            widths.Children.Add(b);
        }
        panel.Children.Add(widths);

        var remove = new Button
        {
            Style = (Style)FindResource("RibbonSmallButton"),
            Margin = new Thickness(-4, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = _settings.Pens.Count > 1,
            Content = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13 },
        };
        Ardesia.Controls.Rb.SetLabel(remove, "Rimuovi dalla barra");
        remove.Click += (_, _) =>
        {
            popup.IsOpen = false;
            RemovePen(index);
        };
        panel.Children.Add(remove);

        void Refresh()
        {
            foreach (var (b, hex) in colorButtons) b.IsChecked = string.Equals(hex, pen.Color, StringComparison.OrdinalIgnoreCase);
            foreach (var (b, g) in gradeButtons) b.IsChecked = g == pen.Grade;
            foreach (var (b, w) in widthButtons) b.IsChecked = Math.Abs(w - pen.Width) < 0.01;
            sample.Color = PresetColor(pen);
            foreach (var (button, bar, icon, i) in _penButtons)
            {
                if (i != index) continue;
                UpdatePenVisual(bar, icon, pen);
                button.ToolTip = PresetName(pen) + " – la freccetta in basso a destra sceglie " +
                                 (pen.Pencil ? "durezza" : "colore") + " e spessore.";
            }
        }

        void Changed()
        {
            _settings.Save();
            Refresh();
            ActivatePen(index);
        }

        foreach (var (b, hex) in colorButtons)
            b.Click += (_, _) =>
            {
                pen.Color = hex;
                Changed();
            };
        foreach (var (b, g) in gradeButtons)
            b.Click += (_, _) =>
            {
                pen.Grade = g;
                Changed();
            };
        foreach (var (b, w) in widthButtons)
            b.Click += (_, _) =>
            {
                pen.Width = w;
                Changed();
                popup.IsOpen = false;
            };

        var border = new Border
        {
            Background = Brushes.White,
            BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 2, 10, 10),
            Child = panel,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.25 },
        };
        popup.Child = border;
        Refresh();
        popup.IsOpen = true;
    }

    // ═══════════════════════════ Barra completa / semplificata ═══════════════════════════

    void SetSimpleRibbon(bool simple)
    {
        Ribbon.Visibility = simple ? Visibility.Collapsed : Visibility.Visible;
        RibbonModeButton.Visibility = Ribbon.Visibility;
        SimpleBar.Visibility = simple ? Visibility.Visible : Visibility.Collapsed;
        if (_settings.SimpleRibbon != simple)
        {
            _settings.SimpleRibbon = simple;
            _settings.Save();
        }
        SyncRibbon();
    }

    void RibbonMode_Click(object sender, RoutedEventArgs e) => SetSimpleRibbon(!_settings.SimpleRibbon);

    void TouchTest_Click(object sender, RoutedEventArgs e) =>
        new TouchTestWindow { Owner = this, WindowState = WindowState.Maximized }.ShowDialog();

    void ShapesButton_Click(object sender, RoutedEventArgs e) => ShapesPopup.IsOpen = true;

    /// <summary>Freccetta della gomma precisa: sceglie tra misura piccola, media e grande.</summary>
    void EraserSize_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // non attivare anche il pulsante che contiene la freccetta
        var target = FindAncestor<ToggleButton>(sender as DependencyObject) as FrameworkElement ?? (FrameworkElement)sender;
        var popup = new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        string[] names = { "Piccola", "Media", "Grande" };
        for (int i = 0; i < PageEditor.EraserSizes.Length; i++)
        {
            double size = PageEditor.EraserSizes[i];
            double d = new[] { 8.0, 16, 26 }[i];
            var item = new StackPanel { Margin = new Thickness(2) };
            item.Children.Add(new Grid
            {
                Width = 32, Height = 32,
                Children =
                {
                    new System.Windows.Shapes.Ellipse
                    {
                        Width = d, Height = d,
                        Stroke = (Brush)FindResource("TextBrush"), StrokeThickness = 1.5,
                        StrokeDashArray = new DoubleCollection { 2, 1.5 },
                    },
                },
            });
            item.Children.Add(new TextBlock { Text = names[i], FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });
            var b = new ToggleButton
            {
                Style = (Style)FindResource("PenToggle"),
                Content = item,
                Padding = new Thickness(6, 4, 6, 4),
                IsChecked = Math.Abs(Editor.EraserSize - size) < 0.1,
                MinWidth = 54,
            };
            b.Click += (_, _) =>
            {
                Editor.EraserSize = size;
                _settings.EraserSize = size;
                _settings.Save();
                Editor.Tool = EditorTool.EraserPoint;
                popup.IsOpen = false;
                SyncRibbon();
            };
            row.Children.Add(b);
        }
        popup.Child = new Border
        {
            Background = Brushes.White,
            BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 2, 10, 10),
            Child = row,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.25 },
        };
        popup.IsOpen = true;
    }
    // ═══════════════════════════ Menu File ═══════════════════════════

    // ═══════════════════════════ Versioni nuove ═══════════════════════════

    ReleaseInfo? _newRelease;

    /// <summary>
    /// Controllo delle versioni nuove poco dopo l'avvio (per non rallentarlo) e poi ogni 6 ore, perché le LIM restano
    /// accese per giorni; GitHub viene interpellato al massimo una volta al giorno (Services/Updates.cs).
    /// </summary>
    void StartUpdateChecks()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        timer.Tick += async (_, _) =>
        {
            timer.Interval = TimeSpan.FromHours(6);
            await CheckForUpdateAsync();
        };
        timer.Start();
        Closed += (_, _) => timer.Stop();
    }

    async Task CheckForUpdateAsync()
    {
        ReleaseInfo? release;
        try
        {
            release = await Updates.CheckAsync(_settings);
        }
        catch
        {
            return; // risposta strana o file di prova illeggibile: nessun avviso
        }
        _newRelease = release;
        UpdateButton.Visibility = release != null ? Visibility.Visible : Visibility.Collapsed;
        if (release == null) return;
        UpdateText.Text = $"Nuova versione {release.Version}";
        UpdateButton.ToolTip = $"È disponibile Ardesia {release.Version}: guarda le novità e aggiorna";
    }

    void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_newRelease is not { } release) return;
        bool installed = Updates.IsInstalled && release.SetupUrl != null;
        var dlg = new UpdateDialog(release, installed) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dlg.ShowDialog();
        if (!dlg.Accepted) return;
        if (!installed)
        {
            try { Process.Start(new ProcessStartInfo(release.PageUrl) { UseShellExecute = true }); } catch { /* nessun browser */ }
            return;
        }
        string setup;
        try
        {
            setup = BusyDialog.Run(this, $"Scarico Ardesia {release.Version}…", (progress, ct) => Updates.DownloadSetupAsync(release, progress, ct));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            AppMessage.Show(this, "Non sono riuscita a scaricare la versione nuova. Controlla la connessione a internet e riprova.\n\n" + ex.Message,
                MessageIcon.Warning, "Aggiornamento non riuscito");
            return;
        }
        // l'installer parte quando Ardesia si è chiusa del tutto (App.OnExit), dopo il salvataggio e i caricamenti su Drive
        Updates.PendingSetup = setup;
        Close();
        if (IsVisible) Updates.PendingSetup = null; // chiusura annullata
    }

    // ═══════════════════════════ Account Google ═══════════════════════════

    /// <summary>Nome di chi è collegato a Google nella barra in alto; se nessuno è collegato, «Accedi a Google».</summary>
    void UpdateAccountButton()
    {
        var g = GoogleAccountService.Current;
        AccountButton.Visibility = Visibility.Visible;
        AccountName.Text = g.IsSignedIn ? g.DisplayName ?? g.Email ?? "" : "Accedi a Google";
        AccountButton.ToolTip = g.IsSignedIn
            ? $"Collegato a Google come {g.Email}"
            : "Accedi con l'account Google della scuola per salvare i blocchi appunti su Google Drive";
    }

    bool _signingIn;

    /// <summary>«Accedi a Google» dalla barra in alto o dal menu File: l'accesso si completa nel browser.</summary>
    async void SignInGoogle()
    {
        if (_signingIn || GoogleAccountService.Current.IsSignedIn) return;
        _signingIn = true;
        AccountButton.IsEnabled = false;
        var oldStatus = StatusText.Text;
        StatusText.Text = "Completa l'accesso nella pagina di Google che si è aperta nel browser…";
        var error = await DriveUi.SignInAsync(this);
        _signingIn = false;
        AccountButton.IsEnabled = true;
        UpdateAccountButton();
        if (error != null)
        {
            StatusText.Text = oldStatus;
            AppMessage.Show(this, error, MessageIcon.Warning, "Accesso a Google non riuscito");
            return;
        }
        StatusText.Text = "Collegato a Google: in File trovi «Nuovo blocco appunti su Drive…»";
        StatusText.ToolTip = null;
        _ = DriveSync.Current; // riprende i caricamenti rimasti in coda per questo account
    }

    void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        var g = GoogleAccountService.Current;
        if (!g.IsSignedIn)
        {
            SignInGoogle();
            return;
        }
        var menu = new ContextMenu { PlacementTarget = AccountButton, Placement = PlacementMode.Bottom, FontSize = 14 };
        menu.Items.Add(new MenuItem { Header = $"Collegato a Google come {g.Email}", IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Nuovo blocco appunti su Drive…", () => NewNotebook()));
        menu.Items.Add(Item("Apri da Drive…", OpenFromDrive));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Esci da Google", async () =>
        {
            var (_, warning) = await SignOutGoogleAsync();
            if (warning != null) AppMessage.Show(this, warning, MessageIcon.Warning);
            CloseDriveNotebookIfSignedOut();
        }));
        menu.Items.Add(Item("Impostazioni…", () => OpenSettings()));
        menu.IsOpen = true;
    }

    void FileButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = FileButton, Placement = PlacementMode.Bottom, FontSize = 14 };
        // Da collegati a Google «Nuovo» crea il blocco su Drive (docs/google-drive.md §7).
        bool google = DriveSync.Current != null;
        menu.Items.Add(Item(google ? "Nuovo blocco appunti su Drive…" : "Nuovo blocco appunti", () => NewNotebook()));
        if (google) menu.Items.Add(Item("Nuovo blocco appunti sul computer…", () => NewNotebook(onComputer: true)));
        if (google) menu.Items.Add(Item("Apri da Drive…", OpenFromDrive));
        menu.Items.Add(Item(google ? "Apri dal computer…   (Ctrl+O)" : "Apri…   (Ctrl+O)", Open));
        // da scollegati: l'accesso a Google, per avere anche i blocchi su Drive
        if (!google && _settings.Persist) menu.Items.Add(Item("Accedi a Google Drive…", SignInGoogle, !_signingIn));
        var recent = new MenuItem { Header = "Recenti" };
        foreach (var f in _settings.RecentFiles.Where(p => DriveIdOf(p) != null ? DriveSync.IsCurrentAccountPath(p) : File.Exists(p)))
        {
            var file = f;
            var header = DriveSync.Describe(file) is { } d ? $"{d.Title}   (Google Drive{(d.Folder != null ? " · " + d.Folder : "")})" : file;
            recent.Items.Add(Item(header, () =>
            {
                if (ConfirmClose()) TryOpen(file);
            }));
        }
        recent.IsEnabled = recent.Items.Count > 0;
        menu.Items.Add(recent);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Salva   (Ctrl+S)", () => Save()));
        if (_driveId != null) menu.Items.Add(Item("Salva una copia sul computer…", SaveCopyToComputer));
        else menu.Items.Add(Item("Salva con nome…", () => SaveAs()));
        menu.Items.Add(Item("Copia in un altro blocco appunti…", () => CopyToNotebook(null)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Esporta in PDF…   (Ctrl+P)", () => ExportPdf(ExportScope.Page)));
        menu.Items.Add(Item(_nb.ClassPdfDisabled ? "PDF per la classe (spento)…" : "PDF per la classe…", ClassPdfSettings, _path != null));
        if (_driveId is { } driveId)
        {
            menu.Items.Add(Item("Apri la cartella su Google Drive", OpenDriveFolderInBrowser));
            menu.Items.Add(Item("Elimina il blocco appunti…", () => DeleteDriveNotebook(driveId, _nb.Title)));
        }
        else
            menu.Items.Add(Item("Apri la cartella del blocco appunti", () =>
            {
                if (_path != null) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_path}\"") { UseShellExecute = true });
            }, _path != null));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Prova del tocco della LIM…", () => new TouchTestWindow { Owner = this, WindowState = WindowState.Maximized }.ShowDialog()));
        menu.Items.Add(Item("Impostazioni…", () => OpenSettings()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Esci", Close));
        menu.IsOpen = true;
    }

    void ExportPdf(ExportScope scope)
    {
        if (_page == null && scope == ExportScope.Page) scope = ExportScope.Section;
        Editor.Flush();
        var dlg = new ExportDialog(_page?.DisplayTitle ?? "—", _section?.DisplayTitle ?? "—", _nb.Title, scope,
            _page?.Pages.Count > 0) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        List<NotebookNode> pages;
        string name;
        switch (dlg.Scope)
        {
            case ExportScope.Page when _page != null:
                pages = dlg.IncludeSubpagesChecked ? _page.DescendantsAndSelf().ToList() : new() { _page };
                name = _page.DisplayTitle;
                break;
            case ExportScope.Section when _section != null:
                pages = _section.DescendantsAndSelf().Where(n => n.Kind == NodeKind.Page).ToList();
                name = _section.DisplayTitle;
                break;
            default:
                pages = _nb.AllPages().ToList();
                name = _nb.Title;
                break;
        }
        if (pages.Count == 0)
        {
            AppMessage.Show(this, "Non ci sono pagine da esportare.");
            return;
        }

        var sfd = new SaveFileDialog
        {
            Title = "Esporta in PDF",
            Filter = "Documento PDF (*.pdf)|*.pdf",
            FileName = SafeFileName(name) + ".pdf",
            InitialDirectory = ExistingFolder(_settings.LastExportFolder ?? DriveLocator.GoogleDriveFolders().FirstOrDefault()),
        };
        DriveLocator.AddPlaces(sfd);
        if (sfd.ShowDialog(this) != true) return;

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            PdfExporter.Export(_nb, pages, sfd.FileName, name);
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            AppMessage.Show(this, ex.Message, MessageIcon.Error, "Esportazione non riuscita");
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        _settings.LastExportFolder = Path.GetDirectoryName(sfd.FileName);
        _settings.Save();
        StatusText.Text = $"PDF esportato: {Path.GetFileName(sfd.FileName)}";
        if (dlg.OpenAfterExport)
        {
            try
            {
                Process.Start(new ProcessStartInfo(sfd.FileName) { UseShellExecute = true });
            }
            catch
            {
                // nessun lettore PDF associato
            }
        }
    }

    // ═══════════════════════════ Tastiera ═══════════════════════════

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        bool inText = Keyboard.FocusedElement is TextBoxBase;

        if (e.Key == Key.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && !inText)
        {
            if (Editor.ExitTemporaryTool())
            {
                e.Handled = true;
                return;
            }
            if (_fullScreen)
            {
                ToggleFullScreen();
                e.Handled = true;
                return;
            }
        }
        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.S: Save(); e.Handled = true; return;
                case Key.O: Open(); e.Handled = true; return;
                case Key.N: AddPage(); e.Handled = true; return;
                case Key.P: ExportPdf(ExportScope.Page); e.Handled = true; return;
            }
        }
        if (inText) return;

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Z: Editor.Undo(); e.Handled = true; break;
                case Key.Y: Editor.Redo(); e.Handled = true; break;
                case Key.C: Editor.Copy(cut: false); e.Handled = true; break;
                case Key.X: Editor.Copy(cut: true); e.Handled = true; break;
                case Key.V: Editor.Paste(); e.Handled = true; break;
                case Key.A: Editor.SelectAll(); e.Handled = true; break;
                case Key.OemPlus or Key.Add: Editor.ZoomIn(); e.Handled = true; break;
                case Key.OemMinus or Key.Subtract: Editor.ZoomOut(); e.Handled = true; break;
                case Key.D0 or Key.NumPad0: Editor.ZoomReset(); e.Handled = true; break;
            }
        }
        else if (e.Key == Key.Delete && Editor.DeleteSelection())
        {
            e.Handled = true;
        }
    }

    // ═══════════════════════════ Istantanea (test visivi) ═══════════════════════════

    /// <summary>Istantanea del contenuto di una finestra (senza la cornice di Windows), in PNG.</summary>
    static void SaveWindowPng(Window w, string pngPath)
    {
        var root = (FrameworkElement)w.Content;
        var dpi = VisualTreeHelper.GetDpi(w);
        // il margine del contenuto sposta il disegno: va contato, o si perde il bordo destro e il fondo
        var m = root.Margin;
        var bmp = new RenderTargetBitmap((int)((root.ActualWidth + m.Left + m.Right) * dpi.DpiScaleX), (int)((root.ActualHeight + m.Top + m.Bottom) * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(pngPath);
        enc.Save(fs);
    }

    public void ScheduleSnapshot(string pngPath, bool demo, string? tab)
    {
        ContentRendered += async (_, _) =>
        {
            await Task.Delay(400);
            if (demo) BuildDemo();
            if (tab != null)
                foreach (TabItem ti in Ribbon.Items)
                    if (string.Equals(ti.Header as string, tab, StringComparison.OrdinalIgnoreCase))
                        Ribbon.SelectedItem = ti;
            // --tab Semplificata: la barra semplificata (le impostazioni della prova non si salvano)
            if (string.Equals(tab, "Semplificata", StringComparison.OrdinalIgnoreCase)) SetSimpleRibbon(true);
            // --tab Impostazioni / «Impostazioni blocco»: si fotografa la finestra Impostazioni invece del foglio
            Window shot = this;
            if (tab != null && tab.StartsWith("Impostazioni", StringComparison.OrdinalIgnoreCase))
                shot = OpenSettings(notebookTab: tab.Contains("blocco", StringComparison.OrdinalIgnoreCase), modal: false,
                    path: Path.Combine(Path.GetDirectoryName(Path.GetFullPath(pngPath))!, "demo" + NotebookStore.Extension));
            // --tab «Funzioni grafico»: la finestrella delle funzioni, con una funzione nascosta e una riga sbagliata
            if (tab != null && tab.StartsWith("Funzioni", StringComparison.OrdinalIgnoreCase))
            {
                var state = new Editor.Graph.GraphState();
                state.SetFunctions(new[] { "y = x² − 4", "f(x) = 1,5x + a", "x = 3", "y = sen(" });
                state.Functions[1].Hidden = true;
                var dlg = new Editor.Graph.GraphFunctionsDialog(state.Functions) { Owner = this };
                dlg.Show();
                shot = dlg;
            }
            // --tab Copia: «Copia in un altro blocco appunti», con un blocco esistente scelto e «Sostituisci»
            if (string.Equals(tab, "Copia", StringComparison.OrdinalIgnoreCase))
            {
                var dlg = new CopyToNotebookDialog(NotebookCopy.Describe(_nb, _section), drive: true) { Owner = this };
                dlg.ShowForSnapshot("«2BO Fisica 26-27» (Google Drive)");
                shot = dlg;
            }
            // --tab Messaggi: le domande e gli avvisi principali, uno per file (accanto a pngPath: messaggio-1.png, …)
            if (string.Equals(tab, "Messaggi", StringComparison.OrdinalIgnoreCase))
            {
                var samples = new (string Text, string Heading, MessageIcon Icon, string[] Buttons)[]
                {
                    ("Va nel cestino di Google Drive: lo puoi recuperare da lì per 30 giorni. Le cartelle di classe e materia restano.",
                        "Eliminare il blocco appunti «2BO Fisica 26-27»?", MessageIcon.Warning, new[] { "Elimina", "Annulla" }),
                    ("Gli alunni che hanno il link non potranno più aprirlo.", "Eliminare anche il PDF per la classe?",
                        MessageIcon.Question, new[] { "Elimina anche il PDF", "Tieni il PDF" }),
                    ("L'operazione non si può annullare.", "Vuoi eliminare la sezione «Algebra» e tutte le sue pagine?",
                        MessageIcon.Warning, new[] { "Elimina la sezione", "Annulla" }),
                    ("Questo blocco appunti risulta aperto anche sul PC LIM-AULA12 (ultima attività alle 10:42). Se lo modifichi qui e là, le modifiche non si uniscono.",
                        "Blocco aperto su un altro PC", MessageIcon.Warning, new[] { "Aprilo lo stesso", "Annulla" }),
                    ("G:\\Il mio Drive\\Fisica 2BO.ardesia\n\nPuoi salvarlo in un'altra posizione.", "Non è stato possibile salvare «Fisica 2BO»",
                        MessageIcon.Warning, new[] { "Salva in un'altra posizione…", "Continua senza salvare", "Resta qui" }),
                    ("Ho copiato la sezione «Geometria» in «2BO Fisica 26-27».", "Copia fatta", MessageIcon.Info, new[] { "Apri «2BO Fisica 26-27»", "Resta qui" }),
                    ("Il file non è un blocco appunti di Ardesia valido.", "Impossibile aprire il file", MessageIcon.Error, new[] { "OK" }),
                };
                var dir = Path.GetDirectoryName(Path.GetFullPath(pngPath))!;
                for (int i = 0; i < samples.Length; i++)
                {
                    var (text, heading, icon, buttons) = samples[i];
                    var dlg = AppMessage.ShowForSnapshot(this, text, heading, icon, buttons);
                    await Task.Delay(500);
                    SaveWindowPng(dlg, Path.Combine(dir, $"messaggio-{i + 1}.png"));
                    dlg.Close();
                }
            }
            // --tab Aggiornamento: le novità di una versione nuova (finta), come dopo il clic su «Nuova versione»
            if (string.Equals(tab, "Aggiornamento", StringComparison.OrdinalIgnoreCase))
            {
                var release = new ReleaseInfo
                {
                    Version = "0.8.0",
                    Notes = "## Novità\n- **Grafico di funzione**: i cursori dei parametri hanno minimo, massimo e passo.\n" +
                            "- Il doppio tocco sul grafico apre «Funzioni del grafico» anche sulle LIM Promethean.\n" +
                            "  - Con la penna e con il dito.\n## Correzioni\n- Il PDF per la classe si aggiorna anche se il nome del blocco contiene `/`.",
                };
                UpdateText.Text = "Nuova versione 0.8.0";
                UpdateButton.Visibility = Visibility.Visible;
                var dlg = new UpdateDialog(release, installed: true) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                dlg.Show();
                shot = dlg;
            }
            // --tab Messaggio: una domanda nello stile di Ardesia (come «Elimina il blocco appunti»)
            if (string.Equals(tab, "Messaggio", StringComparison.OrdinalIgnoreCase))
                shot = AppMessage.ShowForSnapshot(this,
                    "Va nel cestino di Google Drive: lo puoi recuperare da lì per 30 giorni. Le cartelle di classe e materia restano.",
                    "Eliminare il blocco appunti «2BO Fisica 26-27»?", MessageIcon.Warning, "Elimina", "Annulla");
            // --tab «Scrivi a mano»: la finestrella dopo una lettura dal foglio, con due funzioni scritte nella striscia
            // e lette davvero dal riconoscimento di Windows
            bool writing = tab != null && tab.StartsWith("Scrivi", StringComparison.OrdinalIgnoreCase);
            if (writing)
            {
                var dlg = new Editor.Graph.GraphFunctionsDialog(new[] { new Editor.Graph.GraphFunctionData { Text = "y = x² − 4", Color = "" } },
                    "Ho letto così quello che hai scritto: controlla, correggi se serve, poi «Disegna».") { Owner = this };
                dlg.Show();
                var ink = Ardesia.Editor.Graph.GraphSelfTest.StrokeFont.Fraction("y=", "x+1", "x-2", 34, 20, 40);
                ink.Add(Ardesia.Editor.Graph.GraphSelfTest.StrokeFont.Write("y=sen x", 34, 20, 125));
                dlg.AddWriting(ink);
                shot = dlg;
            }
            await Task.Delay(writing ? 4000 : 600);
            SaveWindowPng(shot, pngPath);

            if (demo)
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(pngPath))!;
                try
                {
                    Editor.Flush();
                    var file = Path.Combine(dir, "demo" + NotebookStore.Extension);
                    NotebookStore.Save(_nb, file);
                    var reloaded = NotebookStore.Load(file);
                    PdfExporter.Export(reloaded, reloaded.AllPages().ToList(), Path.Combine(dir, "demo.pdf"), reloaded.Title);
                }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString());
                }
            }
            _dirty = false;
            Application.Current.Shutdown();
        };
    }

    void BuildDemo()
    {
        _nb.Title = "Matematica 3B";
        var s1 = _nb.Sections[0];
        s1.Title = "Geometria";
        s1.Pages[0].Title = "Il teorema di Pitagora";
        var sub = NotebookNode.NewPage("Esercizi");
        _nb.Insert(sub, s1.Pages[0]);
        _nb.Insert(NotebookNode.NewPage("Circonferenza"), s1);
        var s2 = NotebookNode.NewSection("Algebra", SectionColors[1]);
        _nb.Insert(s2, null);
        _nb.Insert(NotebookNode.NewPage("Equazioni di primo grado"), s2);
        var s3 = NotebookNode.NewSection("Verifiche", SectionColors[2]);
        _nb.Insert(s3, s2);
        _nb.Insert(NotebookNode.NewPage("Correzione"), s3);
        _nb.Insert(NotebookNode.NewSection("Fisica", SectionColors[3]), null);
        ShowSection(s1);
        Editor.DrawDemo();
        SyncRibbon();
    }
}
