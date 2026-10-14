using System.Windows;
using Ardesia.Services;

namespace Ardesia;

public enum ExportScope { Page, Section, Notebook }

public partial class ExportDialog : Window
{
    public ExportDialog(string pageTitle, string sectionTitle, string notebookTitle, ExportScope initial, bool hasSubpages)
    {
        InitializeComponent();
        ScopePage.Content = $"La pagina corrente  ·  «{pageTitle}»";
        ScopeSection.Content = $"La sezione corrente, con le sottosezioni  ·  «{sectionTitle}»";
        ScopeNotebook.Content = $"L'intero blocco appunti  ·  «{notebookTitle}»";
        IncludeSubpages.IsChecked = hasSubpages;
        IncludeSubpages.Visibility = hasSubpages ? Visibility.Visible : Visibility.Collapsed;
        (initial switch
        {
            ExportScope.Section => ScopeSection,
            ExportScope.Notebook => ScopeNotebook,
            _ => ScopePage,
        }).IsChecked = true;

        var drives = DriveLocator.GoogleDriveFolders();
        DriveHint.Text = drives.Count > 0
            ? $"Per condividere il PDF con la classe salvalo nella cartella di Google Drive della classe. " +
              $"Ho trovato Google Drive in «{drives[0]}»: lo trovi anche nella barra laterale della finestra di salvataggio."
            : "Per condividere il PDF con la classe salvalo in una cartella di Google Drive condivisa con gli alunni. " +
              "Installa «Google Drive per desktop» per vedere il Drive della scuola come un'unità (di solito G:).";
    }

    public ExportScope Scope =>
        ScopeSection.IsChecked == true ? ExportScope.Section :
        ScopeNotebook.IsChecked == true ? ExportScope.Notebook : ExportScope.Page;

    public bool IncludeSubpagesChecked => IncludeSubpages.IsChecked == true;
    public bool OpenAfterExport => OpenAfter.IsChecked == true;

    void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
