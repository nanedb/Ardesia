using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Ardesia.Models;

namespace Ardesia.Services.Google;

/// <summary>Stato di un blocco su Drive, per la barra in alto.</summary>
public enum DriveSyncState { Synced, Pending, Uploading, Offline, Retrying, Failed, SignedOut }

/// <summary>Quello che Ardesia ricorda di un blocco su Drive (in sync.json, accanto alla copia in cache).</summary>
public sealed class DriveSyncEntry
{
    /// <summary>Titolo del blocco; su Drive il file si chiama «{titolo}.ardesia».</summary>
    public string Title { get; set; } = "";

    /// <summary>Nome attuale del file su Drive.</summary>
    public string? DriveName { get; set; }

    /// <summary>Cartella da mostrare negli elenchi, es. «2BO / Fisica».</summary>
    public string? Folder { get; set; }

    public string? ParentId { get; set; }

    /// <summary>Revisione di Drive da cui parte la copia in cache.</summary>
    public string? BaseRevision { get; set; }

    /// <summary>La copia in cache ha modifiche non ancora caricate (sopravvive a un arresto improvviso).</summary>
    public bool Pending { get; set; }

    public DateTime? LastUpload { get; set; }
    public string? LastError { get; set; }
    public DateTime? ModifiedTime { get; set; }
}

/// <summary>Una cartella creata da Ardesia su Drive.</summary>
public sealed record DriveFolder(string Id, string Name, string? ParentId, DateTime? ModifiedTime);

/// <summary>Un blocco appunti nell'elenco di Drive.</summary>
public sealed record DriveNotebook(string Id, string Title, string? Class, string? Subject, DateTime? Modified, long? Size)
{
    public string Folder => Class != null && Subject != null ? $"{Class} / {Subject}" : Class ?? "";
}

/// <summary>Elenco dei blocchi e delle cartelle classe/materia (le usate più di recente prima).</summary>
public sealed record DriveListing(List<DriveNotebook> Notebooks, List<string> Classes, Dictionary<string, List<string>> SubjectsByClass,
    List<string> Subjects, bool Offline);

/// <summary>Il blocco è stato eliminato o spostato nel cestino dal sito di Drive (o non è più accessibile).</summary>
public sealed class DriveGoneException(string message, bool hadPendingChanges) : Exception(message)
{
    public bool HadPendingChanges { get; } = hadPendingChanges;
}

/// <summary>Esito dell'apertura: la copia in cache da aprire e un eventuale avviso per la barra.</summary>
public sealed record DriveOpenResult(string CachePath, string? Warning);

/// <summary>
/// Blocchi appunti su Google Drive (docs/google-drive.md §4): la copia in cache è il file di lavoro (si salva come
/// sempre, in un attimo), il caricamento su Drive va in coda e parte ~30 s dopo l'ultima modifica, al massimo ogni
/// ~3 minuti; nuovi tentativi con attese crescenti. Tutto gira sul thread della UI con chiamate asincrone: la LIM non
/// aspetta mai la rete.
/// </summary>
public sealed class DriveSync : IDisposable
{
    public const string MimeType = "application/vnd.ardesia.notebook";
    public const string RootFolderName = "Ardesia";

    static DriveSync? _current;

    /// <summary>Cambia lo stato di un blocco (fileId). Arriva sul thread della UI.</summary>
    public static event Action<string>? StatusChanged;

    /// <summary>
    /// La sincronizzazione dell'account collegato, o null se nessuno è collegato. Va chiamata sul thread della UI.
    /// Cambiando account si ferma quella vecchia (le modifiche in coda restano nel suo sync.json).
    /// </summary>
    public static DriveSync? Current
    {
        get
        {
            var g = GoogleAccountService.Current;
            if (!g.IsSignedIn || g.Email == null)
            {
                _current?.Dispose();
                _current = null;
            }
            else if (_current == null || !string.Equals(_current.Email, g.Email, StringComparison.OrdinalIgnoreCase))
            {
                _current?.Dispose();
                _current = new DriveSync(new DriveApi(g), AccountFolder(g.Email), g.Email);
            }
            return _current;
        }
    }

    /// <summary>%LocalAppData%\Ardesia\Drive: una cartella per account.</summary>
    public static string RootFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ardesia", "Drive");

