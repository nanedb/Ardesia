using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using Ardesia.Models;

namespace Ardesia.Services.Google;

/// <summary>
/// Prove di DriveSync con un Drive finto in memoria (prima parte di --prova-drive): cartelle classe/materia,
/// creazione, coda di caricamento (pausa, massimo, tentativi, rinomina), sync.json dopo un riavvio, apertura con e
/// senza download, senza rete, blocco eliminato; fase 3: conflitti «tieni entrambi» all'apertura e al caricamento,
/// lucchetto morbido (proprio e di un altro PC), chiusura senza rete e ripresa quando la rete torna. Non usa la rete
/// né l'account.
/// </summary>
public static class DriveSelfTest
{
    /// <summary>Esegue le prove; <paramref name="log"/> riceve (esito, titolo, dettagli). Restituisce i problemi trovati.</summary>
    public static async Task<int> RunAsync(Action<bool?, string, string> log)
    {
        int failures = 0;
        void Check(bool ok, string title, string detail = "")
        {
            if (!ok) failures++;
            log(ok, title, detail);
        }

        var folder = Path.Combine(Path.GetTempPath(), "ardesia-prova-drive-" + Guid.NewGuid().ToString("N")[..8]);
        var drive = new FakeDrive();
        DriveSync? sync = null;
        try
        {
            Check(DriveSync.SafeName("1BE/CE") == "1BE-CE" && DriveSync.SafeName(" a:b*c? ") == "a-b-c-",
                "Caratteri non ammessi nei nomi → «-»", $"«1BE/CE» → «{DriveSync.SafeName("1BE/CE")}»");
            Check(DriveSync.SchoolYear(new DateTime(2026, 9, 1)) == "26-27" && DriveSync.SchoolYear(new DateTime(2027, 8, 31)) == "26-27"
                  && DriveSync.SchoolYear(new DateTime(2026, 8, 31)) == "25-26", "Anno scolastico (cambia a settembre)",
                $"20/10/2026 → {DriveSync.SchoolYear(new DateTime(2026, 10, 20))}");
            var cachePath = Path.Combine(DriveSync.RootFolder, "0123456789abcdef", "1AbC_x-9" + NotebookStore.Extension);
            Check(DriveSync.TryParseCachePath(cachePath, out var key, out var pid) && key == "0123456789abcdef" && pid == "1AbC_x-9"
                  && !DriveSync.TryParseCachePath(@"C:\Documenti\Fisica.ardesia", out _, out _),
                "Riconoscimento della copia in cache di un blocco su Drive");

            sync = NewSync(drive, folder);

            // ── creazione con cartelle ──
            var id = await sync.CreateNotebookAsync("2BO", "Fisica", "2BO Fisica 26-27", NewNotebook(), CancellationToken.None);
            var file = drive.Items[id];
            var subj = drive.Items[file.Parent!];
            var cls = drive.Items[subj.Parent!];
            var root = drive.Items[cls.Parent!];
            Check(file.Name == "2BO Fisica 26-27.ardesia" && file.Mime == DriveSync.MimeType && subj.Name == "Fisica" && cls.Name == "2BO"
                  && root.Name == "Ardesia" && root.Parent == null,
                "Nuovo blocco in Ardesia/2BO/Fisica", $"{root.Name}/{cls.Name}/{subj.Name}/{file.Name}");
            Check(File.Exists(sync.CachePath(id)) && sync.Entry(id) is { Pending: false, BaseRevision: "rev1" },
                "Copia in cache pronta, nulla da caricare");

            await sync.CreateNotebookAsync("2bo", "FISICA", "Altro", NewNotebook(), CancellationToken.None);
            Check(drive.Items.Values.Count(i => i.Mime == DriveApi.FolderMime) == 3,
                "Cartelle esistenti riusate senza distinguere maiuscole", $"cartelle: {drive.Items.Values.Count(i => i.Mime == DriveApi.FolderMime)}");
            await sync.CreateNotebookAsync("1BE/CE", "Matematica", "1BE-CE Matematica 26-27", NewNotebook(), CancellationToken.None);

            var listing = await sync.ListAsync(CancellationToken.None);
            Check(listing.Classes.SequenceEqual(new[] { "1BE-CE", "2BO" }) && listing.SubjectsByClass["2bo"].SequenceEqual(new[] { "Fisica" })
                  && listing.Notebooks.Count == 3 && listing.Notebooks.Count(n => n.Class == "2BO" && n.Subject == "Fisica") == 2,
                "Elenco: blocchi con classe e materia, classi lette dalle cartelle (le più recenti prima)",
                $"classi: {string.Join(", ", listing.Classes)}; materie: {string.Join(", ", listing.Subjects)}");

            // ── coda: si carica dopo una pausa ──
            Modify(sync.CachePath(id), "Pagina 2");
            sync.Changed(id, "2BO Fisica 26-27");
            await Task.Delay(150);
            bool waited = drive.Uploads == 0 && sync.Status(id).State == DriveSyncState.Pending;
            await WaitAsync(() => sync.Entry(id)!.Pending == false, 3000);
            Check(waited && drive.Uploads == 1 && sync.Entry(id)!.BaseRevision == "rev2" && sync.Status(id).State == DriveSyncState.Synced,
                "Caricamento dopo una pausa nella scrittura", $"caricamenti: {drive.Uploads}, stato: {sync.Status(id).Text}");

            // ── …e comunque al massimo ogni tanto mentre si scrive ──
            int before = drive.Uploads;
            var end = DateTime.Now.AddMilliseconds(2400);
            while (DateTime.Now < end)
            {
                sync.Changed(id, "2BO Fisica 26-27");
                await Task.Delay(120);
            }
            Check(drive.Uploads > before, "Caricamento anche scrivendo senza pause (al massimo ogni «3 minuti»)",
                $"caricamenti durante la scrittura: {drive.Uploads - before}");
            await WaitAsync(() => sync.Entry(id)!.Pending == false, 3000);

            // ── errori di rete e di Google: nuovi tentativi ──
            var states = new List<DriveSyncState>();
            Action<string> onStatus = x =>
            {
                if (x == id) states.Add(sync.Status(id).State);
            };
            DriveSync.StatusChanged += onStatus;
            drive.Failures.Enqueue(new HttpRequestException("rete assente (finta)"));
            drive.Failures.Enqueue(new DriveException(503, "backendError", "Google occupato (finto)"));
            before = drive.Uploads;
            sync.Changed(id, "2BO Fisica 26-27");
            await WaitAsync(() => sync.Entry(id)!.Pending == false, 5000);
            DriveSync.StatusChanged -= onStatus;
            Check(drive.Uploads == before + 1 && states.Contains(DriveSyncState.Offline) && states.Contains(DriveSyncState.Retrying)
                  && states[^1] == DriveSyncState.Synced,
                "Senza rete o con Google occupato si riprova", string.Join(" → ", states));

            // ── rinomina col caricamento ──
            sync.Changed(id, "Fisica 2BO / secondo quadrimestre");
            sync.UploadSoon(id);
            await WaitAsync(() => sync.Entry(id)!.Pending == false, 3000);
            Check(drive.Items[id].Name == "Fisica 2BO - secondo quadrimestre.ardesia", "Nome nuovo su Drive col caricamento", drive.Items[id].Name);

            // ── salvataggio durante un caricamento: resta «da caricare» ──
            drive.UploadDelay = 400;
            sync.Changed(id, "Fisica 2BO / secondo quadrimestre");
            sync.UploadSoon(id);
            await WaitAsync(() => sync.Status(id).State == DriveSyncState.Uploading, 2000);
            var during = sync.Status(id).State;
            before = drive.Uploads;
            sync.QuietDelay = TimeSpan.FromSeconds(30); // il secondo caricamento non deve partire da sé subito dopo il primo
            sync.Changed(id, "Fisica 2BO / secondo quadrimestre");
            await WaitAsync(() => drive.Uploads > before && sync.Status(id).State != DriveSyncState.Uploading, 2000);
            bool stillPending = sync.Entry(id)!.Pending;
            drive.UploadDelay = 0;
            sync.UploadSoon(id);
            await WaitAsync(() => sync.Entry(id)!.Pending == false, 3000);
            sync.QuietDelay = TimeSpan.FromMilliseconds(400);
            Check(during == DriveSyncState.Uploading && stillPending && !sync.Entry(id)!.Pending && drive.Uploads == before + 2,
                "Modifica durante il caricamento: si ricarica dopo",
                $"stato: {during}; ancora da caricare: {stillPending}; caricamenti: {drive.Uploads - before}");

            // ── la coda sopravvive a un riavvio (sync.json) ──
            sync.QuietDelay = sync.MaxDelay = TimeSpan.FromHours(1);
            sync.Changed(id, "Fisica 2BO / secondo quadrimestre");
            sync.Dispose();
            before = drive.Uploads;
            sync = NewSync(drive, folder);
            await WaitAsync(() => sync.Entry(id)!.Pending == false, 3000);
            Check(drive.Uploads == before + 1, "Modifiche in coda riprese dopo un riavvio di Ardesia");

            // ── apertura ──
            int downloads = drive.Downloads;
            var r = await sync.OpenAsync(id, null, CancellationToken.None);
            Check(r.Warning == null && drive.Downloads == downloads, "Apertura senza download se la copia è l'ultima versione");
            drive.ChangeRemotely(id, File.ReadAllBytes(sync.CachePath(id)), "Cambiato altrove");
            r = await sync.OpenAsync(id, null, CancellationToken.None);
            Check(drive.Downloads == downloads + 1 && NotebookStore.Load(r.CachePath).Sections.Any(s => s.Title == "Cambiato altrove"),
                "Modificato su un altro PC: si scarica la versione nuova");
            // ── conflitto all'apertura: modifiche non caricate qui, versione nuova là ──
            sync.QuietDelay = TimeSpan.FromHours(1);
            drive.ChangeRemotely(id, File.ReadAllBytes(sync.CachePath(id)), "Là");
            Modify(sync.CachePath(id), "Qui");
            sync.Changed(id, "Fisica 2BO / secondo quadrimestre");
            r = await sync.OpenAsync(id, null, CancellationToken.None);
            var openCopy = Path.GetFileNameWithoutExtension(r.CachePath);
            Check(r.Conflict != null && openCopy != id && drive.Items.TryGetValue(openCopy, out var oc)
                  && oc.Name.StartsWith("Fisica 2BO - secondo quadrimestre (modifiche da PC-PROVA, ") && oc.Parent == drive.Items[id].Parent
                  && SectionsOf(oc.Data).Contains("Qui") && !SectionsOf(oc.Data).Contains("Là")
                  && SectionsOf(drive.Items[id].Data).Contains("Là") && !SectionsOf(drive.Items[id].Data).Contains("Qui")
                  && File.Exists(r.CachePath) && !File.Exists(sync.CachePath(id)) && sync.Entry(id) is { Pending: false, BaseRevision: null }
                  && drive.Downloads == downloads + 1,
                "Modificato qui e altrove, all'apertura: le modifiche di qui in una copia nella stessa cartella",
                drive.Items.TryGetValue(openCopy, out var oc2) ? oc2.Name : "nessuna copia");
            sync.QuietDelay = TimeSpan.FromMilliseconds(400);
            id = openCopy;

            drive.Failures.Enqueue(new HttpRequestException("rete assente (finta)"));
            r = await sync.OpenAsync(id, null, CancellationToken.None);
            Check(r.Warning?.StartsWith("Senza connessione") == true, "Senza rete si apre la copia in cache, con un avviso", r.Warning ?? "");
            var other = listing.Notebooks.Last(n => n.Id != id).Id;
            File.Delete(sync.CachePath(other));
            drive.Failures.Enqueue(new HttpRequestException("rete assente (finta)"));
            string offlineError = "";
            try { await sync.OpenAsync(other, null, CancellationToken.None); }
            catch (IOException ex) { offlineError = ex.Message; }
            Check(offlineError.Length > 0, "Senza rete e senza copia: messaggio chiaro", offlineError);

            // ── blocco eliminato dal sito di Drive ──
            drive.Items[other].Trashed = true;
            bool gone = false;
            try { await sync.OpenAsync(other, null, CancellationToken.None); }
            catch (DriveGoneException) { gone = true; }
            Check(gone, "Blocco nel cestino di Drive: Ardesia se ne accorge all'apertura");

            // ── errore definitivo nel caricamento (403/404): niente tentativi infiniti ──
            drive.Failures.Enqueue(new DriveException(404, "notFound", "File not found (finto)"));
            sync.Changed(id, "Fisica 2BO / secondo quadrimestre");
            sync.UploadSoon(id);
            await WaitAsync(() => sync.Status(id).State == DriveSyncState.Failed, 2000);
            Check(sync.Status(id).State == DriveSyncState.Failed && sync.Entry(id)!.Pending, "Blocco non più accessibile: avviso, le modifiche restano qui",
                sync.Status(id).Text);
            sync.UploadSoon(id); // riprova a mano (cambio di blocco)
            await WaitAsync(() => sync.Entry(id)!.Pending == false, 3000);

            // ── lucchetto morbido: scritto all'apertura, rinnovato, tolto alla chiusura ──
            var props = drive.Items[id].Props;
            sync.LockRefresh = TimeSpan.FromMilliseconds(500);
            sync.SetOpen(id);
            await WaitAsync(() => props.GetValueOrDefault(DriveSync.LockPcKey) == "PC-PROVA", 2000);
            var firstTime = props.GetValueOrDefault(DriveSync.LockTimeKey);
            Check(props.GetValueOrDefault(DriveSync.LockPcKey) == "PC-PROVA" && DateTime.TryParse(firstTime, null,
                      System.Globalization.DateTimeStyles.RoundtripKind, out var at) && Math.Abs((DateTime.UtcNow - at.ToUniversalTime()).TotalMinutes) < 1,
                "Lucchetto scritto all'apertura (apertoSu, apertoAlle)", $"apertoSu={props.GetValueOrDefault(DriveSync.LockPcKey)}, apertoAlle={firstTime}");
            await WaitAsync(() => props.GetValueOrDefault(DriveSync.LockTimeKey) != firstTime, 2000);
            Check(props.GetValueOrDefault(DriveSync.LockTimeKey) != firstTime, "Lucchetto rinnovato ogni tanto (solo metadati)",
                $"aggiornamenti dei metadati: {drive.MetadataUpdates}");
            sync.LockRefresh = TimeSpan.FromMinutes(5);
            var beforeUpload = props.GetValueOrDefault(DriveSync.LockTimeKey);
            await Task.Delay(20);
            Modify(sync.CachePath(id), "Col lucchetto");
            sync.Changed(id, drive.Items[id].Name.Replace(NotebookStore.Extension, ""));
            await sync.FlushAsync(TimeSpan.FromSeconds(3));
            Check(props.GetValueOrDefault(DriveSync.LockPcKey) == "PC-PROVA" && props.GetValueOrDefault(DriveSync.LockTimeKey) != beforeUpload,
                "Lucchetto aggiornato col caricamento");
            sync.SetOpen(null);
            bool released = await sync.FlushAsync(TimeSpan.FromSeconds(3));
            Check(released && !props.ContainsKey(DriveSync.LockPcKey) && !props.ContainsKey(DriveSync.LockTimeKey) && !sync.HasWork,
                "Lucchetto tolto alla chiusura del blocco");

            sync.QuietDelay = TimeSpan.FromHours(1);
            sync.SetOpen(id);
            await WaitAsync(() => props.ContainsKey(DriveSync.LockPcKey), 2000);
            Modify(sync.CachePath(id), "Chiusura con coda");
            sync.Changed(id, drive.Items[id].Name.Replace(NotebookStore.Extension, ""));
            sync.SetOpen(null);
            before = drive.Uploads;
            released = await sync.FlushAsync(TimeSpan.FromSeconds(3));
            Check(released && drive.Uploads == before + 1 && !props.ContainsKey(DriveSync.LockPcKey) && !sync.HasWork,
                "Chiusura con modifiche in coda: il caricamento toglie anche il lucchetto");
            sync.QuietDelay = TimeSpan.FromMilliseconds(400);

            // ── lucchetto di un altro PC ──
            props[DriveSync.LockPcKey] = "LIM-AULA12";
            props[DriveSync.LockTimeKey] = DateTime.UtcNow.AddMinutes(-3).ToString("o");
            DriveLock? asked = null;
            bool canceled = false;
            try { await sync.OpenAsync(id, null, CancellationToken.None, lk => { asked = lk; return false; }); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled && asked?.Pc == "LIM-AULA12" && Math.Abs((DateTime.Now.AddMinutes(-3) - asked.At).TotalSeconds) < 30,
                "Aperto su un altro PC da 3 minuti: si chiede, «Annulla» non apre", asked != null ? $"{asked.Pc} alle {asked.At:HH:mm}" : "nessuna domanda");
            asked = null;
            r = await sync.OpenAsync(id, null, CancellationToken.None, lk => { asked = lk; return true; });
            Check(asked != null && r.CachePath == sync.CachePath(id), "«Aprilo lo stesso» apre");
            listing = await sync.ListAsync(CancellationToken.None);
            Check(listing.Notebooks.FirstOrDefault(n => n.Id == id)?.OpenOn == "LIM-AULA12", "Nell'elenco: «aperto su LIM-AULA12»");

            sync.SetOpen(id);
            await WaitAsync(() => props.GetValueOrDefault(DriveSync.LockPcKey) == "PC-PROVA", 2000);
            props[DriveSync.LockPcKey] = "LIM-AULA12"; // l'altro PC lo riapre
            props[DriveSync.LockTimeKey] = DateTime.UtcNow.ToString("o");
            sync.SetOpen(null);
            await sync.FlushAsync(TimeSpan.FromSeconds(3));
            Check(props.GetValueOrDefault(DriveSync.LockPcKey) == "LIM-AULA12", "Chiudendo non si toglie il lucchetto di un altro PC");

            asked = null;
            props[DriveSync.LockTimeKey] = DateTime.UtcNow.AddMinutes(-20).ToString("o");
            await sync.OpenAsync(id, null, CancellationToken.None, lk => { asked = lk; return false; });
            bool oldAsked = asked != null;
            props[DriveSync.LockPcKey] = "PC-PROVA";
            props[DriveSync.LockTimeKey] = DateTime.UtcNow.ToString("o");
            await sync.OpenAsync(id, null, CancellationToken.None, lk => { asked = lk; return false; });
            listing = await sync.ListAsync(CancellationToken.None);
            Check(!oldAsked && asked == null && listing.Notebooks.First(n => n.Id == id).OpenOn == null,
                "Lucchetto vecchio (20 minuti) o di questo stesso PC: nessuna domanda");
            props.Clear();

            // ── conflitto al caricamento: «tieni entrambi», si continua sulla copia ──
            DriveFork? fork = null;
            Action<DriveFork> onFork = f => fork = f;
            DriveSync.Forked += onFork;
            sync.SetOpen(id);
            await WaitAsync(() => props.ContainsKey(DriveSync.LockPcKey), 2000);
            var title = drive.Items[id].Name.Replace(NotebookStore.Extension, "");
            Modify(sync.CachePath(id), "Mia modifica");
            sync.Changed(id, title);
            drive.ChangeRemotely(id, drive.Items[id].Data, "Dell'altro PC");
            props[DriveSync.LockPcKey] = "LIM-AULA12";
            props[DriveSync.LockTimeKey] = DateTime.UtcNow.ToString("o");
            sync.UploadSoon(id);
            await WaitAsync(() => fork != null && !sync.HasPending, 3000);
            DriveSync.Forked -= onFork;
            var copy = fork?.NewId ?? "";
            Check(fork != null && fork.OldId == id && drive.Items.TryGetValue(copy, out var ci)
                  && ci.Name.StartsWith(title + " (modifiche da PC-PROVA, ") && ci.Name.EndsWith(NotebookStore.Extension) && ci.Parent == drive.Items[id].Parent
                  && SectionsOf(ci.Data).Contains("Mia modifica") && !SectionsOf(ci.Data).Contains("Dell'altro PC")
                  && SectionsOf(drive.Items[id].Data).Contains("Dell'altro PC") && !SectionsOf(drive.Items[id].Data).Contains("Mia modifica")
                  && fork.NewTitle == ci.Name.Replace(NotebookStore.Extension, "") && fork.Message.Contains("LIM-AULA12"),
                "Modificato qui e altrove, al caricamento: la versione dell'altro PC resta, la nostra in una copia",
                fork != null ? $"«{fork.NewTitle}»" : "nessun conflitto");
            Check(sync.OpenId == copy && File.Exists(sync.CachePath(copy)) && !File.Exists(sync.CachePath(id))
                  && sync.Status(copy) is { State: DriveSyncState.Conflict, Text: "Conflitto: salvata una copia" }
                  && sync.ConflictMessage(copy) != null && drive.Items[copy].Props.GetValueOrDefault(DriveSync.LockPcKey) == "PC-PROVA"
                  && props.GetValueOrDefault(DriveSync.LockPcKey) == "LIM-AULA12",
                "La copia in cache passa al nuovo file, col lucchetto; stato «Conflitto: salvata una copia»", sync.Status(copy).Text);
            sync.SetOpen(null);
            await sync.FlushAsync(TimeSpan.FromSeconds(3));
            Check(sync.Status(copy).State == DriveSyncState.Synced && !drive.Items[copy].Props.ContainsKey(DriveSync.LockPcKey),
                "Chiuso il blocco, lo stato del conflitto sparisce e il lucchetto si toglie");
            id = copy;

            // ── senza rete: alla chiusura non si aspetta, poi la rete torna e si carica subito ──
            var retry = sync.RetryDelays;
            sync.RetryDelays = new[] { TimeSpan.FromMinutes(10) };
            bool network = false;
            sync.NetworkAvailable = () => network;
            for (int i = 0; i < 5; i++) drive.Failures.Enqueue(new HttpRequestException("rete assente (finta)"));
            Modify(sync.CachePath(id), "Senza rete");
            sync.Changed(id, drive.Items[id].Name.Replace(NotebookStore.Extension, ""));
            var t0 = DateTime.Now;
            bool flushedOffline = await sync.FlushAsync(TimeSpan.FromSeconds(10));
            var waitedOffline = DateTime.Now - t0;
            Check(!flushedOffline && waitedOffline < TimeSpan.FromSeconds(3) && sync.Entry(id)!.Pending && sync.Status(id).State == DriveSyncState.Offline,
                "Chiusura senza rete: non si aspetta, le modifiche restano da caricare", $"atteso {waitedOffline.TotalSeconds:0.0} s; {sync.Status(id).Text}");
            drive.Failures.Clear();
            network = true;
            sync.NetworkRestored();
            await WaitAsync(() => !sync.Entry(id)!.Pending, 2000);
            Check(!sync.Entry(id)!.Pending && SectionsOf(drive.Items[id].Data).Contains("Senza rete"),
                "La rete torna: si carica subito, senza aspettare il prossimo tentativo (10 minuti)");
            sync.RetryDelays = retry;
            sync.NetworkAvailable = () => true;

            // ── fase 4: PDF per la classe su Drive ──
            await ClassPdfTestsAsync(drive, sync, folder, Check);

            // ── eliminare: cestino, le cartelle restano ──
            await sync.TrashAsync(id, CancellationToken.None);
            Check(drive.Items[id].Trashed && !File.Exists(sync.CachePath(id)) && sync.Entry(id) == null
                  && drive.Items.Values.Where(i => i.Mime == DriveApi.FolderMime).All(f => !f.Trashed),
                "Elimina: nel cestino di Drive, via dalla cache; le cartelle restano");

            // ── cartella «Ardesia» cestinata: si ricrea ──
            drive.Items[root.Id].Trashed = true;
            await sync.EnsureFolderAsync("2BO", "Fisica", CancellationToken.None);
            Check(sync.RootFolderId != root.Id && drive.Items[sync.RootFolderId!].Name == "Ardesia", "Cartella «Ardesia» cestinata: se ne crea una nuova");
        }
        catch (Exception ex)
        {
            Check(false, "Errore imprevisto", ex.ToString());
        }
        finally
        {
            sync?.Dispose();
            try { Directory.Delete(folder, recursive: true); } catch { /* cartella temporanea */ }
        }
        return failures;
    }

