using System.IO;
using System.Windows;
using System.Windows.Controls;
using Ardesia.Models;
using Ardesia.Services;

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
