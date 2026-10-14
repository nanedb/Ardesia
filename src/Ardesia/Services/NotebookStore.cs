using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ardesia.Models;

namespace Ardesia.Services;

/// <summary>
/// Formato .ardesia: un archivio ZIP che contiene
///   mimetype            → "application/vnd.ardesia.notebook"
///   notebook.json       → struttura, titoli, sfondi, caselle di testo
///   ink/{pagina}.isf    → tratti di penna (Ink Serialized Format)
///   media/{id}          → immagini incorporate
/// </summary>
public static class NotebookStore
{
    public const string Extension = ".ardesia";
    public const string FileFilter = "Blocco appunti Ardesia (*.ardesia)|*.ardesia";
    const string MimeType = "application/vnd.ardesia.notebook";

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Save(Notebook nb, string path)
    {
        path = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(path)!;
        var tmp = Path.Combine(dir, "~" + Path.GetFileName(path) + ".tmp");

        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            Write(zip, "mimetype", System.Text.Encoding.ASCII.GetBytes(MimeType), CompressionLevel.NoCompression);
            Write(zip, "notebook.json", JsonSerializer.SerializeToUtf8Bytes(nb, Json), CompressionLevel.Optimal);

            var usedMedia = new HashSet<string>();
            foreach (var page in nb.AllPages())
            {
                if (page.Page is not { } data) continue;
                if (data.Ink is { Length: > 0 })
                    Write(zip, $"ink/{page.Id}.isf", data.Ink, CompressionLevel.Fastest);
                foreach (var e in data.Elements)
                    if (e.MediaId != null) usedMedia.Add(e.MediaId);
            }
            foreach (var id in usedMedia)
                if (nb.Media.TryGetValue(id, out var bytes))
                    Write(zip, $"media/{id}", bytes, CompressionLevel.NoCompression);
        }

        // Sostituzione "atomica": se qualcosa va storto durante la scrittura il file precedente resta intatto.
        if (File.Exists(path))
        {
            try
            {
                File.Replace(tmp, path, null);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Alcune unità di rete/cloud non supportano Replace: ripiego su una copia.
            }
            File.Copy(tmp, path, overwrite: true);
            File.Delete(tmp);
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    public static Notebook Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        var entry = zip.GetEntry("notebook.json")
                    ?? throw new InvalidDataException("Il file non è un blocco appunti di Ardesia.");
        Notebook nb;
        using (var s = entry.Open())
            nb = JsonSerializer.Deserialize<Notebook>(s, Json)
                 ?? throw new InvalidDataException("Il blocco appunti è vuoto o danneggiato.");

        if (nb.FormatVersion > Notebook.CurrentFormatVersion)
            throw new InvalidDataException("Questo blocco appunti è stato creato con una versione più recente di Ardesia. Aggiorna l'applicazione per aprirlo.");

        nb.FixParents();
        foreach (var page in nb.AllPages())
        {
            var inkEntry = zip.GetEntry($"ink/{page.Id}.isf");
            if (inkEntry != null) page.Page!.Ink = ReadAll(inkEntry);
        }
        foreach (var e in zip.Entries)
            if (e.FullName.StartsWith("media/", StringComparison.Ordinal) && e.Name.Length > 0)
                nb.Media[e.Name] = ReadAll(e);

        return nb;
    }

    static void Write(ZipArchive zip, string name, byte[] data, CompressionLevel level)
    {
        var e = zip.CreateEntry(name, level);
        using var s = e.Open();
        s.Write(data, 0, data.Length);
    }

    static byte[] ReadAll(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