    /// <summary>
    /// Fase 4 (§6): PDF creato una volta accanto al blocco e poi aggiornato nello stesso file, condiviso col dominio,
    /// rinominato col blocco, ritrovato da un altro PC, condivisione rifiutata, PDF cestinato dal sito, conflitto (il PDF
    /// resta al blocco originale), eliminazione con e senza PDF.
    /// </summary>
    static async Task ClassPdfTestsAsync(FakeDrive drive, DriveSync sync, string folder, Action<bool, string, string> report)
    {
        void check(bool ok, string title, string detail = "") => report(ok, title, detail);
        var none = CancellationToken.None;
        static byte[] Pdf(string s) => System.Text.Encoding.ASCII.GetBytes("%PDF-1.4 prova " + s);
        List<FakeDrive.Item> Pdfs() => drive.Items.Values.Where(i => i.Mime == DriveSync.PdfMime).ToList();
        async Task NewPdf(string nb, string s)
        {
            File.WriteAllBytes(sync.ClassPdfCachePath(nb), Pdf(s));
            sync.ClassPdfChanged(nb);
            await sync.FlushAsync(TimeSpan.FromSeconds(3));
        }

        var id = await sync.CreateNotebookAsync("2BO", "Fisica", "2BO Fisica PDF", NewNotebook(), none);
        int pdfsBefore = Pdfs().Count;
        await NewPdf(id, "uno");
        var pdf = Pdfs().Skip(pdfsBefore).FirstOrDefault();
        var e = sync.Entry(id)!;
        check(pdf != null && pdf.Name == "2BO Fisica PDF.pdf" && pdf.Parent == drive.Items[id].Parent && e.ClassPdfId == pdf.Id
              && pdf.Data.SequenceEqual(Pdf("uno")) && drive.Items[id].Props.GetValueOrDefault(DriveSync.ClassPdfKey) == pdf.Id
              && pdf.Props.GetValueOrDefault(DriveSync.ClassPdfOfKey) == id && !sync.HasWork,
            "PDF per la classe creato accanto al blocco, con lo stesso nome, e legato al blocco",
            pdf != null ? $"{pdf.Name}, pdfClasse={drive.Items[id].Props.GetValueOrDefault(DriveSync.ClassPdfKey)}" : "nessun PDF");
        if (pdf == null) return;
        check(pdf.Shares.SequenceEqual(new[] { "liceofoscarini.it" }) && e.ClassPdfShared,
            "Condiviso col dominio della scuola al primo caricamento", string.Join(", ", pdf.Shares));
        check(sync.ClassPdfStatus(id).Link == $"https://drive.google.com/file/d/{pdf.Id}/view", "Link per gli alunni", sync.ClassPdfStatus(id).Link ?? "");

        // aggiornato: stesso file (stesso ID e link), dopo il blocco; niente seconda condivisione
        Modify(sync.CachePath(id), "Lezione 2");
        sync.Changed(id, "2BO Fisica PDF");
        drive.Ops.Clear();
        int rev = pdf.Rev;
        await NewPdf(id, "due");
        int nbUpload = drive.Ops.IndexOf("contenuto " + id), pdfUpload = drive.Ops.IndexOf("contenuto " + pdf.Id);
        check(Pdfs().Count == pdfsBefore + 1 && pdf.Data.SequenceEqual(Pdf("due")) && pdf.Rev == rev + 1 && sync.Entry(id)!.ClassPdfId == pdf.Id
              && pdf.Shares.Count == 1 && nbUpload >= 0 && pdfUpload > nbUpload && !sync.HasWork,
            "PDF aggiornato nello stesso file (stesso link), dopo il blocco", string.Join(" → ", drive.Ops));

        // rinomina del blocco: anche il PDF
        sync.Changed(id, "2BO Fisica PDF / rinominato");
        await sync.FlushAsync(TimeSpan.FromSeconds(3));
        check(drive.Items[id].Name == "2BO Fisica PDF - rinominato.ardesia" && pdf.Name == "2BO Fisica PDF - rinominato.pdf" && !sync.HasWork,
            "Rinomina del blocco: Drive rinomina anche il PDF (stesso ID)", pdf.Name);

        // un altro PC ritrova lo stesso PDF dal blocco su Drive e lo aggiorna (non ne crea un altro)
        var folder2 = folder + "-altro-pc";
        var other = NewSync(drive, folder2);
        other.MachineName = "PC-ALTRO";
        try
        {
            await other.OpenAsync(id, null, none);
            File.WriteAllBytes(other.ClassPdfCachePath(id), Pdf("tre"));
            other.ClassPdfChanged(id);
            await other.FlushAsync(TimeSpan.FromSeconds(3));
            check(other.Entry(id)!.ClassPdfId == pdf.Id && Pdfs().Count == pdfsBefore + 1 && pdf.Data.SequenceEqual(Pdf("tre")) && pdf.Shares.Count == 1,
                "Un altro PC ritrova il PDF dal blocco su Drive e aggiorna lo stesso file");
        }
        finally
        {
            other.Dispose();
            try { Directory.Delete(folder2, recursive: true); } catch { /* cartella temporanea */ }
        }
        var listing = await sync.ListAsync(none);
        check(listing.Notebooks.FirstOrDefault(n => n.Id == id)?.ClassPdfId == pdf.Id, "Nell'elenco il blocco sa del suo PDF (per eliminarlo)");

        // condivisione rifiutata dall'amministratore: avviso una volta, il PDF c'è lo stesso, niente tentativi infiniti
        var id2 = await sync.CreateNotebookAsync("2BO", "Fisica", "Senza condivisione", NewNotebook(), none);
        var refused = new List<string>();
        Action<string, string> onRefused = (nb, m) => refused.Add(m);
        DriveSync.ClassPdfShareRefused += onRefused;
        try
        {
            drive.ShareFailures.Enqueue(new DriveException(403, "publishOutNotPermitted", "Condivisione non permessa (finta)"));
            await NewPdf(id2, "uno");
            await NewPdf(id2, "due");
        }
        finally
        {
            DriveSync.ClassPdfShareRefused -= onRefused;
        }
        var e2 = sync.Entry(id2)!;
        var pdf2 = e2.ClassPdfId != null ? drive.Items.GetValueOrDefault(e2.ClassPdfId) : null;
        check(pdf2 != null && pdf2.Data.SequenceEqual(Pdf("due")) && pdf2.Shares.Count == 0 && refused.Count == 1
              && refused[0].Contains("a mano") && e2.ClassPdfShareError != null && !sync.HasWork
              && sync.ClassPdfStatus(id2).ShareError != null,
            "Condivisione rifiutata: avviso chiaro una volta, PDF caricato, si condivide a mano", refused.FirstOrDefault() ?? "nessun avviso");
        sync.RetryShare(id2); // «Riprova a condividere»: ora l'amministratore la permette
        await sync.FlushAsync(TimeSpan.FromSeconds(3));
        check(pdf2!.Shares.SequenceEqual(new[] { "liceofoscarini.it" }) && sync.Entry(id2) is { ClassPdfShared: true, ClassPdfShareError: null },
            "«Riprova a condividere»: ora condiviso col dominio", string.Join(", ", pdf2.Shares));

        // PDF cestinato dal sito di Drive: al prossimo aggiornamento se ne crea uno nuovo, legato al blocco
        pdf2!.Trashed = true;
        await NewPdf(id2, "tre");
        var e2b = sync.Entry(id2)!;
        check(e2b.ClassPdfId != null && e2b.ClassPdfId != pdf2.Id && drive.Items[e2b.ClassPdfId].Data.SequenceEqual(Pdf("tre"))
              && drive.Items[id2].Props.GetValueOrDefault(DriveSync.ClassPdfKey) == e2b.ClassPdfId && !sync.HasWork,
            "PDF cestinato dal sito di Drive: se ne crea uno nuovo", e2b.ClassPdfId ?? "");

        // conflitto: la copia non si porta via il PDF, che resta al blocco originale (con il suo contenuto)
        DriveFork? fork = null;
        Action<DriveFork> onFork = f => fork = f;
        DriveSync.Forked += onFork;
        try
        {
            sync.QuietDelay = TimeSpan.FromHours(1);
            Modify(sync.CachePath(id), "Mia lezione");
            sync.Changed(id, "2BO Fisica PDF - rinominato");
            File.WriteAllBytes(sync.ClassPdfCachePath(id), Pdf("mia"));
            sync.ClassPdfChanged(id);
            drive.ChangeRemotely(id, drive.Items[id].Data, "Lezione dell'altro PC");
            await sync.FlushAsync(TimeSpan.FromSeconds(3));
            await WaitAsync(() => fork != null, 2000);
        }
        finally
        {
            DriveSync.Forked -= onFork;
            sync.QuietDelay = TimeSpan.FromMilliseconds(400);
        }
        var copy = fork != null ? drive.Items.GetValueOrDefault(fork.NewId) : null;
        check(copy != null && !copy.Props.ContainsKey(DriveSync.ClassPdfKey) && copy.Props.GetValueOrDefault(DriveSync.CopyOfKey) == id
              && sync.Entry(fork!.NewId) is { ClassPdfId: null, ConflictCopyOf: var of } && of == id
              && drive.Items[id].Props.GetValueOrDefault(DriveSync.ClassPdfKey) == pdf.Id && pdf.Data.SequenceEqual(Pdf("tre"))
              && !File.Exists(sync.ClassPdfCachePath(id)) && Pdfs().Count(p => !p.Trashed) == pdfsBefore + 2
              && fork.Message.Contains("PDF per la classe"),
            "Conflitto: il PDF resta al blocco originale; la copia non ne ha e il PDF rifatto qui non lo sovrascrive",
            copy != null ? copy.Name : "nessuna copia");
        if (fork != null) await sync.TrashAsync(fork.NewId, none);

        // eliminare il blocco tenendo il PDF, ed eliminando anche il PDF (tutto nel cestino, le cartelle restano)
        var keptPdf = sync.Entry(id2)!.ClassPdfId!;
        await sync.TrashAsync(id2, none);
        check(drive.Items[id2].Trashed && !drive.Items[keptPdf].Trashed && sync.Entry(id2) == null && !File.Exists(sync.ClassPdfCachePath(id2)),
            "Elimina il blocco e «Tieni il PDF»: il PDF resta (col suo link)");
        await sync.TrashAsync(id, none, sync.Entry(id)!.ClassPdfId);
        check(drive.Items[id].Trashed && pdf.Trashed && drive.Items.Values.Where(i => i.Mime == DriveApi.FolderMime).All(f => !f.Trashed),
            "Elimina il blocco «e anche il PDF»: entrambi nel cestino, le cartelle restano");
    }

