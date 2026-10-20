using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using Ardesia.Models;

namespace Ardesia.Services.Google;

/// <summary>
/// Prove di DriveSync con un Drive finto in memoria (prima parte di --prova-drive): cartelle classe/materia,
/// creazione, coda di caricamento (pausa, massimo, tentativi, rinomina), sync.json dopo un riavvio, apertura con e
/// senza download, senza rete, blocco eliminato. Non usa la rete né l'account.
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
            Modify(sync.CachePath(id), "Qui");
            sync.Changed(id, "Fisica 2BO / secondo quadrimestre");
            drive.ChangeRemotely(id, File.ReadAllBytes(sync.CachePath(id)), "Là");
            r = await sync.OpenAsync(id, null, CancellationToken.None);
            Check(r.Warning != null && drive.Downloads == downloads + 1, "Modificato qui e altrove: si tiene questo (conflitti: fase 3)", r.Warning ?? "");
            await sync.FlushAsync(TimeSpan.FromSeconds(3));

            drive.Failures.Enqueue(new HttpRequestException("rete assente (finta)"));
            r = await sync.OpenAsync(id, null, CancellationToken.None);
            Check(r.Warning?.StartsWith("Senza connessione") == true, "Senza rete si apre la copia in cache, con un avviso", r.Warning ?? "");
            var other = listing.Notebooks.First(n => n.Id != id).Id;
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

    static DriveSync NewSync(IDriveClient client, string folder) => new(client, folder, "prova@liceofoscarini.it")
    {
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
        }

        public readonly Dictionary<string, Item> Items = new();
        public readonly Queue<Exception> Failures = new();
        public int Uploads, Downloads, UploadDelay;
        int _next;

        void MaybeFail()
        {
            if (Failures.Count > 0) throw Failures.Dequeue();
        }

        static DriveFile Info(Item i) => new(i.Id, i.Name, i.Mime, null, i.Mime == DriveApi.FolderMime ? null : "rev" + i.Rev, i.Rev,
            new[] { i.Parent ?? "root-del-mio-drive" }, i.Modified, i.Trashed, i.Data.Length);

        Item Add(string name, string mime, string? parent, byte[] data)
        {
            var it = new Item { Id = $"finto{++_next}", Name = name, Mime = mime, Parent = parent, Data = data, Modified = DateTime.Now.AddSeconds(_next) };
            Items[it.Id] = it;
            return it;
        }

        Item Find(string id) => Items.TryGetValue(id, out var it) ? it : throw new DriveException(404, "notFound", "File not found: " + id);

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
            return Task.FromResult(Info(Add(name, mimeType, parent, data)));
        }

        public async Task<DriveFile> UpdateContentAsync(string id, string mimeType, byte[] data, CancellationToken ct,
            IDictionary<string, string>? appProperties = null, string? newName = null)
        {
            if (UploadDelay > 0) await Task.Delay(UploadDelay, ct);
            MaybeFail();
            var it = Find(id);
            it.Data = data;
            it.Rev++;
            it.Modified = DateTime.Now;
            if (newName != null) it.Name = newName;
            Uploads++;
            return Info(it);
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
