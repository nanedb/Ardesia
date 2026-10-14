using System.IO;

namespace Ardesia.Services;

/// <summary>Trova le cartelle di Google Drive per desktop (di solito sull'unità G:).</summary>
public static class DriveLocator
{
    static readonly string[] Names = { "Il mio Drive", "My Drive", "Drive condivisi", "Shared drives" };
    static List<string>? _cache;

    public static IReadOnlyList<string> GoogleDriveFolders()
    {
        if (_cache != null) return _cache;
        var result = new List<string>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType is DriveType.CDRom or DriveType.Removable) continue;
                foreach (var name in Names)
                {
                    try
                    {
                        var p = Path.Combine(d.Name, name);
                        if (Directory.Exists(p)) result.Add(p);
                    }
                    catch
                    {
                        // unità non pronta
                    }
                }
            }
        }
        catch
        {
            // ignorato
        }
        return _cache = result;
    }

    public static void AddPlaces(Microsoft.Win32.FileDialog dlg)
    {
        foreach (var p in GoogleDriveFolders())
            dlg.CustomPlaces.Add(new Microsoft.Win32.FileDialogCustomPlace(p));
    }
}
