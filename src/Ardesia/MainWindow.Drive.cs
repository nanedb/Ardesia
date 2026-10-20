using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Ardesia.Controls;
using Ardesia.Services;
using Ardesia.Services.Google;
using Microsoft.Win32;

namespace Ardesia;

/// <summary>
/// Blocchi appunti su Google Drive (docs/google-drive.md, fase 2). Il file aperto resta la copia in cache
/// (<c>_path</c>), quindi salvataggio automatico, salvataggio di emergenza e NotebookStore non cambiano; qui c'è solo
/// quello che serve in più: apertura con download, «da caricare» dopo ogni salvataggio, stato nella barra, «Esci».
/// </summary>
public partial class MainWindow
{
    /// <summary>ID su Drive del blocco aperto, o null per un file sul computer.</summary>
    string? _driveId;
    bool _closeDriveNotebook; // dopo «Esci da Google» il blocco su Drive aperto va chiuso

    static string? DriveIdOf(string? path) => DriveSync.TryParseCachePath(path, out _, out var id) ? id : null;

    bool _exitingDrive; // chiusura di Ardesia: i messaggi dei conflitti si mostrano alla fine
    readonly List<string> _exitDriveNotes = new();

    void InitDrive()
    {
        Action<string> onStatus = id =>
        {
            if (id == _driveId) UpdateDriveStatus();
        };
        DriveSync.StatusChanged += onStatus;
        Action<DriveFork> onForked = OnDriveForked;
        DriveSync.Forked += onForked;
        Closed += (_, _) =>
        {
            DriveSync.StatusChanged -= onStatus;
            DriveSync.Forked -= onForked;
        };
        // «Conflitto: salvata una copia» nella barra: clic → spiegazione
        StatusText.MouseLeftButtonUp += (_, _) =>
        {
            if (_driveId != null && DriveSync.Current?.ConflictMessage(_driveId) is { } m)
                MessageBox.Show(this, m, "Ardesia – Conflitto su Google Drive", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        _ = DriveSync.Current; // riprende i caricamenti rimasti in coda dall'ultima volta
    }

    /// <summary>Cambiato il blocco aperto: il lucchetto morbido (§4.4) passa a quello nuovo, o si toglie.</summary>
    void DriveNotebookChanged()
    {
        if (DriveSync.Current is { } sync)
            sync.SetOpen(_driveId != null && DriveSync.TryParseCachePath(_path, out var key, out _) && key == sync.AccountKeyValue ? _driveId : null);
        UpdateDriveStatus();
    }

    /// <summary>
    /// Conflitto durante un caricamento (§4.4): le modifiche di questo PC sono andate in un file nuovo. Se è il blocco
    /// aperto, Ardesia continua su quello (nuovo percorso in cache, nuovo titolo); il messaggio si mostra una volta.
    /// </summary>
    void OnDriveForked(DriveFork f)
    {
        if (f.OldId == _driveId && DriveSync.Current is { } sync)
        {
            _path = sync.CachePath(f.NewId);
            _driveId = f.NewId;
            _nb.Title = f.NewTitle; // si salva nella copia nuova, il nome su Drive è già quello
            _settings.AddRecent(_path);
            _settings.Save();
            UpdateTitle();
            UpdateDriveStatus();
        }
        if (_exitingDrive) _exitDriveNotes.Add(f.Message);
        else
            Dispatcher.BeginInvoke(() =>
            {
                if (IsVisible) MessageBox.Show(this, f.Message, "Ardesia – Conflitto su Google Drive", MessageBoxButton.OK, MessageBoxImage.Information);
            });
    }

    /// <summary>
    /// Prima di aprire un blocco su Drive: metadati, ed eventualmente download della versione nuova (con la
    /// finestrella di attesa). <paramref name="path"/> resta la copia in cache (con un conflitto diventa quella della
    /// copia nuova). Falso se non si può aprire.
    /// </summary>
    bool PrepareDriveOpen(ref string path, bool quiet, out DriveOpenResult? result)
    {
        result = null;
        if (!DriveSync.TryParseCachePath(path, out var key, out var id)) return true;
        var sync = DriveSync.Current;
        if (sync == null || sync.AccountKeyValue != key)
        {
            var d = DriveSync.Describe(path);
            if (!quiet)
                MessageBox.Show(this, $"«{d?.Title}» è su Google Drive: per aprirlo accedi a Google" +
                                      (d?.Email is { } who ? $" con {who}" : "") + " (File › Impostazioni › Google Drive).",
                    "Ardesia", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        var title = sync.Entry(id)?.Title;
        try
        {
            result = BusyDialog.Run(this, title != null ? $"Apro «{title}» da Google Drive…" : "Apro il blocco appunti da Google Drive…",
                (p, ct) => sync.OpenAsync(id, p, ct, ConfirmOpenElsewhere));
            path = result.CachePath;
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (DriveGoneException ex)
        {
            DriveUi.HandleGone(this, sync, id, ex, _settings);
            return false;
        }
        catch (Exception ex)
        {
            if (!quiet)
                MessageBox.Show(this, $"Impossibile aprire il blocco appunti da Google Drive.\n\n{ex.Message}", "Ardesia",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>
    /// Lucchetto di un altro PC (§4.4): si chiede se aprire lo stesso. Il pulsante predefinito è «No» (annulla),
    /// contro i tocchi per sbaglio sulla LIM.
    /// </summary>
    bool ConfirmOpenElsewhere(DriveLock lk)
    {
        Window owner = Application.Current.Windows.OfType<BusyDialog>().FirstOrDefault(w => w.IsVisible) ?? (Window)this;
        return MessageBox.Show(owner,
            $"Questo blocco appunti risulta aperto anche sul PC {lk.Pc} (ultima attività alle {lk.At:HH:mm}). " +
            "Se lo modifichi qui e là, le modifiche non si uniscono.\n\nSì = aprilo lo stesso    No = annulla",
            "Ardesia – Blocco aperto su un altro PC", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    /// <summary>
    /// Dopo l'apertura: se su Drive il blocco è stato rinominato (dal sito, o per un conflitto) il titolo segue il
    /// nome; avvisi e conflitti vanno nella barra.
    /// </summary>
    void AfterDriveOpen(DriveOpenResult? r)
    {
        if (_driveId == null || DriveSync.Current?.Entry(_driveId) is not { } e) return;
        if (!e.Pending && e.Title.Length > 0 && DriveSync.SafeName(_nb.Title) != e.Title) _nb.Title = e.Title;
        UpdateDriveStatus();
        if (r?.Warning != null)
        {
            StatusText.Text = r.Warning;
            StatusText.ToolTip = "Le versioni precedenti restano nella cronologia del file su Google Drive.";
        }
        if (r?.Conflict is { } conflict)
            Dispatcher.BeginInvoke(() => MessageBox.Show(this, conflict, "Ardesia – Conflitto su Google Drive",
                MessageBoxButton.OK, MessageBoxImage.Information));
    }

    /// <summary>
    /// Chiusura di Ardesia (§4.5), dopo ConfirmClose: si toglie il lucchetto e si caricano le modifiche in coda, con
    /// «Carico su Google Drive… (2,1 MB)» ed «Esci senza aspettare» (la finestrella compare solo se ci vuole più di un
    /// attimo). Se tutto va bene si esce senza domande; altrimenti si avvisa che le modifiche restano su questo PC.
    /// </summary>
    void FinishDriveOnExit()
    {
        if (!_settings.Persist || DriveSync.Current is not { } sync) return;
        _exitingDrive = true;
        sync.SetOpen(null);
        if (!sync.HasWork) return;
        var bytes = sync.PendingBytes();
        bool flushed;
        try
        {
            flushed = BusyDialog.Run(this, bytes > 0 ? $"Carico su Google Drive… ({DriveUi.Size(bytes)})" : "Aggiorno Google Drive…",
                (p, ct) => sync.FlushAsync(TimeSpan.FromMinutes(3), ct, p), "Esci senza aspettare");
        }
        catch (OperationCanceledException)
        {
            flushed = false;
        }
        foreach (var note in _exitDriveNotes)
            MessageBox.Show(this, note, "Ardesia – Conflitto su Google Drive", MessageBoxButton.OK, MessageBoxImage.Information);
        if (!flushed && sync.HasPending)
            MessageBox.Show(this,
                "Le ultime modifiche sono salvate su questo PC ma non ancora su Google Drive. Verranno caricate la prossima " +
                "volta che apri Ardesia su questo PC.", "Ardesia", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Dopo ogni salvataggio della copia in cache: il blocco va in coda per Drive.</summary>
    void DriveSaved()
    {
        if (_path == null) return;
        DriveSync.NotifySaved(_path, _nb.Title);
        UpdateDriveStatus();
    }

    /// <summary>Nuvola e testo nella barra in alto (docs/google-drive.md §7).</summary>
    void UpdateDriveStatus()
    {
        if (_driveId == null)
        {
            // chiuso un blocco su Drive: il suo stato non vale per quello nuovo
            if (DriveIcon.Visibility == Visibility.Visible) StatusText.Text = "";
            DriveIcon.Visibility = Visibility.Collapsed;
            return;
        }
        DriveIcon.Visibility = Visibility.Visible;
        if (DriveSync.Current is { } sync && _path != null && sync.AccountKeyValue == (DriveSync.TryParseCachePath(_path, out var k, out _) ? k : null))
        {
            var (state, text, tip) = sync.Status(_driveId);
            StatusText.Text = text;
            StatusText.ToolTip = tip;
            StatusText.Cursor = state == DriveSyncState.Conflict ? Cursors.Hand : null;
        }
        else
        {
            StatusText.Text = "Accedi a Google per caricare le modifiche";
            StatusText.ToolTip = "Le modifiche sono salvate su questo PC e verranno caricate quando accedi di nuovo.";
        }
    }

    /// <summary>Titolo della finestra per un blocco su Drive: «Fisica 2BO – Google Drive (giovanni.dalbo@…)».</summary>
    string? DriveWindowTitle() =>
        _driveId == null ? null : $"Google Drive ({DriveSync.Describe(_path)?.Email ?? "account non collegato"})";

    /// <summary>«Apri da Drive…»: l'elenco dei propri blocchi su Drive.</summary>
    void OpenFromDrive()
    {
        if (DriveSync.Current is not { } sync) return;
        var dlg = new DriveBrowserWindow(sync, _driveId) { Owner = this, Delete = n => DeleteDriveNotebook(n.Id, n.Title) };
        if (dlg.ShowDialog() != true) return;
        if (dlg.ResultNew)
        {
            NewNotebook();
            return;
        }
        if (dlg.ResultId is not { } id || id == _driveId) return;
        if (ConfirmClose()) TryOpen(sync.CachePath(id));
    }

    /// <summary>
    /// «Elimina il blocco appunti…» (§2.2). Se è quello aperto, si chiude: dopo l'eliminazione si torna alla schermata iniziale.
    /// </summary>
    bool DeleteDriveNotebook(string id, string title)
    {
        if (DriveSync.Current is not { } sync) return false;
        bool isOpen = id == _driveId;
        if (isOpen)
        {
            Editor.Flush();
            if (_dirty) Save(quiet: true);
        }
        if (!DriveUi.ConfirmAndTrash(this, sync, id, title)) return false;
        _settings.RecentFiles.RemoveAll(p => DriveIdOf(p) == id);
        if (_settings.LastFile is { } last && DriveIdOf(last) == id) _settings.LastFile = null;
        _settings.Save();
        if (isOpen)
        {
            _autosaveTimer.Stop();
            OpenInMemoryNotebook();
            Dispatcher.BeginInvoke(() => ShowStart($"«{title}» è nel cestino di Google Drive."));
        }
        return true;
    }

    /// <summary>«Salva una copia sul computer…»: il blocco su Drive resta quello aperto.</summary>
    void SaveCopyToComputer()
    {
        Editor.Flush();
        var dlg = new SaveFileDialog
        {
            Filter = NotebookStore.FileFilter,
            Title = "Salva una copia sul computer",
            FileName = SafeFileName(_nb.Title) + NotebookStore.Extension,
            InitialDirectory = ExistingFolder(_settings.LastSaveFolder),
        };
        DriveLocator.AddPlaces(dlg);
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            NotebookStore.Save(_nb, dlg.FileName);
            StatusText.Text = $"Copia salvata: {Path.GetFileName(dlg.FileName)}";
            StatusText.ToolTip = dlg.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Impossibile salvare la copia:\n{dlg.FileName}\n\n{ex.Message}", "Ardesia",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Apre nel browser la cartella del blocco su Drive (o il file, se la cartella non si conosce).</summary>
    void OpenDriveFolderInBrowser()
    {
        if (_driveId == null) return;
        var parent = DriveSync.Current?.Entry(_driveId)?.ParentId;
        var url = parent != null ? $"https://drive.google.com/drive/folders/{parent}" : $"https://drive.google.com/file/d/{_driveId}/view";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>
    /// «Esci da Google»: prima si caricano le modifiche in coda (con «Esci senza aspettare»), poi si revoca l'accesso
    /// e si toglie la cache di Drive dell'account (se non restano modifiche da caricare). Restituisce (fatto, avviso).
    /// </summary>
    async Task<(bool Done, string? Warning)> SignOutGoogleAsync()
    {
        var g = GoogleAccountService.Current;
        if (!g.IsSignedIn) return (true, null);
        var email = g.Email!;
        if (_driveId != null)
        {
            Editor.Flush();
            if (_dirty) Save(quiet: true);
        }
        DriveSync.Current?.SetOpen(null); // il lucchetto si toglie prima di uscire
        if (DriveSync.Current is { HasWork: true } sync)
        {
            bool flushed;
            try
            {
                flushed = BusyDialog.Run(this, "Carico le ultime modifiche su Google Drive…",
                    (_, ct) => sync.FlushAsync(TimeSpan.FromMinutes(2), ct), "Esci senza aspettare");
            }
            catch (OperationCanceledException)
            {
                flushed = false;
            }
            if (!flushed && MessageBox.Show(this,
                    "Alcune modifiche non sono ancora su Google Drive. Restano su questo PC e verranno caricate quando accedi " +
                    "di nuovo con questo account.\n\nUscire lo stesso da Google?",
                    "Ardesia", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                DriveNotebookChanged(); // si resta: il lucchetto torna
                return (false, null);
            }
        }
        var warning = await g.SignOutAsync(CancellationToken.None);
        _ = DriveSync.Current; // ferma la sincronizzazione dell'account
        _closeDriveNotebook = _driveId != null;
        if (_closeDriveNotebook)
        {
            _autosaveTimer.Stop();
            _dirty = false;
            OpenInMemoryNotebook();
        }
        DriveSync.DeleteAccountCache(email);
        return (true, warning);
    }

    /// <summary>Dopo «Esci da Google» con un blocco su Drive aperto: si torna alla schermata iniziale.</summary>
    void CloseDriveNotebookIfSignedOut()
    {
        if (!_closeDriveNotebook) return;
        _closeDriveNotebook = false;
        ShowStart("Sei uscito da Google: il blocco appunti su Drive è stato chiuso.");
    }
}
