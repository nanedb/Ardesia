using System.IO;
using System.Windows;
using System.Windows.Input;
using Ardesia.Controls;
using Ardesia.Models;
using Ardesia.Services;
using Ardesia.Services.Google;
using Microsoft.Win32;

namespace Ardesia;

/// <summary>
/// «Copia in un altro blocco appunti…» (menu File, e menu contestuali di sezioni e pagine): la copia si fa sul file
/// del blocco di destinazione, senza aprirlo. Per un blocco su Drive si scarica prima la versione nuova (come
/// all'apertura) e poi le modifiche vanno nella coda di caricamento; il suo PDF per la classe si rifà subito.
/// </summary>
public partial class MainWindow
{
    const string DriveTargetPrefix = "drive:";

    /// <param name="node">Sezione o pagina da copiare; null = tutto il blocco appunti.</param>
    void CopyToNotebook(NotebookNode? node)
    {
        Editor.Flush();
        var sync = _settings.Persist ? DriveSync.Current : null;
        CopyToNotebookDialog? dlg = null;
        dlg = new CopyToNotebookDialog(NotebookCopy.Describe(_nb, node), sync != null)
        {
            Owner = this,
            PickDrive = () =>
            {
                if (sync == null) return null;
                var browser = new DriveBrowserWindow(sync, _driveId, pick: true) { Owner = dlg };
                return browser.ShowDialog() == true && browser.ResultId is { } id
                    ? (DriveTargetPrefix + id, $"«{browser.ResultTitle}» (Google Drive)")
                    : null;
            },
            PickComputer = () =>
            {
                var path = StartWindow.PickNotebookFile(dlg, _settings);
                if (path == null) return null;
                if (_path != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
                {
                    AppMessage.Show(dlg, "È il blocco appunti aperto: scegline un altro.");
                    return null;
                }
                return (path, $"«{Path.GetFileNameWithoutExtension(path)}» ({path})");
            },
        };
        if (dlg.ShowDialog() != true) return;

        // il file su cui copiare
        string? target;
        bool replace;
        if (dlg.ToNew)
        {
            target = StartWindow.CreateNotebook(this, _settings);
            replace = true; // il blocco nuovo ha solo una sezione vuota
        }
        else
        {
            target = dlg.Target!.StartsWith(DriveTargetPrefix) ? DownloadForCopy(dlg.Target[DriveTargetPrefix.Length..]) : dlg.Target;
            replace = dlg.Replace;
        }
        if (target == null) return;

        string title;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var nb = NotebookStore.Load(target);
            NotebookCopy.Copy(_nb, node, nb, replace);
            NotebookStore.Save(nb, target);
            title = nb.Title;
            if (DriveIdOf(target) is { } id)
            {
                DriveSync.NotifySaved(target, nb.Title);
                DriveSync.Current?.UploadSoon(id);
            }
            if (WriteClassPdf(nb, target) is { } pdfError)
                AppMessage.Show(this, pdfError + "\n\nLa copia è fatta; manca solo il PDF per la classe, che si rifarà quando apri quel blocco appunti e lo modifichi.",
                    MessageIcon.Warning, "PDF per la classe non aggiornato");
        }
        catch (Exception ex)
        {
            AppMessage.Show(this, $"{target}\n\n{ex.Message}", MessageIcon.Error, "Non è stato possibile copiare nel blocco appunti");
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
        _settings.AddRecent(target);
        if (_path != null) _settings.AddRecent(_path); // il blocco aperto resta il primo dei recenti
        _settings.Save();

        StatusText.Text = $"Copiato in «{title}»";
        StatusText.ToolTip = null;
        if (AppMessage.Ask(this, $"Ho copiato {NotebookCopy.Describe(_nb, node)} in «{title}».",
                $"Apri «{title}»", "Resta qui", "Copia fatta", MessageIcon.Info, safeDefault: true)
            && ConfirmClose())
            TryOpen(target);
    }

    /// <summary>Blocco su Drive in cui copiare: si scarica la versione nuova (come all'apertura); null se non si può.</summary>
    string? DownloadForCopy(string id)
    {
        if (DriveSync.Current is not { } sync) return null;
        try
        {
            return BusyDialog.Run(this, "Scarico il blocco appunti da Google Drive…",
                (p, ct) => sync.OpenAsync(id, p, ct, ConfirmOpenElsewhere)).CachePath;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (DriveGoneException ex)
        {
            DriveUi.HandleGone(this, sync, id, ex, _settings);
            return null;
        }
        catch (Exception ex)
        {
            AppMessage.Show(this, ex.Message, MessageIcon.Error, "Impossibile scaricare il blocco appunti da Google Drive");
            return null;
        }
    }

    /// <summary>
    /// PDF per la classe di un blocco che non è aperto (dopo una copia), come <see cref="UpdateClassPdf"/>: prima un file
    /// temporaneo, poi al suo posto; per un blocco su Drive accanto alla cache e poi in coda. Un messaggio d'errore, o null.
    /// </summary>
    static string? WriteClassPdf(Notebook nb, string path)
    {
        bool drive = DriveSync.TryParseCachePath(path, out _, out _);
        var pdf = drive ? nb.ClassPdfDisabled ? null : DriveSync.ClassPdfFileFor(path) : nb.ClassPdfFile(path);
        if (pdf == null) return null;
        var pages = nb.ClassPdfPages().ToList();
        if (pages.Count == 0) return null;
        var dir = Path.GetDirectoryName(pdf);
        if (dir == null || !Directory.Exists(dir)) return $"Non trovo la cartella del PDF per la classe:\n{dir}";
        var tmp = Path.Combine(dir, "~" + Path.GetFileName(pdf) + ".tmp");
        try
        {
            PdfExporter.Export(nb, pages, tmp, nb.Title);
            File.Move(tmp, pdf, overwrite: true);
            if (drive) DriveSync.NotifyClassPdf(path);
            return null;
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { /* già assente */ }
            return $"Non è stato possibile aggiornare il PDF per la classe:\n{pdf}\n\n{ex.Message}";
        }
    }
}
