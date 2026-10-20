using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Ardesia.Models;
using Ardesia.Services;
using Ardesia.Services.Google;
using Microsoft.Win32;

namespace Ardesia.Controls;

/// <summary>Impostazioni del PDF per la classe: dove salvarlo e quali sezioni includere.</summary>
public partial class ClassPdfPanel : UserControl
{
    Notebook? _nb;
    readonly Dictionary<Guid, CheckBox> _checks = new();

    /// <summary>Accanto al file del blocco appunti, con lo stesso nome.</summary>
    string _defaultPath = "";

    /// <summary>Blocco su Google Drive: il PDF va su Drive accanto al blocco, con un link che non cambia (§6).</summary>
    string? _driveId;

    public ClassPdfPanel()
    {
        InitializeComponent();
        Action<string> onStatus = id =>
        {
            if (id == _driveId) RefreshDrive();
        };
        Loaded += (_, _) => DriveSync.StatusChanged += onStatus;
        Unloaded += (_, _) => DriveSync.StatusChanged -= onStatus;
        Enabled.Click += (_, _) => RefreshDrive();
    }

    /// <summary>Vero per un blocco su Google Drive.</summary>
    public bool IsDrive => _driveId != null;

    public void Load(Notebook nb, string notebookPath)
    {
        _nb = nb;
        _defaultPath = Path.ChangeExtension(notebookPath, ".pdf");
        Enabled.IsChecked = !nb.ClassPdfDisabled;
        PathBox.Text = nb.ClassPdfPath ?? _defaultPath;
        _checks.Clear();
        SectionList.Children.Clear();
        foreach (var s in nb.Sections) AddSection(s, 0);

        _driveId = DriveSync.TryParseCachePath(notebookPath, out _, out var id) ? id : null;
        FileRow.Visibility = _driveId != null ? Visibility.Collapsed : Visibility.Visible;
        DriveBox.Visibility = _driveId != null ? Visibility.Visible : Visibility.Collapsed;
        if (_driveId != null)
            Intro.Text = "Ardesia tiene aggiornato un PDF di questo blocco appunti su Google Drive, accanto al blocco e con lo stesso " +
                         "nome: lo rifà quando chiudi Ardesia o apri un altro blocco appunti, se ci sono modifiche, e lo carica sempre " +
                         "nello stesso file. Gli alunni lo aprono col link da qualsiasi computer, tablet o telefono: il link non cambia mai.";
        RefreshDrive();
    }

    /// <summary>Stato del PDF su Drive, link e note.</summary>
    void RefreshDrive()
    {
        if (_driveId == null) return;
        var info = DriveSync.Current?.ClassPdfStatus(_driveId);
        bool has = info?.Id != null;
        CopyLinkButton.IsEnabled = OpenPdfButton.IsEnabled = has;
        string text;
        if (info == null)
            text = "Accedi a Google per vedere il PDF per la classe su Drive.";
        else if (info.Uploading)
            text = has ? "Aggiorno il PDF su Google Drive…" : "Carico il PDF su Google Drive…";
        else if (info.Pending)
            text = "PDF rifatto su questo PC: Ardesia lo carica su Google Drive appena può.";
        else if (!has)
            text = "Non c'è ancora su Google Drive: Ardesia lo crea quando chiudi Ardesia o apri un altro blocco appunti " +
                   "(oppure subito, con «Aggiorna ora»).";
        else
            text = info.Uploaded is { } t ? $"Su Google Drive, aggiornato {DriveUi.When(t)}." : "Su Google Drive.";
        if (info?.Error != null && !info.Uploading) text += $"\nUltimo tentativo non riuscito: {info.Error}";
        if (has && Enabled.IsChecked != true) text += "\nSpento: il PDF già su Drive resta, col suo link, ma non si aggiorna più.";
        DriveStatus.Text = text;

        bool refused = info?.ShareError != null;
        DriveNote.Text = refused
            ? $"Google non ha permesso ad Ardesia di condividerlo con la scuola ({info!.ShareError}). Condividilo a mano: «Apri il PDF su " +
              $"Drive», poi «Condividi» e, in «Accesso generale», «{GoogleClientConfig.SchoolDomain}» (chiunque abbia il link)."
            : has
                ? $"Lo apre chi ha il link e un account @{GoogleClientConfig.SchoolDomain}: gli alunni, anche a casa. Il link non cambia " +
                  "mai: basta incollarlo una volta su Classroom."
                : "";
        DriveNote.Foreground = refused ? new SolidColorBrush(Color.FromRgb(0xB3, 0x26, 0x1E)) : (Brush)FindResource("SubtleTextBrush");
        DriveNote.Visibility = DriveNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (_driveId == null || DriveSync.Current?.ClassPdfStatus(_driveId).Link is not { } link) return;
        try
        {
            Clipboard.SetText(link);
            ShowMessage("Link copiato: incollalo su Classroom (o dove lo leggono gli alunni).", error: false);
        }
        catch (Exception ex)
        {
            ShowMessage($"Non è stato possibile copiare il link ({ex.Message}). Eccolo: {link}", error: true);
        }
    }

