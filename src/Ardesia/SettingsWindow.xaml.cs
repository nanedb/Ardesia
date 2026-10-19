using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ardesia.Models;
using Ardesia.Services;
using Ardesia.Services.Google;

namespace Ardesia;

/// <summary>
/// Impostazioni, divise in due schede: «Questo PC» (settings.json, valgono per tutti i blocchi appunti)
/// e «Questo blocco appunti» (salvate nel file .ardesia, lo seguono anche su un'altra LIM).
/// </summary>
public partial class SettingsWindow : Window
{
    readonly AppSettings _settings;
    readonly Notebook _nb;
    readonly List<string> _recent;
    string? _updatedWith; // impostazioni del PDF con cui è stato rifatto da «Aggiorna ora il PDF»

    /// <summary>Chiamato da «Aggiorna ora il PDF» dopo aver applicato le impostazioni: restituisce un messaggio di errore o null.</summary>
    public Func<string?>? UpdateNow { get; init; }

    /// <summary>Vero se il PDF per la classe è stato rifatto con «Aggiorna ora il PDF».</summary>
    public bool ClassPdfUpdated => _updatedWith != null && _updatedWith == ClassPdfKey();

    string ClassPdfKey() => $"{_nb.ClassPdfDisabled}|{_nb.ClassPdfPath}|{string.Join(",", _nb.ClassPdfExcluded)}";

    public SettingsWindow(AppSettings settings, Notebook nb, string? notebookPath)
    {
        InitializeComponent();
        _settings = settings;
        _nb = nb;
        _recent = settings.RecentFiles.ToList();
        FingerPansBox.IsChecked = settings.FingerPans;
        FillRecent();
        UpdateGoogle();
        Closing += (_, _) => _googleCts?.Cancel();

        if (notebookPath != null)
        {
            NotebookHint.Text = $"Queste impostazioni sono salvate nel blocco appunti «{Path.GetFileName(notebookPath)}»: " +
                                "valgono anche quando lo apri su un altro computer o su un'altra LIM.";
            ClassPdf.Load(nb, notebookPath);
            ClassPdf.BindToEnabled(UpdateNowButton);
        }
        else
        {
            NotebookHint.Text = "Salva prima il blocco appunti: le sue impostazioni vengono salvate nel file.";
            ClassPdf.Visibility = UpdateNowButton.Visibility = Visibility.Collapsed;
            ClassPdf.IsEnabled = false;
        }
    }

    /// <summary>Apre direttamente la scheda del blocco appunti (usato anche dalle prove automatiche).</summary>
    public void ShowNotebookTab() => Tabs.SelectedItem = NotebookTab;

