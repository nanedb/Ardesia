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

    /// <summary>Accanto al file del blocco appunti, con lo stesso nome.</summary>
    readonly string _defaultPath;

    public ClassPdfDialog(Notebook nb, string notebookPath)
    {
        InitializeComponent();
        _nb = nb;
        _defaultPath = Path.ChangeExtension(notebookPath, ".pdf");
        Enabled.IsChecked = !nb.ClassPdfDisabled;
        PathBox.Text = nb.ClassPdfPath ?? _defaultPath;
        foreach (var s in nb.Sections) AddSection(s, 0);
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
        _nb.ClassPdfDisabled = Enabled.IsChecked != true;
        // Il percorso proposto non si memorizza: così segue il blocco appunti se viene spostato o rinominato.
        if (Enabled.IsChecked == true)
            _nb.ClassPdfPath = string.Equals(Path.GetFullPath(PathBox.Text), Path.GetFullPath(_defaultPath), StringComparison.OrdinalIgnoreCase)
                ? null : PathBox.Text;
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