    /// <summary>Cartella della cache di un account: hash dell'e-mail, così due docenti non si mescolano.</summary>
    public static string AccountFolder(string email) => Path.Combine(RootFolder, AccountKey(email));

    public static string AccountKey(string email) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant())))[..16].ToLowerInvariant();

    /// <summary>Vero se il percorso è la copia in cache di un blocco su Drive: …\Drive\{account}\{fileId}.ardesia.</summary>
    public static bool TryParseCachePath(string? path, out string accountKey, out string fileId)
    {
        accountKey = fileId = "";
        if (string.IsNullOrEmpty(path)) return false;
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }
        var root = RootFolder + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        var parts = full[root.Length..].Split(Path.DirectorySeparatorChar);
        if (parts.Length != 2 || !parts[1].EndsWith(NotebookStore.Extension, StringComparison.OrdinalIgnoreCase)) return false;
        accountKey = parts[0];
        fileId = Path.GetFileNameWithoutExtension(parts[1]);
        return fileId.Length > 0;
    }

    /// <summary>Vero se il percorso è un blocco su Drive dell'account collegato (anche se la copia in cache manca).</summary>
    public static bool IsCurrentAccountPath(string? path) =>
        TryParseCachePath(path, out var key, out _) && Current is { } s && s.AccountKeyValue == key;

    /// <summary>Titolo, cartella ed e-mail di un blocco su Drive, letti dal sync.json del suo account (per recenti e messaggi).</summary>
    public static (string Title, string? Folder, string? Email)? Describe(string? path)
    {
        if (!TryParseCachePath(path, out var key, out var id)) return null;
        var data = _current?.AccountKeyValue == key ? _current._data : LoadData(Path.Combine(RootFolder, key));
        return data.Files.TryGetValue(id, out var e) ? (e.Title, e.Folder, data.Email) : ("Blocco su Google Drive", null, data.Email);
    }

    /// <summary>
    /// Dopo ogni salvataggio della copia in cache: segna il blocco «da caricare». Se il suo account non è quello
    /// collegato (uscito, accesso scaduto) lo scrive solo nel suo sync.json: si caricherà quando si rientra.
    /// </summary>
    public static void NotifySaved(string path, string title)
    {
        if (!TryParseCachePath(path, out var key, out var id)) return;
        if (Current is { } s && s.AccountKeyValue == key)
        {
            s.Changed(id, title);
            return;
        }
        var folder = Path.Combine(RootFolder, key);
        var data = LoadData(folder);
        if (!data.Files.TryGetValue(id, out var e)) data.Files[id] = e = new DriveSyncEntry();
        e.Title = title;
        e.Pending = true;
        SaveData(folder, data);
    }

    /// <summary>Nome valido per file e cartelle: i caratteri non ammessi diventano «-» (scelta dell'utente).</summary>
    public static string SafeName(string s)
    {
        var clean = new string(s.Select(ch => "\\/:*?\"<>|".Contains(ch) || char.IsControl(ch) ? '-' : ch).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "Blocco appunti" : clean;
    }

    /// <summary>Anno scolastico, che cambia a settembre: settembre 2026 – agosto 2027 = «26-27».</summary>
    public static string SchoolYear(DateTime d)
    {
        int first = d.Month >= 9 ? d.Year : d.Year - 1;
        return $"{first % 100:00}-{(first + 1) % 100:00}";
    }

    // ═══════════════════════════ Istanza (un account) ═══════════════════════════

    sealed class SyncData
    {
        public string Email { get; set; } = "";
        public string? RootFolderId { get; set; }
        public Dictionary<string, DriveSyncEntry> Files { get; set; } = new();

        /// <summary>Ultimo elenco delle cartelle letto: classi e materie anche senza rete.</summary>
        public List<DriveFolder> Folders { get; set; } = new();
    }

    /// <summary>Stato in memoria della coda per un file.</summary>
    sealed class Runtime
    {
        public DateTime First = DateTime.MinValue, Last = DateTime.MinValue, NextTry = DateTime.MinValue;
        public int Serial, Failures;
        public bool Uploading, Blocked;
        public DriveSyncState? State;
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    readonly IDriveClient _client;
    readonly string _folder;
    readonly SyncData _data;
    readonly Dictionary<string, Runtime> _rt = new();
    readonly DispatcherTimer _timer;
    readonly CancellationTokenSource _cts = new();
    bool _busy, _disposed;

    public DriveSync(IDriveClient client, string folder, string email)
    {
        _client = client;
        _folder = folder;
        Directory.CreateDirectory(folder);
        _data = LoadData(folder);
        if (!string.Equals(_data.Email, email, StringComparison.OrdinalIgnoreCase))
        {
            _data.Email = email;
            SaveData(folder, _data);
        }
        Email = email;
        AccountKeyValue = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        // Modifiche rimaste in coda da una sessione precedente (anche dopo un arresto improvviso): si caricano presto.
        foreach (var (id, e) in _data.Files)
            if (e.Pending)
                _rt[id] = new Runtime { First = DateTime.Now - MaxDelay, Last = DateTime.Now - QuietDelay };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Pump();
        _timer.Start();
    }

    public string Email { get; }

    /// <summary>Nome della cartella dell'account (hash dell'e-mail).</summary>
    public string AccountKeyValue { get; }

    /// <summary>Caricamento dopo questo tempo senza modifiche…</summary>
    public TimeSpan QuietDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>…e comunque al massimo ogni tanto mentre si scrive.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Attese tra un tentativo e l'altro (l'ultima si ripete).</summary>
    public TimeSpan[] RetryDelays { get; set; } =
        { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5) };

    /// <summary>Ogni quanto si guarda la coda (le prove la accorciano).</summary>
    public TimeSpan PumpInterval
    {
        get => _timer.Interval;
        set => _timer.Interval = value;
    }

    public string CachePath(string id) => Path.Combine(_folder, id + NotebookStore.Extension);

    public DriveSyncEntry? Entry(string id) => _data.Files.GetValueOrDefault(id);

    public string? RootFolderId => _data.RootFolderId;

    /// <summary>Vero se ci sono modifiche da caricare (anche ferme per un errore).</summary>
    public bool HasPending => _data.Files.Values.Any(e => e.Pending);

    /// <summary>Stato del blocco e testo per la barra in alto.</summary>
    public (DriveSyncState State, string Text, string Tooltip) Status(string id)
    {
        var e = Entry(id);
        var rt = _rt.GetValueOrDefault(id);
        var state = rt?.State ?? (e?.Pending == true ? DriveSyncState.Pending : DriveSyncState.Synced);
        if (state == DriveSyncState.Synced && e?.Pending == true) state = DriveSyncState.Pending;
        var when = e?.LastUpload is { } t ? $" alle {t:HH:mm}" : "";
        return state switch
        {
            DriveSyncState.Uploading => (state, "Caricamento su Drive…", "Ardesia sta caricando le ultime modifiche su Google Drive."),
            DriveSyncState.Pending => (state, "Salvato su questo PC · da caricare su Drive",
                "Le modifiche vanno su Google Drive dopo una pausa nella scrittura (o al massimo ogni 3 minuti)."),
            DriveSyncState.Offline => (state, "Senza connessione · modifiche salvate su questo PC, da caricare",
                "Ardesia riprova da sola a caricarle su Google Drive."),
            DriveSyncState.Retrying => (state, "Google Drive non risponde · riprovo tra poco", e?.LastError ?? ""),
            DriveSyncState.Failed => (state, "Non caricato su Drive", e?.LastError ?? ""),
            DriveSyncState.SignedOut => (state, "Accedi a Google per caricare le modifiche",
                "Le modifiche sono salvate su questo PC e verranno caricate quando accedi di nuovo con questo account."),
            _ => (state, "Su Drive · salvato" + when, $"Su Google Drive{(e?.Folder != null ? $", in Ardesia / {e.Folder}" : "")}."),
        };
    }

    // ───────────── coda di caricamento ─────────────

    /// <summary>La copia in cache è stata salvata: si caricherà dopo una pausa (o al massimo ogni ~3 minuti).</summary>
    public void Changed(string id, string title)
    {
        if (_disposed) return;
        if (!_data.Files.TryGetValue(id, out var e)) _data.Files[id] = e = new DriveSyncEntry();
        e.Title = title;
        var rt = RuntimeOf(id);
        var now = DateTime.Now;
        if (!e.Pending || rt.First == DateTime.MinValue) rt.First = now;
        rt.Last = now;
        rt.Serial++;
        if (!e.Pending)
        {
            e.Pending = true;
            SaveData(_folder, _data);
        }
        if (rt.State is null or DriveSyncState.Synced) SetState(id, DriveSyncState.Pending);
    }

    /// <summary>Carica appena possibile (cambio di blocco, uscita).</summary>
    public void UploadSoon(string id)
    {
        if (!_data.Files.TryGetValue(id, out var e) || !e.Pending) return;
        var rt = RuntimeOf(id);
        rt.First = rt.Last = DateTime.MinValue;
        rt.NextTry = DateTime.MinValue;
        rt.Blocked = false;
        Pump();
    }

    /// <summary>Carica subito tutto quello che è in coda e aspetta (al massimo <paramref name="timeout"/>). Vero se non resta niente.</summary>
    public async Task<bool> FlushAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        foreach (var id in _data.Files.Where(f => f.Value.Pending).Select(f => f.Key).ToList()) UploadSoon(id);
        var end = DateTime.Now + timeout;
        while (DateTime.Now < end && !ct.IsCancellationRequested)
        {
            var pending = _data.Files.Where(f => f.Value.Pending).Select(f => f.Key).ToList();
            if (pending.Count == 0) return true;
            // con un errore definitivo, o senza accesso, inutile aspettare
            if (!_busy && pending.All(id => _rt.GetValueOrDefault(id) is { } rt
                    && (rt.Blocked || rt.State == DriveSyncState.SignedOut || rt.Failures >= 2))) return false;
            try { await Task.Delay(150, ct); } catch (OperationCanceledException) { break; }
        }
        return !HasPending;
    }

    Runtime RuntimeOf(string id) => _rt.TryGetValue(id, out var rt) ? rt : _rt[id] = new Runtime();

    void SetState(string id, DriveSyncState state)
    {
        RuntimeOf(id).State = state;
        StatusChanged?.Invoke(id);
    }

    void Pump()
    {
        if (_busy || _disposed) return;
        var now = DateTime.Now;
        string? next = null;
        var nextDue = DateTime.MaxValue;
        foreach (var (id, e) in _data.Files)
        {
            if (!e.Pending) continue;
            var rt = RuntimeOf(id);
            if (rt.Blocked || rt.Uploading) continue;
            var due = rt.First == DateTime.MinValue ? DateTime.MinValue : Min(rt.Last + QuietDelay, rt.First + MaxDelay);
            if (rt.NextTry > due) due = rt.NextTry;
            if (due <= now && due < nextDue)
            {
                next = id;
                nextDue = due;
            }
        }
        if (next != null) _ = UploadAsync(next);
    }

    static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    async Task UploadAsync(string id)
    {
        var e = _data.Files[id];
        var rt = RuntimeOf(id);
        var path = CachePath(id);
        if (!File.Exists(path))
        {
            e.Pending = false; // niente da caricare: la copia è stata tolta
            SaveData(_folder, _data);
            SetState(id, DriveSyncState.Synced);
            return;
        }
        _busy = rt.Uploading = true;
        SetState(id, DriveSyncState.Uploading);
        try
        {
            // L'istantanea si prende qui, sul thread della UI, tra un salvataggio e l'altro: il prossimo salvataggio
            // può sostituire il file mentre il caricamento è in corso.
            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (IOException)
            {
                rt.NextTry = DateTime.Now.AddSeconds(2);
                SetState(id, DriveSyncState.Pending);
                return;
            }
            int serial = rt.Serial;
            var wanted = SafeName(e.Title) + NotebookStore.Extension;
            var f = await _client.UpdateContentAsync(id, MimeType, data, _cts.Token,
                newName: e.Title.Length > 0 && wanted != e.DriveName ? wanted : null);
            if (_disposed) return;
            e.BaseRevision = f.HeadRevisionId;
            e.DriveName = f.Name;
            e.ModifiedTime = f.ModifiedTime ?? DateTime.Now;
            e.LastUpload = DateTime.Now;
            e.LastError = null;
            if (rt.Serial == serial) e.Pending = false; // altrimenti si è salvato di nuovo nel frattempo: si ricarica
            rt.Failures = 0;
            SaveData(_folder, _data);
            SetState(id, e.Pending ? DriveSyncState.Pending : DriveSyncState.Synced);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // sync fermata (uscita, cambio di account): la coda resta in sync.json
        }
        catch (GoogleAuthException ex)
        {
            e.LastError = ex.Message;
            rt.NextTry = DateTime.Now.AddMinutes(5);
            SetState(id, DriveSyncState.SignedOut);
        }
        catch (Exception ex) when (DriveApi.IsTransient(ex))
        {
            rt.Failures++;
            rt.NextTry = DateTime.Now + RetryDelays[Math.Min(rt.Failures, RetryDelays.Length) - 1];
            e.LastError = ex.Message;
            SetState(id, ex is DriveException ? DriveSyncState.Retrying : DriveSyncState.Offline);
        }
        catch (DriveException ex) when (ex.Status is 403 or 404)
        {
            // cancellato dal sito di Drive o non più accessibile: il lavoro resta in cache, niente più tentativi
            rt.Blocked = true;
            e.LastError = "Il blocco appunti non è più accessibile su Google Drive (eliminato o spostato nel cestino?). " +
                          "Le modifiche restano su questo PC: puoi salvarne una copia dal menu File.";
            SaveData(_folder, _data);
            SetState(id, DriveSyncState.Failed);
        }
        catch (Exception ex)
        {
            rt.Failures++;
            rt.NextTry = DateTime.Now + RetryDelays[^1];
            e.LastError = ex.Message;
            SetState(id, DriveSyncState.Retrying);
        }
        finally
        {
            _busy = rt.Uploading = false;
        }
        if (!_disposed) Pump(); // un altro file in coda
    }

    // ───────────── cartelle ─────────────

    /// <summary>
    /// La cartella «Ardesia/{classe}/{materia}», creata se manca. Le cartelle esistenti si riusano senza distinguere
    /// maiuscole e minuscole. Restituisce l'ID della materia e i nomi veri di classe e materia.
    /// </summary>
    public async Task<(string Id, string Class, string Subject)> EnsureFolderAsync(string className, string subject, CancellationToken ct)
    {
        var root = await EnsureRootAsync(ct);
        var cls = await ChildFolderAsync(root, SafeName(className), ct);
        var subj = await ChildFolderAsync(cls.Id, SafeName(subject), ct);
        SaveData(_folder, _data);
        return (subj.Id, cls.Name, subj.Name);
    }

    async Task<string> EnsureRootAsync(CancellationToken ct)
    {
        if (_data.RootFolderId is { } known)
        {
            try
            {
                var f = await _client.GetAsync(known, ct);
                if (!f.Trashed) return known;
            }
            catch (DriveException ex) when (ex.Status == 404)
            {
                // cancellata: si cerca o si ricrea
            }
        }
        var found = (await _client.ListAsync(
                $"mimeType='{DriveApi.FolderMime}' and name='{RootFolderName}' and 'root' in parents and trashed=false", ct))
            .OrderBy(f => f.ModifiedTime ?? DateTime.MaxValue).FirstOrDefault()
            ?? await _client.CreateFolderAsync(RootFolderName, null, ct);
        _data.RootFolderId = found.Id;
        RememberFolder(found);
        SaveData(_folder, _data);
        return found.Id;
    }

    async Task<DriveFolder> ChildFolderAsync(string parent, string name, CancellationToken ct)
    {
        var children = await _client.ListAsync($"mimeType='{DriveApi.FolderMime}' and '{parent}' in parents and trashed=false", ct);
        var f = children.FirstOrDefault(c => string.Equals(c.Name.Trim(), name, StringComparison.CurrentCultureIgnoreCase))
                ?? await _client.CreateFolderAsync(name, parent, ct);
        return RememberFolder(f);
    }

    DriveFolder RememberFolder(DriveFile f)
    {
        var folder = new DriveFolder(f.Id, f.Name, f.Parents?.FirstOrDefault(), f.ModifiedTime);
        _data.Folders.RemoveAll(x => x.Id == f.Id);
        _data.Folders.Add(folder);
        return folder;
    }

    /// <summary>«2BO / Fisica» per una cartella materia dentro «Ardesia», o il percorso che si riesce a ricostruire.</summary>
    (string? Class, string? Subject) Locate(string? parentId)
    {
        var byId = _data.Folders.ToDictionary(f => f.Id);
        if (parentId == null || !byId.TryGetValue(parentId, out var subj)) return (null, null);
        if (subj.ParentId != null && byId.TryGetValue(subj.ParentId, out var cls))
            return cls.ParentId == _data.RootFolderId ? (cls.Name, subj.Name) : ($"{cls.Name} / {subj.Name}", null);
        return subj.Id == _data.RootFolderId ? (null, null) : (subj.Name, null);
    }

    // ───────────── elenco ─────────────

    /// <summary>
    /// I blocchi creati da Ardesia su Drive (con `drive.file` si vedono solo quelli) e le classi e materie già usate,
    /// lette dalle cartelle: valgono su qualunque PC. Senza rete: l'ultimo elenco noto.
    /// </summary>
    public async Task<DriveListing> ListAsync(CancellationToken ct)
    {
        List<DriveFile> folders, files;
        try
        {
            folders = await _client.ListAsync($"mimeType='{DriveApi.FolderMime}' and trashed=false", ct);
            files = await _client.ListAsync($"mimeType='{MimeType}' and trashed=false", ct);
        }
        catch (Exception ex) when (DriveApi.IsTransient(ex) && !ct.IsCancellationRequested)
        {
            return BuildListing(_data.Files.Select(kv => new DriveFile(kv.Key, kv.Value.DriveName ?? kv.Value.Title, MimeType, null,
                kv.Value.BaseRevision, null, kv.Value.ParentId != null ? new[] { kv.Value.ParentId } : null, kv.Value.ModifiedTime)).ToList(), true);
        }

        _data.Folders = folders.Select(f => new DriveFolder(f.Id, f.Name, f.Parents?.FirstOrDefault(), f.ModifiedTime)).ToList();
        var ids = folders.Select(f => f.Id).ToHashSet();
        if (_data.RootFolderId == null || !ids.Contains(_data.RootFolderId))
        {
            // la cartella «Ardesia» è quella che non sta dentro un'altra cartella di Ardesia
            _data.RootFolderId = folders.Where(f => f.Name == RootFolderName && f.Parents?.Any(ids.Contains) != true)
                .OrderBy(f => f.ModifiedTime ?? DateTime.MaxValue).FirstOrDefault()?.Id;
        }
        foreach (var f in files)
        {
            if (!_data.Files.TryGetValue(f.Id, out var e)) continue;
            e.DriveName = f.Name;
            e.ParentId = f.Parents?.FirstOrDefault();
            e.ModifiedTime = f.ModifiedTime;
            if (!e.Pending) e.Title = TitleOf(f.Name);
            var (c, s) = Locate(e.ParentId);
            e.Folder = c != null && s != null ? $"{c} / {s}" : c;
        }
        SaveData(_folder, _data);
        return BuildListing(files, false);
    }

    DriveListing BuildListing(List<DriveFile> files, bool offline)
    {
        var notebooks = files.Select(f =>
        {
            var (c, s) = Locate(f.Parents?.FirstOrDefault());
            var pending = _data.Files.GetValueOrDefault(f.Id);
            return new DriveNotebook(f.Id, pending?.Pending == true ? pending.Title : TitleOf(f.Name), c, s, f.ModifiedTime, f.Size);
        }).OrderByDescending(n => n.Modified ?? DateTime.MinValue).ToList();

        // Classi = figlie di «Ardesia», materie = figlie di una classe; prima le usate più di recente.
        var root = _data.RootFolderId;
        var classes = _data.Folders.Where(f => root != null && f.ParentId == root).ToList();
        DateTime Used(DriveFolder f, Func<DriveNotebook, bool> inside) =>
            notebooks.Where(inside).Select(n => n.Modified ?? DateTime.MinValue).Append(f.ModifiedTime ?? DateTime.MinValue).Max();
        var classOrder = classes.OrderByDescending(c => Used(c, n => string.Equals(n.Class, c.Name, StringComparison.CurrentCultureIgnoreCase)))
            .Select(c => c.Name).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        var byClass = new Dictionary<string, List<string>>(StringComparer.CurrentCultureIgnoreCase);
        var allSubjects = new List<(string Name, DateTime Used)>();
        foreach (var c in classes)
        {
            var subjects = _data.Folders.Where(f => f.ParentId == c.Id)
                .Select(s => (s.Name, Used: Used(s, n => string.Equals(n.Class, c.Name, StringComparison.CurrentCultureIgnoreCase)
                                                        && string.Equals(n.Subject, s.Name, StringComparison.CurrentCultureIgnoreCase))))
                .ToList();
            allSubjects.AddRange(subjects);
            if (!byClass.TryGetValue(c.Name, out var list)) byClass[c.Name] = list = new List<string>();
            list.AddRange(subjects.OrderByDescending(s => s.Used).Select(s => s.Name));
        }
        var subjectOrder = allSubjects.GroupBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(g => g.Max(s => s.Used)).Select(g => g.First().Name).ToList();
        return new DriveListing(notebooks, classOrder, byClass, subjectOrder, offline);
    }

    static string TitleOf(string driveName) =>
        driveName.EndsWith(NotebookStore.Extension, StringComparison.OrdinalIgnoreCase) ? driveName[..^NotebookStore.Extension.Length] : driveName;

    // ───────────── creazione, apertura, eliminazione ─────────────

    /// <summary>Nuovo blocco in «Ardesia/{classe}/{materia}/{nome}.ardesia»; la copia in cache è pronta da aprire.</summary>
    public async Task<string> CreateNotebookAsync(string className, string subject, string title, Notebook nb, CancellationToken ct)
    {
        var (parent, cls, subj) = await EnsureFolderAsync(className, subject, ct);
        var tmp = Path.Combine(_folder, $"~nuovo-{Guid.NewGuid():N}{NotebookStore.Extension}");
        try
        {
            nb.Title = title;
            NotebookStore.Save(nb, tmp);
            var data = await File.ReadAllBytesAsync(tmp, ct);
            var f = await _client.CreateFileAsync(SafeName(title) + NotebookStore.Extension, MimeType, parent, data, ct,
                new Dictionary<string, string> { ["ardesiaFormato"] = nb.FormatVersion.ToString() });
            File.Move(tmp, CachePath(f.Id), overwrite: true);
            _data.Files[f.Id] = new DriveSyncEntry
            {
                Title = title,
                DriveName = f.Name,
                ParentId = parent,
                Folder = $"{cls} / {subj}",
                BaseRevision = f.HeadRevisionId,
                ModifiedTime = f.ModifiedTime ?? DateTime.Now,
                LastUpload = DateTime.Now,
            };
            SaveData(_folder, _data);
            return f.Id;
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* già spostato */ }
        }
    }

    /// <summary>
    /// Apertura (§4.3): se la versione su Drive è quella della cache si apre subito la cache; se è cambiata (modificata
    /// su un altro PC) si scarica. Senza rete si apre la cache, con un avviso; se manca, non si può aprire.
    /// </summary>
    public async Task<DriveOpenResult> OpenAsync(string id, IProgress<double>? progress, CancellationToken ct)
    {
        var cache = CachePath(id);
        var e = Entry(id);
        bool hasCache = File.Exists(cache);
        DriveFile meta;
        try
        {
            meta = await _client.GetAsync(id, ct);
        }
        catch (DriveException ex) when (ex.Status == 404)
        {
            throw Gone(e, hasCache);
        }
        catch (Exception ex) when (DriveApi.IsTransient(ex) && !ct.IsCancellationRequested)
        {
            if (hasCache) return new DriveOpenResult(cache, "Senza connessione: potrebbe non essere l'ultima versione");
            throw new IOException($"Senza connessione a Google Drive non posso aprire «{e?.Title ?? "il blocco appunti"}»: " +
                                  "su questo PC non ce n'è ancora una copia.", ex);
        }
        if (meta.Trashed) throw Gone(e, hasCache);

        if (e == null) _data.Files[id] = e = new DriveSyncEntry();
        e.DriveName = meta.Name;
        e.ParentId = meta.Parents?.FirstOrDefault();
        e.ModifiedTime = meta.ModifiedTime;
        if (!e.Pending) e.Title = TitleOf(meta.Name);
        var (c, s) = Locate(e.ParentId);
        if (c != null) e.Folder = s != null ? $"{c} / {s}" : c;

        string? warning = null;
        if (hasCache && e.BaseRevision != null && e.BaseRevision == meta.HeadRevisionId)
        {
            // la cache è già l'ultima versione
        }
        else if (hasCache && e.Pending)
        {
            // Cambiata altrove e anche qui (conflitto, fase 3): si tiene questa; Drive conserva le revisioni precedenti.
            warning = "Il blocco era stato modificato anche su un altro PC: si tiene la versione di questo PC";
        }
        else
        {
            var data = await _client.DownloadAsync(id, ct, progress);
            var tmp = cache + ".download";
            await File.WriteAllBytesAsync(tmp, data, ct);
            File.Move(tmp, cache, overwrite: true);
            e.BaseRevision = meta.HeadRevisionId;
            e.Pending = false;
            e.LastUpload ??= meta.ModifiedTime;
        }
        SaveData(_folder, _data);
        return new DriveOpenResult(cache, warning);
    }

    static DriveGoneException Gone(DriveSyncEntry? e, bool hasCache) => new(
        $"«{e?.Title ?? "Il blocco appunti"}» non c'è più su Google Drive: è stato eliminato o spostato nel cestino.",
        hasCache && e?.Pending == true);

    /// <summary>Nel cestino di Drive (le cartelle no: §2.2), poi via dalla cache. Senza rete non si elimina.</summary>
    public async Task TrashAsync(string id, CancellationToken ct)
    {
        RuntimeOf(id).Blocked = true; // niente caricamenti nel frattempo
        try
        {
            await _client.TrashAsync(id, ct);
        }
        catch (DriveException ex) when (ex.Status == 404)
        {
            // già sparito
        }
        catch
        {
            RuntimeOf(id).Blocked = false;
            throw;
        }
        Forget(id);
    }

    /// <summary>Dimentica un blocco (eliminato da Drive): via da sync.json e dalla cache.</summary>
    public void Forget(string id)
    {
        _data.Files.Remove(id);
        _rt.Remove(id);
        try { File.Delete(CachePath(id)); } catch { /* resta: nessuno la usa più */ }
        SaveData(_folder, _data);
    }

    /// <summary>
    /// «Svuota la cache di Drive su questo PC»: toglie le copie senza modifiche da caricare (e non quella aperta).
    /// Si riscaricano alla prossima apertura. Restituisce i byte liberati.
    /// </summary>
    public long ClearCache(string? keepId)
    {
        long freed = 0;
        foreach (var file in Directory.EnumerateFiles(_folder, "*" + NotebookStore.Extension))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (id == keepId || Entry(id)?.Pending == true) continue;
            try
            {
                var len = new FileInfo(file).Length;
                File.Delete(file);
                freed += len;
            }
            catch
            {
                // in uso: si lascia
            }
        }
        return freed;
    }

    /// <summary>Byte occupati dalla cache di questo account.</summary>
    public long CacheSize() =>
        Directory.EnumerateFiles(_folder, "*" + NotebookStore.Extension).Sum(f => new FileInfo(f).Length);

    /// <summary>Dopo «Esci»: cancella la cartella dell'account se non ci sono modifiche da caricare.</summary>
    public static void DeleteAccountCache(string email)
    {
        var folder = AccountFolder(email);
        if (!Directory.Exists(folder) || LoadData(folder).Files.Values.Any(e => e.Pending)) return;
        try { Directory.Delete(folder, recursive: true); } catch { /* file in uso: resta */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _cts.Cancel();
        if (_current == this) _current = null;
    }

    // ───────────── sync.json ─────────────

    static SyncData LoadData(string folder)
    {
        try
        {
            var file = Path.Combine(folder, "sync.json");
            if (File.Exists(file)) return JsonSerializer.Deserialize<SyncData>(File.ReadAllText(file), Json) ?? new SyncData();
        }
        catch
        {
            // illeggibile: si riparte (le copie in cache restano e si riscaricano se serve)
        }
        return new SyncData();
    }

    /// <summary>Scrittura atomica: file temporaneo e sostituzione, come i blocchi appunti.</summary>
    static void SaveData(string folder, SyncData data)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "sync.json");
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, Json));
            if (File.Exists(file)) File.Replace(tmp, file, null);
            else File.Move(tmp, file);
        }
        catch
        {
            // non bloccare la LIM per sync.json: si riscrive al prossimo cambiamento
        }
    }
}