    void FillRecent()
    {
        RecentList.Children.Clear();
        for (int i = 0; i < _recent.Count; i++)
        {
            var file = _recent[i];
            bool exists = File.Exists(file);
            var name = new TextBlock
            {
                Text = Path.GetFileNameWithoutExtension(file) + (exists ? "" : "  (non trovato)"),
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var folder = new TextBlock
            {
                Text = Path.GetDirectoryName(file),
                FontSize = 12.5,
                Foreground = (System.Windows.Media.Brush)FindResource("SubtleTextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var remove = new Button
            {
                Content = "Togli",
                Style = (Style)FindResource("BigPlain"),
                MinWidth = 90,
                Padding = new Thickness(14, 8, 14, 8),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Togli dall'elenco (il file non viene cancellato)",
            };
            remove.Click += (_, _) =>
            {
                _recent.Remove(file);
                FillRecent();
            };
            var row = new DockPanel { Margin = new Thickness(12, 8, 8, 8), ToolTip = file };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                Children = { name, folder },
            });
            if (i > 0) RecentList.Children.Add(new Border { Height = 1, Background = (System.Windows.Media.Brush)FindResource("LineBrush") });
            RecentList.Children.Add(row);
        }
        bool empty = _recent.Count == 0;
        RecentEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ((Border)RecentList.Parent).Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ClearRecentButton.IsEnabled = !empty;
    }

    // ───────────── Google Drive (accesso e uscita valgono subito, anche se poi si preme Annulla) ─────────────

    CancellationTokenSource? _googleCts;

    void UpdateGoogle()
    {
        var g = GoogleAccountService.Current;
        if (_googleCts != null)
        {
            GoogleStatus.Text = "Accesso in corso…";
            GoogleDetail.Text = "Completa l'accesso nella pagina di Google che si è aperta nel browser.";
            GoogleButton.Content = "Annulla";
            GoogleButton.Style = (Style)FindResource("BigPlain");
            GoogleButton.IsEnabled = true;
        }
        else if (g.IsSignedIn)
        {
            GoogleStatus.Text = g.DisplayName == g.Email ? $"Collegato come {g.Email}" : $"Collegato come {g.DisplayName} ({g.Email})";
            GoogleDetail.Text = $"Accesso fatto il {g.SignedInAt:d MMMM yyyy}. Ardesia lo ricorda su questo PC per il tuo utente di Windows; " +
                                "vede su Drive solo i file che crea lei.";
            GoogleButton.Content = "Esci";
            GoogleButton.Style = (Style)FindResource("BigPlain");
            GoogleButton.IsEnabled = true;
        }
        else
        {
            GoogleStatus.Text = "Non collegato";
            GoogleDetail.Text = g.IsConfigured
                ? $"Accedi con l'account @{GoogleClientConfig.SchoolDomain}: si apre il browser. Il salvataggio dei blocchi appunti su Drive arriverà presto."
                : g.ConfigError ?? "";
            GoogleButton.Content = "Accedi con Google";
            GoogleButton.Style = (Style)FindResource("BigAccent");
            GoogleButton.IsEnabled = g.IsConfigured;
        }
    }

    void ShowGoogleMessage(string? text, bool error)
    {
        GoogleMessage.Text = text ?? "";
        GoogleMessage.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        GoogleMessage.Foreground = error ? new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)) : (Brush)FindResource("SubtleTextBrush");
    }

    async void Google_Click(object sender, RoutedEventArgs e)
    {
        var g = GoogleAccountService.Current;
        if (_googleCts != null)
        {
            _googleCts.Cancel();
            return;
        }
        ShowGoogleMessage(null, false);
        if (g.IsSignedIn)
        {
            GoogleButton.IsEnabled = false;
            var warning = await g.SignOutAsync(CancellationToken.None);
            ShowGoogleMessage(warning ?? "Sei uscito da Google: Ardesia non può più accedere al tuo Drive.", warning != null);
            UpdateGoogle();
            return;
        }

        _googleCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        UpdateGoogle();
        try
        {
            await g.SignInAsync(_googleCts.Token);
        }
        catch (OperationCanceledException)
        {
            ShowGoogleMessage("Accesso annullato.", false);
        }
        catch (Exception ex)
        {
            ShowGoogleMessage(ex.Message, true);
        }
        finally
        {
            _googleCts.Dispose();
            _googleCts = null;
        }
        if (!IsLoaded) return; // finestra chiusa durante l'accesso
        UpdateGoogle();
        Activate(); // il browser era davanti
    }

    void ClearRecent_Click(object sender, RoutedEventArgs e)
    {
        _recent.Clear();
        FillRecent();
    }

    void TouchTest_Click(object sender, RoutedEventArgs e) =>
        new TouchTestWindow { Owner = this, WindowState = WindowState.Maximized }.ShowDialog();

    void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (!ClassPdf.Apply() || UpdateNow == null) return;
        if (UpdateNow() is { } error)
        {
            ClassPdf.ShowMessage(error, error: true);
            return;
        }
        _updatedWith = ClassPdfKey();
        ClassPdf.ShowMessage($"PDF per la classe aggiornato alle {DateTime.Now:HH:mm}.", error: false);
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (ClassPdf.IsEnabled && !ClassPdf.Apply())
        {
            ShowNotebookTab();
            return;
        }
        _settings.FingerPans = FingerPansBox.IsChecked == true;
        _settings.RecentFiles = _recent;
        _settings.Save();
        DialogResult = true;
    }
}
