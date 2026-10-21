using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Ardesia.Models;

namespace Ardesia.Services.Google;

/// <summary>Stato di un blocco su Drive, per la barra in alto.</summary>
public enum DriveSyncState { Synced, Pending, Uploading, Offline, Retrying, Failed, SignedOut, Conflict }

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

    // ── PDF per la classe su Drive (§6): sempre lo stesso file, quindi il link per gli alunni non cambia mai ──

    /// <summary>ID del PDF per la classe su Drive, o null se non c'è ancora.</summary>
    public string? ClassPdfId { get; set; }

    /// <summary>Nome attuale del PDF su Drive (segue il titolo del blocco).</summary>
    public string? ClassPdfName { get; set; }

    /// <summary>Il blocco su Drive sa del suo PDF (appProperty «pdfClasse»): così lo ritrovano anche gli altri PC.</summary>
    public bool ClassPdfLinked { get; set; }

    /// <summary>Condiviso con il dominio della scuola (chi ha il link lo legge).</summary>
    public bool ClassPdfShared { get; set; }

    /// <summary>Condivisione automatica rifiutata da Google (impostazioni dell'amministratore): si condivide a mano.</summary>
    public string? ClassPdfShareError { get; set; }

    /// <summary>Il PDF rifatto su questo PC ({fileId}.pdf nella cache) è da caricare.</summary>
    public bool ClassPdfPending { get; set; }

    public DateTime? ClassPdfUpload { get; set; }
    public string? ClassPdfError { get; set; }

    /// <summary>Copia nata da un conflitto (§4.4): ID del blocco originale, a cui resta legato il PDF per la classe.</summary>
    public string? ConflictCopyOf { get; set; }
}

/// <summary>Stato del PDF per la classe di un blocco su Drive, per le impostazioni del blocco.</summary>
public sealed record DriveClassPdfInfo(string? Id, string? Link, bool Pending, bool Uploading, DateTime? Uploaded, string? Error,
    string? ShareError);

/// <summary>Una cartella creata da Ardesia su Drive.</summary>
public sealed record DriveFolder(string Id, string Name, string? ParentId, DateTime? ModifiedTime);

/// <summary>Un blocco appunti nell'elenco di Drive.</summary>
public sealed record DriveNotebook(string Id, string Title, string? Class, string? Subject, DateTime? Modified, long? Size,
    string? OpenOn = null, string? ClassPdfId = null)
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

/// <summary>
/// Esito dell'apertura: la copia in cache da aprire, un eventuale avviso per la barra e, se c'era un conflitto, la
/// spiegazione (allora la copia da aprire è quella nuova, «… (modifiche da PC, …)»).
/// </summary>
public sealed record DriveOpenResult(string CachePath, string? Warning, string? Conflict = null);

/// <summary>«Lucchetto morbido» di un altro PC (§4.4): il blocco risulta aperto là.</summary>
public sealed record DriveLock(string Pc, DateTime At);

