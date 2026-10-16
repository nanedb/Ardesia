using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Ardesia.Models;
using Ardesia.Services;
using Microsoft.Win32;

namespace Ardesia.Controls;

/// <summary>Impostazioni del PDF per la classe: dove salvarlo e quali sezioni includere.</summary>
public partial class ClassPdfPanel : UserControl
{
    Notebook? _nb;
    readonly Dictionary<Guid, CheckBox> _checks = new();

    /// <summary>Accanto al file del blocco appunti, con lo stesso nome.</summary>
    string _defaultPath = "";

    public ClassPdfPanel()
    {
        InitializeComponent();
    }

    public void Load(Notebook nb, string notebookPath)
    {
        _nb = nb;
        _defaultPath = Path.ChangeExtension(notebookPath, ".pdf");
        Enabled.IsChecked = !nb.ClassPdfDisabled;
        PathBox.Text = nb.ClassPdfPath ?? _defaultPath;
        _checks.Clear();
        SectionList.Children.Clear();
        foreach (var s in nb.Sections) AddSection(s, 0);
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
        if (Enabled.IsChecked == true)
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
        if (Enabled.IsChecked == true)
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
