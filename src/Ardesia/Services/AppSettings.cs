using System.IO;
using System.Text.Json;

namespace Ardesia.Services;

/// <summary>Una penna (o evidenziatore) della barra, con il suo colore e spessore.</summary>
public class PenPreset
{
    public bool Highlighter { get; set; }
    public bool Pencil { get; set; }

    /// <summary>Solo per la matita: durezza da 0 (3H) a 6 (3B).</summary>
    public int Grade { get; set; } = 3;
    public string Color { get; set; } = "#000000";
    public double Width { get; set; } = 2.5;

    public static List<PenPreset> Defaults() => new()
    {
        new() { Color = "#000000", Width = 2.5 },
        new() { Color = "#1E5AC8", Width = 2.5 },
        new() { Color = "#D32F2F", Width = 2.5 },
        new() { Color = "#2E7D32", Width = 4 },
        new() { Pencil = true, Grade = 3, Width = 2.5 },
        new() { Highlighter = true, Color = "#FFD600", Width = 4 },
        new() { Highlighter = true, Color = "#76FF03", Width = 4 },
    };
}

public class AppSettings
{
    public string? LastFile { get; set; }
    public List<string> RecentFiles { get; set; } = new();
    public string? LastSaveFolder { get; set; }
    public string? LastExportFolder { get; set; }
    public bool SimpleRibbon { get; set; }

    /// <summary>«Il dito sposta la pagina»: da attivare sulle LIM che distinguono penna e dito (es. Newline).</summary>
    public bool FingerPans { get; set; }
    public double EraserSize { get; set; } = 22;
    public List<PenPreset> Pens { get; set; } = PenPreset.Defaults();

    /// <summary>Se false le impostazioni non vengono scritte su disco (usato nei test).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Persist { get; set; } = true;

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ardesia");

    static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // impostazioni illeggibili: si riparte dai valori predefiniti
        }
        return new AppSettings();
    }

    public void Save()
    {
        if (!Persist) return;
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // non bloccare l'app per un problema con le impostazioni
        }
    }

    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 10) RecentFiles.RemoveRange(10, RecentFiles.Count - 10);
        LastFile = path;
        // la cartella della cache di Drive non è un posto in cui proporre di salvare
        if (!Google.DriveSync.TryParseCachePath(path, out _, out _)) LastSaveFolder = Path.GetDirectoryName(path);
    }
}
