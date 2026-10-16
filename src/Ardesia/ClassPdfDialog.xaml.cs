using System.Windows;
using Ardesia.Models;

namespace Ardesia;

/// <summary>Impostazioni del PDF per la classe: dove salvarlo e quali sezioni includere.</summary>
public partial class ClassPdfDialog : Window
{
    /// <summary>Chiamato da «Aggiorna ora» dopo aver applicato le impostazioni: restituisce un messaggio di errore o null.</summary>
    public Func<string?>? UpdateNow { get; init; }

    public ClassPdfDialog(Notebook nb, string notebookPath)
    {
        InitializeComponent();
        Panel.Load(nb, notebookPath);
        Panel.BindToEnabled(UpdateNowButton);
    }

    void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (!Panel.Apply() || UpdateNow == null) return;
        if (UpdateNow() is { } error)
        {
            Panel.ShowMessage(error, error: true);
            return;
        }
        DialogResult = true;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Panel.Apply()) DialogResult = true;
    }
}