    void OpenPdf_Click(object sender, RoutedEventArgs e)
    {
        if (_driveId == null || DriveSync.Current?.ClassPdfStatus(_driveId).Link is not { } link) return;
        try
        {
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowMessage($"Non è stato possibile aprire il browser: {ex.Message}", error: true);
        }
    }

    /// <summary>Lega l'abilitazione di un pulsante (es. «Aggiorna ora») all'interruttore del PDF.</summary>
    public void BindToEnabled(UIElement element) =>
        BindingOperations.SetBinding(element, IsEnabledProperty, new Binding(nameof(CheckBox.IsChecked)) { Source = Enabled });

    void AddSection(NotebookNode s, int depth)
    {
        var cb = new CheckBox
        {
            Content = s.DisplayTitle,
            IsChecked = !_nb!.ClassPdfExcluded.Contains(s.Id),
            Margin = new Thickness(depth * 22, 4, 0, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _checks[s.Id] = cb;
        SectionList.Children.Add(cb);
        foreach (var sub in s.Sections) AddSection(sub, depth + 1);
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "PDF per la classe",
            Filter = "Documento PDF (*.pdf)|*.pdf",
            FileName = Path.GetFileName(PathBox.Text),
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(PathBox.Text)) ? Path.GetDirectoryName(PathBox.Text) : null,
            OverwritePrompt = false,
        };
        DriveLocator.AddPlaces(dlg);
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) PathBox.Text = dlg.FileName;
    }

    /// <summary>Scrive le scelte nel blocco appunti; false (con un messaggio) se la cartella non va.</summary>
    public bool Apply()
    {
        if (_nb == null) return true;
        Message.Visibility = Visibility.Collapsed;
        if (Enabled.IsChecked == true && _driveId == null)
        {
            var dir = Path.GetDirectoryName(PathBox.Text);
            if (string.IsNullOrWhiteSpace(PathBox.Text) || dir == null || !Directory.Exists(dir))
            {
                ShowMessage("La cartella del PDF non esiste: scegline un'altra con «Scegli…».", error: true);
                return false;
            }
        }
        _nb.ClassPdfDisabled = Enabled.IsChecked != true;
        // Il percorso proposto non si memorizza: così segue il blocco appunti se viene spostato o rinominato.
        if (Enabled.IsChecked == true && _driveId == null)
            _nb.ClassPdfPath = string.Equals(Path.GetFullPath(PathBox.Text), Path.GetFullPath(_defaultPath), StringComparison.OrdinalIgnoreCase)
                ? null : PathBox.Text;
        _nb.ClassPdfExcluded = _checks.Where(c => c.Value.IsChecked != true).Select(c => c.Key).ToList();
        return true;
    }

    public void ShowMessage(string text, bool error)
    {
        Message.Text = text;
        Message.Foreground = error ? new SolidColorBrush(Color.FromRgb(0xB3, 0x26, 0x1E)) : (Brush)FindResource("AccentBrush");
        Message.Visibility = Visibility.Visible;
    }
}