    static DriveSync NewSync(IDriveClient client, string folder) => new(client, folder, "prova@liceofoscarini.it")
    {
        MachineName = "PC-PROVA",
        NetworkAvailable = () => true,
        QuietDelay = TimeSpan.FromMilliseconds(400),
        MaxDelay = TimeSpan.FromMilliseconds(1500),
        RetryDelays = new[] { TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(300) },
        PumpInterval = TimeSpan.FromMilliseconds(40),
    };

    internal static Notebook NewNotebook(string section = "Sezione 1")
    {
        var nb = new Notebook();
        var s = NotebookNode.NewSection(section, "#1E5A68");
        nb.Insert(s, null);
        nb.Insert(NotebookNode.NewPage(), s);
        return nb;
    }

    /// <summary>Aggiunge una sezione al blocco salvato in <paramref name="path"/> (come se il docente avesse scritto).</summary>
    internal static void Modify(string path, string section)
    {
        var nb = NotebookStore.Load(path);
        var s = NotebookNode.NewSection(section, "#2E7D32");
        nb.Insert(s, null);
        nb.Insert(NotebookNode.NewPage(), s);
        NotebookStore.Save(nb, path);
    }

    /// <summary>Titoli delle sezioni di un blocco salvato in memoria (contenuto di un file su Drive).</summary>
    internal static List<string> SectionsOf(byte[] data)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"ardesia-prova-{Guid.NewGuid():N}{NotebookStore.Extension}");
        try
        {
            File.WriteAllBytes(tmp, data);
            return NotebookStore.Load(tmp).Sections.Select(s => s.Title).ToList();
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* temporaneo */ }
        }
    }

    internal static async Task WaitAsync(Func<bool> condition, int ms)
    {
        var end = DateTime.Now.AddMilliseconds(ms);
        while (!condition() && DateTime.Now < end) await Task.Delay(30);
    }

    /// <summary>Drive finto in memoria, con errori da iniettare.</summary>
    sealed class FakeDrive : IDriveClient
    {
        public sealed class Item
        {
            public string Id = "", Name = "", Mime = "";
            public string? Parent;
            public byte[] Data = Array.Empty<byte>();
            public int Rev = 1;
            public bool Trashed;
            public DateTime Modified = DateTime.Now;
            public Dictionary<string, string> Props = new();

            /// <summary>Domini con cui è condiviso (lettura, per chi ha il link).</summary>
            public List<string> Shares = new();
        }

        public readonly Dictionary<string, Item> Items = new();
        public readonly Queue<Exception> Failures = new();

        /// <summary>Errori della sola condivisione (es. rifiutata dall'amministratore).</summary>
        public readonly Queue<Exception> ShareFailures = new();

        /// <summary>Ordine dei caricamenti: «nuovo {nome}», «contenuto {id}».</summary>
        public readonly List<string> Ops = new();

        public int Uploads, Downloads, UploadDelay, MetadataUpdates;
        int _next;

        void MaybeFail()
        {
            if (Failures.Count > 0) throw Failures.Dequeue();
        }

        static DriveFile Info(Item i) => new(i.Id, i.Name, i.Mime, null, i.Mime == DriveApi.FolderMime ? null : "rev" + i.Rev, i.Rev,
            new[] { i.Parent ?? "root-del-mio-drive" }, i.Modified, i.Trashed, i.Data.Length,
            i.Props.Count > 0 ? new Dictionary<string, string>(i.Props) : null);

        /// <summary>Come Drive: un valore null toglie la chiave.</summary>
        static void Merge(Item it, IDictionary<string, string?>? props)
        {
            if (props == null) return;
            foreach (var (k, v) in props)
                if (v == null) it.Props.Remove(k);
                else it.Props[k] = v;
        }

        Item Add(string name, string mime, string? parent, byte[] data)
        {
            var it = new Item { Id = $"finto{++_next}", Name = name, Mime = mime, Parent = parent, Data = data, Modified = DateTime.Now.AddSeconds(_next) };
            Items[it.Id] = it;
            return it;
        }

        Item Find(string id) => Items.TryGetValue(id, out var it) ? it : throw new DriveException(404, "notFound", "File not found: " + id);

        /// <summary>Un altro PC ha caricato una versione nuova (revisione nuova).</summary>
        public void ChangeRemotely(string id, byte[] notebook, string section)
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"ardesia-finto-{Guid.NewGuid():N}.ardesia");
            File.WriteAllBytes(tmp, notebook);
            Modify(tmp, section);
            var it = Items[id];
            it.Data = File.ReadAllBytes(tmp);
            it.Rev++;
            File.Delete(tmp);
        }

        public Task<DriveFile> CreateFolderAsync(string name, string? parent, CancellationToken ct)
        {
            MaybeFail();
            return Task.FromResult(Info(Add(name, DriveApi.FolderMime, parent, Array.Empty<byte>())));
        }

        public Task<DriveFile> CreateFileAsync(string name, string mimeType, string? parent, byte[] data, CancellationToken ct,
            IDictionary<string, string>? appProperties = null)
        {
            MaybeFail();
            var it = Add(name, mimeType, parent, data);
            if (appProperties != null) Merge(it, appProperties.ToDictionary(p => p.Key, p => (string?)p.Value));
            Ops.Add("nuovo " + name);
            return Task.FromResult(Info(it));
        }

        public Task<DriveFile> RenameAsync(string id, string name, CancellationToken ct)
        {
            MaybeFail();
            var it = Find(id);
            it.Name = name;
            return Task.FromResult(Info(it));
        }

        public Task ShareWithDomainAsync(string id, string domain, CancellationToken ct)
        {
            MaybeFail();
            if (ShareFailures.Count > 0) throw ShareFailures.Dequeue();
            Find(id).Shares.Add(domain);
            return Task.CompletedTask;
        }

        public async Task<DriveFile> UpdateContentAsync(string id, string mimeType, byte[] data, CancellationToken ct,
            IDictionary<string, string?>? appProperties = null, string? newName = null, IProgress<double>? progress = null)
        {
            if (UploadDelay > 0) await Task.Delay(UploadDelay, ct);
            MaybeFail();
            var it = Find(id);
            it.Data = data;
            it.Rev++;
            it.Modified = DateTime.Now;
            if (newName != null) it.Name = newName;
            Merge(it, appProperties);
            Uploads++;
            Ops.Add("contenuto " + id);
            progress?.Report(1);
            return Info(it);
        }

        public Task<DriveFile> UpdateMetadataAsync(string id, IDictionary<string, string?> appProperties, CancellationToken ct)
        {
            MaybeFail();
            var it = Find(id);
            Merge(it, appProperties);
            MetadataUpdates++;
            return Task.FromResult(Info(it));
        }

        public Task<DriveFile> GetAsync(string id, CancellationToken ct)
        {
            MaybeFail();
            return Task.FromResult(Info(Find(id)));
        }

        public Task<byte[]> DownloadAsync(string id, CancellationToken ct, IProgress<double>? progress = null)
        {
            MaybeFail();
            Downloads++;
            progress?.Report(1);
            return Task.FromResult(Find(id).Data);
        }

        public Task<List<DriveFile>> ListAsync(string q, CancellationToken ct)
        {
            MaybeFail();
            var mime = Regex.Match(q, "mimeType='([^']+)'").Groups[1].Value;
            var parent = Regex.Match(q, "'([^']+)' in parents") is { Success: true } m ? m.Groups[1].Value : null;
            var name = Regex.Match(q, "name='([^']+)'") is { Success: true } n ? n.Groups[1].Value : null;
            return Task.FromResult(Items.Values
                .Where(i => i.Mime == mime && !i.Trashed && (name == null || i.Name == name)
                            && (parent == null || (parent == "root" ? i.Parent == null : i.Parent == parent)))
                .OrderByDescending(i => i.Modified).Select(Info).ToList());
        }

        public Task<DriveFile> TrashAsync(string id, CancellationToken ct)
        {
            MaybeFail();
            var it = Find(id);
            it.Trashed = true;
            return Task.FromResult(Info(it));
        }
    }
}