/// <summary>
/// Conflitto risolto con «tieni entrambi» (§4.4): la versione dell'altro PC resta in <see cref="OldId"/>, le
/// modifiche di questo PC sono nel file nuovo <see cref="NewId"/>, su cui si continua a lavorare.
/// </summary>
public sealed record DriveFork(string OldId, string NewId, string OldTitle, string NewTitle, string Message);

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

    /// <summary>Chiavi delle appProperties del «lucchetto morbido» (§4.4).</summary>
    public const string LockPcKey = "apertoSu", LockTimeKey = "apertoAlle";

    /// <summary>
    /// appProperties del PDF per la classe (§6): sul blocco l'ID del suo PDF, sul PDF l'ID del blocco; sulla copia di un
    /// conflitto l'ID del blocco originale (il PDF resta a quello).
    /// </summary>
    public const string ClassPdfKey = "pdfClasse", ClassPdfOfKey = "pdfDi", CopyOfKey = "copiaDi";

    public const string PdfMime = "application/pdf";

    /// <summary>
    /// Google non ha permesso di condividere il PDF per la classe col dominio (fileId del blocco, messaggio per il
    /// docente). Arriva una volta sola, sul thread della UI.
    /// </summary>
    public static event Action<string, string>? ClassPdfShareRefused;

    /// <summary>Cambia lo stato di un blocco (fileId). Arriva sul thread della UI.</summary>
    public static event Action<string>? StatusChanged;

    /// <summary>
    /// Un caricamento ha trovato su Drive una versione più nuova di un altro PC: le nostre modifiche sono andate in un
    /// file nuovo, e la copia in cache è passata al nuovo ID. Arriva sul thread della UI.
    /// </summary>
    public static event Action<DriveFork>? Forked;

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

    /// <summary>
    /// %LocalAppData%\Ardesia\Drive: una cartella per account. Per le prove dal vivo con un «secondo PC» sullo stesso
    /// computer si può cambiare con la variabile d'ambiente ARDESIA_PROVA_CACHE.
    /// </summary>
    public static string RootFolder => Environment.GetEnvironmentVariable("ARDESIA_PROVA_CACHE") is { Length: > 0 } test
        ? test
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ardesia", "Drive");

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

    /// <summary>
    /// Il PDF per la classe è stato rifatto accanto alla copia in cache ({fileId}.pdf): va su Drive nella stessa coda,
    /// dopo il blocco. Come <see cref="NotifySaved"/>, senza l'account collegato lo scrive solo nel suo sync.json.
    /// </summary>
    public static void NotifyClassPdf(string path)
    {
        if (!TryParseCachePath(path, out var key, out var id)) return;
        if (Current is { } s && s.AccountKeyValue == key)
        {
            s.ClassPdfChanged(id);
            return;
        }
        var folder = Path.Combine(RootFolder, key);
        var data = LoadData(folder);
        if (!data.Files.TryGetValue(id, out var e)) data.Files[id] = e = new DriveSyncEntry();
        e.ClassPdfPending = true;
        SaveData(folder, data);
    }

    /// <summary>Il PDF per la classe in cache, accanto al blocco: «…\{fileId}.pdf».</summary>
    public static string ClassPdfFileFor(string cachePath) => Path.ChangeExtension(cachePath, ".pdf");

    /// <summary>Link per gli alunni (non cambia mai: il PDF si aggiorna sempre nello stesso file).</summary>
    public static string ClassPdfLinkOf(string pdfId) => $"https://drive.google.com/file/d/{pdfId}/view";

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

        /// <summary>Lucchetto: scritto da noi su Drive, quando, prossimo tentativo dopo un errore.</summary>
        public bool LockHeld;
        public DateTime LockWritten = DateTime.MinValue, LockNextTry = DateTime.MinValue;
        public int LockFailures;

        /// <summary>Spiegazione del conflitto (copia nuova), finché il blocco resta aperto.</summary>
        public string? ConflictMessage;

        /// <summary>PDF per la classe: tentativi, prossimo tentativo, errore definitivo, caricamento in corso.</summary>
        public int PdfFailures;
        public DateTime PdfNextTry = DateTime.MinValue;
        public bool PdfBlocked, PdfUploading;
    }

    /// <summary>Avanzamento che passa subito il valore (senza passare dal contesto di sincronizzazione).</summary>
    sealed class Relay(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    readonly IDriveClient _client;
    readonly string _folder;
    readonly SyncData _data;
    readonly Dictionary<string, Runtime> _rt = new();
    readonly DispatcherTimer _timer;
    readonly CancellationTokenSource _cts = new();
    readonly HashSet<string> _release = new(); // blocchi chiusi a cui togliere il lucchetto
    readonly NetworkAvailabilityChangedEventHandler _onNetwork;
    string? _openId;
    bool _busy, _disposed;
    IProgress<double>? _flushProgress;
    long _flushTotal, _flushDone;

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
        // La rete torna (Wi-Fi della scuola): si riprova subito, senza aspettare il prossimo tentativo.
        var dispatcher = Dispatcher.CurrentDispatcher;
        _onNetwork = (_, a) =>
        {
            if (a.IsAvailable) dispatcher.BeginInvoke(NetworkRestored);
        };
        NetworkChange.NetworkAvailabilityChanged += _onNetwork;
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

    /// <summary>
    /// Nome di questo PC nel lucchetto e nei nomi delle copie di un conflitto (le prove fingono altri PC; per le prove
    /// dal vivo con un «secondo PC» c'è la variabile d'ambiente ARDESIA_PROVA_PC).
    /// </summary>
    public string MachineName { get; set; } =
        Environment.GetEnvironmentVariable("ARDESIA_PROVA_PC") is { Length: > 0 } test ? test : Environment.MachineName;

    /// <summary>Il lucchetto del blocco aperto si rinnova ogni tanto anche senza caricamenti…</summary>
    public TimeSpan LockRefresh { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>…e un lucchetto più vecchio di così non conta più (Ardesia chiusa male, PC spento).</summary>
    public TimeSpan LockMaxAge { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>C'è una rete? Senza, alla chiusura non si aspetta il secondo tentativo (le prove la fingono).</summary>
    public Func<bool> NetworkAvailable { get; set; } = NetworkInterface.GetIsNetworkAvailable;

    /// <summary>Ogni quanto si guarda la coda (le prove la accorciano).</summary>
    public TimeSpan PumpInterval
    {
        get => _timer.Interval;
        set => _timer.Interval = value;
    }

    public string CachePath(string id) => Path.Combine(_folder, id + NotebookStore.Extension);

    public DriveSyncEntry? Entry(string id) => _data.Files.GetValueOrDefault(id);

    public string? RootFolderId => _data.RootFolderId;

    /// <summary>Vero se ci sono modifiche da caricare, blocchi o PDF per la classe (anche ferme per un errore).</summary>
    public bool HasPending => _data.Files.Values.Any(e => e.Pending || e.ClassPdfPending);

    /// <summary>
    /// Vero se alla chiusura c'è ancora qualcosa da fare su Drive: modifiche da caricare, PDF da caricare o rinominare,
    /// lucchetti da togliere.
    /// </summary>
    public bool HasWork => HasPending || _data.Files.Any(f => PdfWork(f.Value) && RuntimeOf(f.Key) is { PdfBlocked: false })
                                      || _release.Any(_data.Files.ContainsKey);

    /// <summary>Byte da caricare (per «Carico su Google Drive… (2,1 MB)»).</summary>
    public long PendingBytes() => _data.Files.Sum(f =>
    {
        long n = 0;
        try
        {
            if (f.Value.Pending) n += new FileInfo(CachePath(f.Key)).Length;
            if (f.Value.ClassPdfPending) n += new FileInfo(ClassPdfCachePath(f.Key)).Length;
        }
        catch
        {
            // file sparito: non conta
        }
        return n;
    });

    /// <summary>Il PDF per la classe rifatto su questo PC, accanto alla copia in cache del blocco.</summary>
    public string ClassPdfCachePath(string id) => ClassPdfFileFor(CachePath(id));

    /// <summary>Lo stato del PDF per la classe di un blocco (impostazioni del blocco).</summary>
    public DriveClassPdfInfo ClassPdfStatus(string id)
    {
        var e = Entry(id);
        var rt = _rt.GetValueOrDefault(id);
        return new DriveClassPdfInfo(e?.ClassPdfId, e?.ClassPdfId is { } pdf ? ClassPdfLinkOf(pdf) : null, e?.ClassPdfPending == true,
            rt?.PdfUploading == true, e?.ClassPdfUpload, e?.ClassPdfError, e?.ClassPdfShareError);
    }

    /// <summary>
    /// C'è lavoro su Drive per il PDF per la classe: caricarlo, legarlo al blocco, rinominarlo col blocco, condividerlo.
    /// </summary>
    static bool PdfWork(DriveSyncEntry e) =>
        e.ClassPdfPending || (e.ClassPdfId != null && (!e.ClassPdfLinked || e.ClassPdfName != ClassPdfNameOf(e)
                                                       || (!e.ClassPdfShared && e.ClassPdfShareError == null)));

    /// <summary>Nome del PDF su Drive: quello del blocco (§6).</summary>
    static string ClassPdfNameOf(DriveSyncEntry e) => SafeName(e.Title) + ".pdf";

    /// <summary>Dominio con cui si condivide il PDF per la classe (sola lettura, per chi ha il link).</summary>
    public string ShareDomain { get; set; } = GoogleClientConfig.SchoolDomain;

    /// <summary>Il blocco aperto in Ardesia (porta il lucchetto), o null.</summary>
    public string? OpenId => _openId;

    /// <summary>Spiegazione del conflitto per il clic sulla barra, finché il blocco resta aperto.</summary>
    public string? ConflictMessage(string id) => _rt.GetValueOrDefault(id)?.ConflictMessage;

    /// <summary>Stato del blocco e testo per la barra in alto.</summary>
    public (DriveSyncState State, string Text, string Tooltip) Status(string id)
    {
        var e = Entry(id);
        var rt = _rt.GetValueOrDefault(id);
        var state = rt?.State ?? (e?.Pending == true ? DriveSyncState.Pending : DriveSyncState.Synced);
        if (state == DriveSyncState.Synced && e?.Pending == true) state = DriveSyncState.Pending;
        var when = e?.LastUpload is { } t ? $" alle {t:HH:mm}" : "";
        if (rt?.ConflictMessage != null && state is DriveSyncState.Synced or DriveSyncState.Pending)
            return (DriveSyncState.Conflict, "Conflitto: salvata una copia",
                "Su Google Drive il blocco era cambiato anche su un altro PC: le modifiche di questo PC sono in una copia. Clic per la spiegazione.");
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

    // ───────────── lucchetto morbido (§4.4) ─────────────

    /// <summary>
    /// Il blocco aperto in Ardesia (null: nessuno su Drive). Il suo lucchetto si scrive subito e si rinnova; quello del
    /// blocco chiuso si toglie (dal caricamento, se ha modifiche in coda).
    /// </summary>
    public void SetOpen(string? id)
    {
        if (_disposed || id == _openId) return;
        if (_openId != null && _rt.GetValueOrDefault(_openId) is { } old)
        {
            if (old.LockHeld) _release.Add(_openId);
            old.ConflictMessage = null;
        }
        _openId = id;
        if (id != null)
        {
            _release.Remove(id);
            var rt = RuntimeOf(id);
            rt.LockWritten = rt.LockNextTry = DateTime.MinValue;
            rt.LockFailures = 0;
        }
        Pump();
    }

    /// <summary>Il lucchetto di un altro PC, se è recente (quello di questo PC non conta).</summary>
    public DriveLock? LockOf(DriveFile f, bool anyAge = false)
    {
        if (f.AppProperties is not { } p || !p.TryGetValue(LockPcKey, out var pc) || pc.Length == 0) return null;
        if (string.Equals(pc, MachineName, StringComparison.OrdinalIgnoreCase)) return null;
        var at = p.TryGetValue(LockTimeKey, out var t) && DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var x)
            ? x.ToLocalTime() : DateTime.MinValue;
        if (!anyAge && (at == DateTime.MinValue || DateTime.Now - at > LockMaxAge)) return null;
        return new DriveLock(pc, at);
    }

    bool LockIsMine(DriveFile f) =>
        f.AppProperties?.GetValueOrDefault(LockPcKey) is { } pc && string.Equals(pc, MachineName, StringComparison.OrdinalIgnoreCase);

    /// <summary>appProperties del lucchetto: da scrivere (blocco aperto) o da togliere (null = via la chiave).</summary>
    Dictionary<string, string?> LockProps(bool hold) => new()
    {
        [LockPcKey] = hold ? MachineName : null,
        [LockTimeKey] = hold ? DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) : null,
    };

    /// <summary>Scrive (o rinnova) il lucchetto del blocco aperto, oppure toglie il nostro da un blocco chiuso.</summary>
    async Task LockAsync(string id, bool hold)
    {
        var rt = RuntimeOf(id);
        _busy = true;
        try
        {
            if (hold)
            {
                await _client.UpdateMetadataAsync(id, LockProps(true), _cts.Token);
                rt.LockHeld = true;
                rt.LockWritten = DateTime.Now;
                if (id != _openId) _release.Add(id); // chiuso nel frattempo
            }
            else
            {
                // si toglie solo se è ancora nostro: un altro PC può averlo aperto nel frattempo
                var meta = await _client.GetAsync(id, _cts.Token);
                if (!meta.Trashed && LockIsMine(meta)) await _client.UpdateMetadataAsync(id, LockProps(false), _cts.Token);
                rt.LockHeld = false;
                if (id != _openId) _release.Remove(id);
            }
            rt.LockFailures = 0;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // sync fermata
        }
        catch (DriveException ex) when (ex.Status is 403 or 404)
        {
            // eliminato o non più accessibile: lo dice il caricamento; niente lucchetto da togliere
            rt.LockHeld = false;
            _release.Remove(id);
            rt.LockNextTry = DateTime.Now + LockRefresh;
        }
        catch (Exception)
        {
            // senza rete: si riprova (il lucchetto non è indispensabile, il conflitto si riconosce comunque)
            rt.LockFailures++;
            rt.LockNextTry = DateTime.Now + RetryDelays[Math.Min(rt.LockFailures, RetryDelays.Length) - 1];
        }
        finally
        {
            _busy = false;
        }
        if (!_disposed) Pump();
    }

    /// <summary>La rete è tornata: i tentativi in attesa ripartono subito.</summary>
    public void NetworkRestored()
    {
        if (_disposed) return;
        foreach (var rt in _rt.Values)
        {
            if (rt.Failures > 0 || rt.State is DriveSyncState.Offline or DriveSyncState.Retrying)
            {
                rt.Failures = 0;
                rt.NextTry = DateTime.MinValue;
            }
            if (rt.LockFailures > 0)
            {
                rt.LockFailures = 0;
                rt.LockNextTry = DateTime.MinValue;
            }
            if (rt.PdfFailures > 0)
            {
                rt.PdfFailures = 0;
                rt.PdfNextTry = DateTime.MinValue;
            }
        }
        Pump();
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

    /// <summary>
    /// Il PDF per la classe è stato rifatto ({fileId}.pdf in cache): si carica appena il blocco è su Drive, sempre nello
    /// stesso file (§6). Si fa alla chiusura o con «Aggiorna ora», mai a ogni salvataggio.
    /// </summary>
    public void ClassPdfChanged(string id)
    {
        if (_disposed) return;
        if (!_data.Files.TryGetValue(id, out var e)) _data.Files[id] = e = new DriveSyncEntry();
        e.ClassPdfPending = true;
        e.ClassPdfError = null;
        var rt = RuntimeOf(id);
        rt.PdfNextTry = DateTime.MinValue;
        rt.PdfBlocked = false;
        SaveData(_folder, _data);
        StatusChanged?.Invoke(id);
        Pump();
    }

    /// <summary>
    /// «Riprova a condividere» (impostazioni del blocco): dopo un rifiuto si ritenta la condivisione col dominio, per
    /// esempio quando l'amministratore l'ha permessa.
    /// </summary>
    public void RetryShare(string id)
    {
        if (_disposed || Entry(id) is not { ClassPdfId: not null } e) return;
        e.ClassPdfShareError = null;
        e.ClassPdfShared = false;
        var rt = RuntimeOf(id);
        rt.PdfNextTry = DateTime.MinValue;
        rt.PdfBlocked = false;
        SaveData(_folder, _data);
        StatusChanged?.Invoke(id);
        Pump();
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

    /// <summary>
    /// Carica subito tutto quello che è in coda, toglie i lucchetti dei blocchi chiusi e aspetta (al massimo
    /// <paramref name="timeout"/>). Vero se non restano modifiche da caricare.
    /// </summary>
    public async Task<bool> FlushAsync(TimeSpan timeout, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        foreach (var id in _data.Files.Where(f => f.Value.Pending).Select(f => f.Key).ToList()) UploadSoon(id);
        foreach (var id in _data.Files.Where(f => PdfWork(f.Value)).Select(f => f.Key)) RuntimeOf(id).PdfNextTry = DateTime.MinValue;
        foreach (var id in _release)
        {
            var rt = RuntimeOf(id);
            rt.LockFailures = 0;
            rt.LockNextTry = DateTime.MinValue;
        }
        _flushTotal = PendingBytes();
        _flushDone = 0;
        _flushProgress = progress;
        try
        {
            Pump();
            var end = DateTime.Now + timeout;
            while (DateTime.Now < end && !ct.IsCancellationRequested)
            {
                // blocchi da caricare e PDF per la classe da caricare, legare, rinominare o condividere
                var pending = _data.Files.Where(f => f.Value.Pending || (PdfWork(f.Value) && !RuntimeOf(f.Key).PdfBlocked))
                    .Select(f => f.Key).ToList();
                // un lucchetto che non si riesce a togliere non vale l'attesa: scade da solo
                bool releases = _release.Any(id => _data.Files.ContainsKey(id) && RuntimeOf(id).LockFailures == 0);
                if (pending.Count == 0 && !releases) return true;
                // con un errore definitivo, senza accesso o senza rete, inutile aspettare
                bool offline = !NetworkAvailable();
                if (!_busy && (!releases || offline) && pending.All(id => Stuck(id, offline)))
                    return !HasPending;
                try { await Task.Delay(150, ct); } catch (OperationCanceledException) { break; }
            }
            return !HasPending;
        }
        finally
        {
            _flushProgress = null;
        }
    }

    Runtime RuntimeOf(string id) => _rt.TryGetValue(id, out var rt) ? rt : _rt[id] = new Runtime();

    /// <summary>Alla chiusura: per questo blocco non vale la pena aspettare (errore definitivo, niente accesso o rete).</summary>
    bool Stuck(string id, bool offline)
    {
        var rt = RuntimeOf(id);
        if (rt.State == DriveSyncState.SignedOut) return true;
        // finché il blocco è da caricare il PDF aspetta: conta il blocco
        if (_data.Files[id].Pending) return rt.Blocked || rt.Failures >= 2 || (offline && rt.Failures >= 1);
        return rt.PdfBlocked || rt.PdfFailures >= 2 || (offline && rt.PdfFailures >= 1);
    }

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
        if (next != null)
        {
            _ = UploadAsync(next);
            return;
        }

        // PDF per la classe: dopo il blocco (così è fatto con la versione caricata), uno alla volta.
        foreach (var (id, e) in _data.Files)
        {
            if (e.Pending || !PdfWork(e)) continue;
            var rt = RuntimeOf(id);
            if (rt.PdfBlocked || rt.PdfNextTry > now) continue;
            _ = ClassPdfAsync(id);
            return;
        }

        // Lucchetti: via quelli dei blocchi chiusi (se hanno modifiche in coda lo fa il caricamento), poi si rinnova
        // quello del blocco aperto.
        foreach (var id in _release)
        {
            if (_data.Files.GetValueOrDefault(id) is not { Pending: false } || RuntimeOf(id).LockNextTry > now) continue;
            _ = LockAsync(id, hold: false);
            return;
        }
        if (_openId is { } open && _data.Files.ContainsKey(open) && RuntimeOf(open) is { Blocked: false } ort
            && ort.LockWritten + LockRefresh <= now && ort.LockNextTry <= now)
            _ = LockAsync(open, hold: true);
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

            // Conflitto (§4.4): se su Drive c'è una revisione diversa da quella da cui parte la cache, un altro PC ha
            // caricato nel frattempo. La sua versione resta com'è, la nostra va in un file nuovo («tieni entrambi»).
            var meta = await _client.GetAsync(id, _cts.Token);
            if (_disposed) return;
            if (meta.Trashed) throw new DriveException(404, "trashed", "Il blocco appunti è nel cestino di Google Drive.");
            if (e.BaseRevision != null && meta.HeadRevisionId != null && meta.HeadRevisionId != e.BaseRevision)
            {
                await ForkAsync(id, e, meta, data, _cts.Token, serial, notify: true);
                return;
            }

            AdoptLinks(e, meta);

            // lucchetto: si rinnova col caricamento se il blocco è aperto, si toglie (se è nostro) se è stato chiuso
            bool hold = id == _openId;
            bool lockProps = hold || LockIsMine(meta);
            var props = lockProps ? LockProps(hold) : null;
            // il legame col PDF per la classe viaggia anche col contenuto (che ne porta l'ID in ClassPdfDriveId)
            if (e is { ClassPdfId: { } pdfId, ClassPdfLinked: true } && meta.AppProperties?.GetValueOrDefault(ClassPdfKey) != pdfId)
                (props ??= new Dictionary<string, string?>())[ClassPdfKey] = pdfId;
            var wanted = SafeName(e.Title) + NotebookStore.Extension;
            long size = data.Length;
            var progress = _flushProgress == null ? null : new Relay(p =>
                _flushProgress?.Report(_flushTotal > 0 ? Math.Min(1, (_flushDone + p * size) / _flushTotal) : 0));
            var f = await _client.UpdateContentAsync(id, MimeType, data, _cts.Token, props,
                e.Title.Length > 0 && wanted != e.DriveName ? wanted : null, progress);
            if (_disposed) return;
            if (lockProps)
            {
                rt.LockHeld = hold;
                if (hold) rt.LockWritten = DateTime.Now;
                else _release.Remove(id);
            }
            _flushDone += size;
            if (_flushTotal > 0) _flushProgress?.Report(Math.Min(1, (double)_flushDone / _flushTotal));
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

    /// <summary>
    /// Dai metadati del blocco su Drive: il suo PDF per la classe (creato magari da un altro PC) e, per la copia di un
    /// conflitto, il blocco originale.
    /// </summary>
    static void AdoptLinks(DriveSyncEntry e, DriveFile meta)
    {
        if (meta.AppProperties?.GetValueOrDefault(ClassPdfKey) is { Length: > 0 } pdf && pdf != e.ClassPdfId)
        {
            e.ClassPdfId = pdf;
            e.ClassPdfLinked = true;
            e.ClassPdfShared = true; // lo ha condiviso il PC che l'ha creato
            e.ClassPdfShareError = null;
            e.ClassPdfName = null; // si controlla il nome alla prossima occasione
        }
        if (meta.AppProperties?.GetValueOrDefault(CopyOfKey) is { Length: > 0 } of) e.ConflictCopyOf = of;
    }

    /// <summary>
    /// PDF per la classe su Drive (§6), dopo il blocco: la prima volta si crea accanto al blocco, con lo stesso nome, si
    /// lega al blocco (appProperty «pdfClasse») e si condivide col dominio della scuola; poi si aggiorna sempre lo stesso
    /// file (stesso ID, stesso link per gli alunni) e si rinomina col blocco. Se il docente l'ha cestinato dal sito di
    /// Drive se ne crea uno nuovo (con un link nuovo).
    /// </summary>
    async Task ClassPdfAsync(string id)
    {
        var e = _data.Files[id];
        var rt = RuntimeOf(id);
        var ct = _cts.Token;
        byte[]? data = null;
        if (e.ClassPdfPending)
        {
            try
            {
                data = File.ReadAllBytes(ClassPdfCachePath(id));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                e.ClassPdfPending = false; // il PDF in cache non c'è più: niente da caricare
                SaveData(_folder, _data);
            }
            catch (IOException)
            {
                rt.PdfNextTry = DateTime.Now.AddSeconds(2);
                return;
            }
        }
        if (!PdfWork(e)) return;
        long size = data?.Length ?? 0;
        _busy = rt.PdfUploading = true;
        StatusChanged?.Invoke(id);
        try
        {
            var name = ClassPdfNameOf(e);
            bool created = false;
            if (e.ClassPdfId == null)
            {
                // un altro PC può averlo già creato: lo dice il blocco su Drive
                var meta = await _client.GetAsync(id, ct);
                if (meta.Trashed) throw new DriveException(404, "trashed", "Il blocco appunti è nel cestino di Google Drive.");
                AdoptLinks(e, meta);
                if (e.ClassPdfId == null)
                {
                    if (data == null) return; // niente PDF da creare
                    var f = await _client.CreateFileAsync(name, PdfMime, meta.Parents?.FirstOrDefault() ?? e.ParentId, data, ct,
                        new Dictionary<string, string> { [ClassPdfOfKey] = id });
                    if (_disposed) return;
                    e.ClassPdfId = f.Id;
                    e.ClassPdfName = name;
                    e.ClassPdfLinked = e.ClassPdfShared = false;
                    e.ClassPdfShareError = null;
                    e.ClassPdfUpload = DateTime.Now;
                    e.ClassPdfPending = false;
                    created = true;
                    SaveData(_folder, _data);
                }
            }
            var pdfId = e.ClassPdfId!;
            if (!created && (data != null || e.ClassPdfName != name))
            {
                DriveFile f;
                try
                {
                    var progress = data == null || _flushProgress == null ? null : new Relay(p =>
                        _flushProgress?.Report(_flushTotal > 0 ? Math.Min(1, (_flushDone + p * size) / _flushTotal) : 0));
                    f = data != null
                        ? await _client.UpdateContentAsync(pdfId, PdfMime, data, ct, null, e.ClassPdfName != name ? name : null, progress)
                        : await _client.RenameAsync(pdfId, name, ct);
                }
                catch (DriveException ex) when (ex.Status is 403 or 404)
                {
                    f = new DriveFile(pdfId, "", PdfMime, null, null, null, Trashed: true); // non più accessibile: come cestinato
                }
                if (_disposed) return;
                if (f.Trashed)
                {
                    // cestinato o cancellato dal sito di Drive: si dimentica; se c'è un PDF da caricare se ne crea uno nuovo
                    e.ClassPdfId = null;
                    e.ClassPdfName = null;
                    e.ClassPdfLinked = e.ClassPdfShared = false;
                    e.ClassPdfShareError = null;
                    await _client.UpdateMetadataAsync(id, new Dictionary<string, string?> { [ClassPdfKey] = null }, ct);
                    SaveData(_folder, _data);
                    return;
                }
                e.ClassPdfName = name;
                if (data != null)
                {
                    _flushDone += size;
                    e.ClassPdfUpload = DateTime.Now;
                    e.ClassPdfPending = false;
                }
                SaveData(_folder, _data);
            }
            if (!e.ClassPdfLinked)
            {
                await _client.UpdateMetadataAsync(id, new Dictionary<string, string?> { [ClassPdfKey] = pdfId }, ct);
                e.ClassPdfLinked = true;
                SaveData(_folder, _data);
            }
            if (!e.ClassPdfShared && e.ClassPdfShareError == null)
            {
                try
                {
                    await _client.ShareWithDomainAsync(pdfId, ShareDomain, ct);
                    e.ClassPdfShared = true;
                }
                catch (DriveException ex) when (!DriveApi.IsTransient(ex) && ex.Status != 401)
                {
                    // l'amministratore non permette di condividere col dominio: si avvisa una volta, il docente condivide a mano
                    e.ClassPdfShareError = ex.Message;
                    ClassPdfShareRefused?.Invoke(id,
                        $"Il PDF per la classe di «{e.Title}» è su Google Drive, ma Google non ha permesso ad Ardesia di condividerlo " +
                        $"con la scuola ({ex.Message}).\n\nPuoi condividerlo a mano: File › Impostazioni › Questo blocco appunti › " +
                        $"«Apri il PDF su Drive», poi «Condividi» e, in «Accesso generale», «{ShareDomain}» (chiunque abbia il link). " +
                        "Il link per gli alunni resta lo stesso.");
                }
                SaveData(_folder, _data);
            }
            e.ClassPdfError = null;
            rt.PdfFailures = 0;
            SaveData(_folder, _data);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // sync fermata: quel che resta da fare è in sync.json
        }
        catch (GoogleAuthException ex)
        {
            e.ClassPdfError = ex.Message;
            rt.PdfNextTry = DateTime.Now.AddMinutes(5);
            SetState(id, DriveSyncState.SignedOut);
        }
        catch (Exception ex) when (DriveApi.IsTransient(ex))
        {
            rt.PdfFailures++;
            rt.PdfNextTry = DateTime.Now + RetryDelays[Math.Min(rt.PdfFailures, RetryDelays.Length) - 1];
            e.ClassPdfError = ex.Message;
        }
        catch (DriveException ex) when (ex.Status is 403 or 404)
        {
            // il blocco non è più accessibile: lo dice il caricamento del blocco; niente più tentativi per il PDF
            rt.PdfBlocked = true;
            e.ClassPdfError = ex.Message;
        }
        catch (Exception ex)
        {
            rt.PdfFailures++;
            rt.PdfNextTry = DateTime.Now + RetryDelays[^1];
            e.ClassPdfError = ex.Message;
        }
        finally
        {
            _busy = rt.PdfUploading = false;
            if (!_disposed) StatusChanged?.Invoke(id);
        }
        if (!_disposed) Pump();
    }

    /// <summary>
    /// «Tieni entrambi» (§4.4, scelta dell'utente: senza domande): <paramref name="data"/> (le modifiche di questo PC)
    /// va in un file nuovo «{titolo} (modifiche da {PC}, dd-MM HH.mm).ardesia» nella stessa cartella; la versione
    /// dell'altro PC resta com'è. La copia in cache passa al nuovo ID. Restituisce il nuovo ID.
    /// </summary>
    async Task<string> ForkAsync(string id, DriveSyncEntry e, DriveFile meta, byte[] data, CancellationToken ct, int serial, bool notify)
    {
        var other = LockOf(meta, anyAge: true)?.Pc;
        var oldTitle = e.Title.Length > 0 ? e.Title : TitleOf(meta.Name);
        var title = $"{oldTitle} (modifiche da {MachineName}, {DateTime.Now:dd-MM HH.mm})";
        var parent = meta.Parents?.FirstOrDefault() ?? e.ParentId;
        bool open = id == _openId;
        // la copia sa di chi è copia; il PDF per la classe («pdfClasse») resta al blocco originale
        var props = new Dictionary<string, string> { [CopyOfKey] = id };
        if (meta.AppProperties?.GetValueOrDefault("ardesiaFormato") is { } format) props["ardesiaFormato"] = format;
        if (open)
            foreach (var (k, v) in LockProps(true)) props[k] = v!;
        var f = await _client.CreateFileAsync(SafeName(title) + NotebookStore.Extension, MimeType, parent, data, ct, props);
        if (_disposed) return f.Id;

        // La copia in cache di adesso (può essere più nuova di quella caricata, se si è salvato nel frattempo).
        var oldCache = CachePath(id);
        try
        {
            File.Copy(oldCache, CachePath(f.Id), overwrite: true);
        }
        catch (IOException)
        {
            File.WriteAllBytes(CachePath(f.Id), data); // la copia caricata
        }
        var rt = RuntimeOf(id);
        AdoptLinks(e, meta);
        var message =
            $"«{oldTitle}» su Google Drive era stato modificato anche {(other != null ? $"sul PC {other}" : "su un altro PC")} " +
            "mentre qui c'erano modifiche non ancora caricate. Le modifiche fatte in due posti non si uniscono.\n\n" +
            "Per non perdere niente Ardesia ha tenuto tutte e due le versioni, nella stessa cartella di Google Drive:\n" +
            $"•  «{oldTitle}»: la versione dell'altro PC;\n" +
            $"•  «{title}»: le modifiche fatte su questo PC. Ardesia continua a lavorare su questa.\n\n" +
            "Se serve, copia le pagine che ti interessano da un blocco all'altro ed elimina quello che non ti serve più." +
            (e.ClassPdfId != null
                ? $"\n\nIl PDF per la classe (quello del link che hanno gli alunni) resta legato a «{oldTitle}»; per la copia è spento."
                : "");
        var ne = new DriveSyncEntry
        {
            Title = title,
            DriveName = f.Name,
            ParentId = parent,
            Folder = e.Folder,
            BaseRevision = f.HeadRevisionId,
            ModifiedTime = f.ModifiedTime ?? DateTime.Now,
            LastUpload = DateTime.Now,
            Pending = rt.Serial != serial, // salvato di nuovo durante il caricamento: si ricarica sul file nuovo
            ConflictCopyOf = id,
        };
        // Un PDF rifatto con le modifiche di questo PC non va sul PDF del blocco originale, che ora è dell'altro PC.
        if (e.ClassPdfPending)
        {
            e.ClassPdfPending = false;
            try { File.Delete(ClassPdfCachePath(id)); } catch { /* resta: non si carica comunque */ }
        }
        _data.Files[f.Id] = ne;
        var nrt = RuntimeOf(f.Id);
        nrt.ConflictMessage = message;
        if (ne.Pending) nrt.First = nrt.Last = DateTime.Now;
        if (open)
        {
            _openId = f.Id;
            nrt.LockHeld = true;
            nrt.LockWritten = DateTime.Now;
            if (rt.LockHeld) _release.Add(id);
        }

        // Il file di prima ora è dell'altro PC: la nostra cache non vale più (alla prossima apertura si riscarica).
        e.Pending = false;
        e.BaseRevision = null;
        e.LastError = null;
        rt.Failures = 0;
        rt.ConflictMessage = null;
        SaveData(_folder, _data);
        if (notify) Forked?.Invoke(new DriveFork(id, f.Id, oldTitle, title, message));
        try { File.Delete(oldCache); } catch { /* ancora in uso: si riscarica comunque */ }
        SetState(id, DriveSyncState.Synced);
        SetState(f.Id, ne.Pending ? DriveSyncState.Pending : DriveSyncState.Synced);
        return f.Id;
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
            AdoptLinks(e, f);
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
            return new DriveNotebook(f.Id, pending?.Pending == true ? pending.Title : TitleOf(f.Name), c, s, f.ModifiedTime, f.Size,
                LockOf(f)?.Pc, f.AppProperties?.GetValueOrDefault(ClassPdfKey) ?? pending?.ClassPdfId);
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
    /// su un altro PC) si scarica; se è cambiata là e anche qui, conflitto: le modifiche di qui vanno in una copia
    /// (§4.4), che è quella da aprire. Senza rete si apre la cache, con un avviso; se manca, non si può aprire.
    /// Se il blocco risulta aperto su un altro PC si chiede a <paramref name="confirmOpenElsewhere"/> (falso = annulla).
    /// </summary>
    public async Task<DriveOpenResult> OpenAsync(string id, IProgress<double>? progress, CancellationToken ct,
        Func<DriveLock, bool>? confirmOpenElsewhere = null)
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
        if (confirmOpenElsewhere != null && LockOf(meta) is { } lk && !confirmOpenElsewhere(lk)) throw new OperationCanceledException();

        if (e == null) _data.Files[id] = e = new DriveSyncEntry();
        AdoptLinks(e, meta);
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
        else if (hasCache && e.Pending && e.BaseRevision != null)
        {
            // Cambiata altrove e anche qui: «tieni entrambi», si apre la copia con le modifiche di questo PC.
            var newId = await ForkAsync(id, e, meta, await File.ReadAllBytesAsync(cache, ct), ct, RuntimeOf(id).Serial, notify: false);
            return new DriveOpenResult(CachePath(newId), null, RuntimeOf(newId).ConflictMessage);
        }
        else if (hasCache && e.Pending)
        {
            // revisione di partenza sconosciuta: si tiene questa; Drive conserva le revisioni precedenti
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

    /// <summary>
    /// Nel cestino di Drive (le cartelle no: §2.2), poi via dalla cache. Con <paramref name="classPdfId"/> anche il PDF
    /// per la classe (se il docente l'ha chiesto); altrimenti il PDF resta, col suo link. Senza rete non si elimina.
    /// </summary>
    public async Task TrashAsync(string id, CancellationToken ct, string? classPdfId = null)
    {
        var rt = RuntimeOf(id);
        rt.Blocked = rt.PdfBlocked = true; // niente caricamenti nel frattempo
        try
        {
            if (classPdfId != null)
            {
                try
                {
                    await _client.TrashAsync(classPdfId, ct);
                }
                catch (DriveException ex) when (ex.Status == 404)
                {
                    // già sparito
                }
            }
            await _client.TrashAsync(id, ct);
        }
        catch (DriveException ex) when (ex.Status == 404)
        {
            // già sparito
        }
        catch
        {
            rt.Blocked = rt.PdfBlocked = false;
            throw;
        }
        Forget(id);
    }

    /// <summary>Dimentica un blocco (eliminato da Drive): via da sync.json e dalla cache.</summary>
    public void Forget(string id)
    {
        _data.Files.Remove(id);
        _rt.Remove(id);
        _release.Remove(id);
        if (_openId == id) _openId = null;
        try { File.Delete(CachePath(id)); } catch { /* resta: nessuno la usa più */ }
        try { File.Delete(ClassPdfCachePath(id)); } catch { /* idem */ }
        SaveData(_folder, _data);
    }

    /// <summary>
    /// «Svuota la cache di Drive su questo PC»: toglie le copie senza modifiche da caricare (e non quella aperta).
    /// Si riscaricano alla prossima apertura. Restituisce i byte liberati.
    /// </summary>
    public long ClearCache(string? keepId)
    {
        long freed = 0;
        foreach (var file in CacheFiles())
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (id == keepId || (file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? Entry(id)?.ClassPdfPending : Entry(id)?.Pending) == true)
                continue;
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
    public long CacheSize() => CacheFiles().Sum(f => new FileInfo(f).Length);

    /// <summary>Copie in cache dei blocchi e dei loro PDF per la classe.</summary>
    List<string> CacheFiles() =>
        Directory.EnumerateFiles(_folder, "*" + NotebookStore.Extension).Concat(Directory.EnumerateFiles(_folder, "*.pdf")).ToList();

    /// <summary>Dopo «Esci»: cancella la cartella dell'account se non ci sono modifiche (o PDF) da caricare.</summary>
    public static void DeleteAccountCache(string email)
    {
        var folder = AccountFolder(email);
        if (!Directory.Exists(folder) || LoadData(folder).Files.Values.Any(e => e.Pending || e.ClassPdfPending)) return;
        try { Directory.Delete(folder, recursive: true); } catch { /* file in uso: resta */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _cts.Cancel();
        NetworkChange.NetworkAvailabilityChanged -= _onNetwork;
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
