using System.IO;
using System.Windows;
using System.Windows.Controls;
using Ardesia.Models;
using Ardesia.Services;
using Microsoft.Win32;

namespace Ardesia;

/// <summary>Impostazioni del PDF per la classe: dove salvarlo e quali sezioni includere.</summary>
public partial class ClassPdfDialog : Window
{
    readonly Notebook _nb;
    readonly Dictionary<Guid, CheckBox> _checks = new();

    /// <summary>Chiamato da «Aggiorna ora» dopo aver applicato le impostazioni: restituisce un messaggio di errore o null.</summary>
    public Func<string?>? UpdateNow { get; init; }

    public ClassPdfDialog(Notebook nb)
    {
        InitializeComponent();
        _nb = nb;
        Enabled.IsChecked = nb.ClassPdfPath != null;
        PathBox.Text = nb.ClassPdfPath ?? DefaultPath();
        foreach (var s in nb.Sections) AddSection(s, 0);
    }

    string DefaultPath()
    {
        var folder = DriveLocator.GoogleDriveFolders().FirstOrDefault()
                     ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(folder, MainWindow.SafeFileName(_nb.Title) + ".pdf");
    }

    void AddSection(NotebookNode s, int depth)
    {
        var cb = new CheckBox
        {
            Content = s.DisplayTitle,
            IsChecked = !_nb.ClassPdfExcluded.Contains(s.Id),
            Margin = new Thickness(depth * 22, 4, 0, 4),
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
        if (dlg.ShowDialog(this) == true) PathBox.Text = dlg.FileName;
    }

    bool Apply()
    {
        Message.Visibility = Visibility.Collapsed;
        if (Enabled.IsChecked == true)
        {
            var dir = Path.GetDirectoryName(PathBox.Text);
            if (string.IsNullOrWhiteSpace(PathBox.Text) || dir == null || !Directory.Exists(dir))
            {
                ShowMessage("La cartella del PDF non esiste: scegline un'altra con «Scegli…».");
                return false;
            }
        }
        _nb.ClassPdfPath = Enabled.IsChecked == true ? PathBox.Text : null;
        _nb.ClassPdfExcluded = _checks.Where(c => c.Value.IsChecked != true).Select(c => c.Key).ToList();
        return true;
    }

    void ShowMessage(string text)
    {
        Message.Text = text;
        Message.Visibility = Visibility.Visible;
    }

    void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (!Apply() || UpdateNow == null) return;
        if (UpdateNow() is { } error)
        {
            ShowMessage(error);
            return;
        }
        DialogResult = true;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Apply()) DialogResult = true;
    }
}
