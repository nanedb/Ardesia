using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ardesia.Models;

namespace Ardesia.Services;

/// <summary>
/// Pagine PDF già pronte, in <c>%LocalAppData%\Ardesia\PdfCache</c>: il nome del file è l'impronta di tutto ciò che si
/// vede sulla pagina (titolo, data, sfondo, tratti, oggetti, immagini). Rifacendo il PDF si convertono solo le pagine
/// cambiate; le altre si rimontano così come sono. Vale per tutti i blocchi appunti e per tutte le esportazioni.
/// </summary>
public static class PdfPageCache
{
    /// <summary>Da cambiare quando cambia il modo di disegnare le pagine nel PDF: le pagine vecchie non valgono più.</summary>
    const string RendererVersion = "vettoriale-5";

    const long MaxBytes = 300L * 1024 * 1024;
    static readonly TimeSpan MaxAge = TimeSpan.FromDays(60);
    static bool _cleaned;

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ardesia", "PdfCache");

    /// <summary>Spenta con la variabile d'ambiente <c>ARDESIA_SENZA_CACHE_PDF</c> (per le prove).</summary>
    static bool Disabled => Environment.GetEnvironmentVariable("ARDESIA_SENZA_CACHE_PDF") is { Length: > 0 };

    public static string Key(Notebook nb, NotebookNode node)
    {
        var data = node.Page ?? new PageData();
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string s)
        {
            h.AppendData(Encoding.UTF8.GetBytes(s));
            h.AppendData(new byte[] { 0 });
        }
        void AddBytes(byte[]? b)
        {
            Add((b?.Length ?? -1).ToString());
            if (b != null) h.AppendData(b);
        }

        Add(RendererVersion);
        Add(node.Title);
        Add(data.Created.Ticks.ToString());
        Add(data.Background.ToString());
        Add(data.BackgroundColor);
        Add(JsonSerializer.Serialize(data.Elements));
        AddBytes(data.Ink);
        foreach (var id in data.Elements.Select(e => e.MediaId).OfType<string>().Distinct())
            AddBytes(nb.Media.TryGetValue(id, out var media) ? media : null);
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }

    public static PdfPagePart? Load(string key)
    {
        if (Disabled) return null;
        var file = Path.Combine(Dir, key + ".pagina");
        try
        {
            if (!File.Exists(file)) return null;
            PdfPagePart part;
            using (var fs = File.OpenRead(file)) part = PdfPagePart.Load(fs);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow); // usata: la pulizia la tiene
            return part;
        }
        catch
        {
            // file danneggiato: si rifà la pagina
            try { File.Delete(file); } catch { /* pazienza */ }
            return null;
        }
    }

    public static void Store(string key, PdfPagePart part)
    {
        if (Disabled) return;
        try
        {
            Directory.CreateDirectory(Dir);
            var file = Path.Combine(Dir, key + ".pagina");
            var tmp = file + "." + Environment.ProcessId + ".tmp";
            using (var fs = File.Create(tmp)) part.Save(fs);
            File.Move(tmp, file, overwrite: true);
        }
        catch
        {
            // senza cache si va solo più piano
        }
    }

    /// <summary>Toglie le pagine non usate da 60 giorni e, se la cache supera 300 MB, le meno usate. Una volta per avvio.</summary>
    public static void CleanUp()
    {
        if (_cleaned) return;
        _cleaned = true;
        try
        {
            if (!Directory.Exists(Dir)) return;
            var files = new DirectoryInfo(Dir).GetFiles("*.pagina").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            long total = 0;
            foreach (var f in files)
            {
                total += f.Length;
                if (total > MaxBytes || DateTime.UtcNow - f.LastWriteTimeUtc > MaxAge)
                    try { f.Delete(); } catch { /* in uso */ }
            }
            foreach (var t in new DirectoryInfo(Dir).GetFiles("*.tmp"))
                if (DateTime.UtcNow - t.LastWriteTimeUtc > TimeSpan.FromDays(1))
                    try { t.Delete(); } catch { /* in uso */ }
        }
        catch
        {
            // la pulizia può aspettare il prossimo avvio
        }
    }
}
