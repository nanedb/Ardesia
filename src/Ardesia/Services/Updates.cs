using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Ardesia.Services;

/// <summary>Una versione di Ardesia pubblicata su GitHub (release), con le note per i docenti e l'installer.</summary>
public sealed class ReleaseInfo
{
    public string Version { get; set; } = "";
    public string Notes { get; set; } = "";
    public string PageUrl { get; set; } = "";
    public string? SetupUrl { get; set; }
    public long SetupSize { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public Version? Parsed =>System.Version.TryParse(Version, out var v) ? v : null;
}

/// <summary>
/// Controllo delle versioni nuove: Ardesia chiede a GitHub l'ultima release a ogni avvio (e dal menu File), scarica l'installer
/// e lo avvia all'uscita in modalità silenziosa; l'installer aspetta che Ardesia sia chiusa e poi la riapre.
/// Senza rete, o con il repository non raggiungibile, non dice niente.
/// </summary>
public static class Updates
{
    const string LatestUrl = "https://api.github.com/repos/nanedb/Ardesia/releases/latest";

    /// <summary>
    /// Per le prove: percorso (o indirizzo) di un JSON come quello di GitHub, letto a ogni avvio al posto della release
    /// vera; l'installer può essere un file locale.
    /// </summary>
    const string TestVariable = "ARDESIA_PROVA_AGGIORNAMENTI";

    public static Version Current { get; } = typeof(Updates).Assembly.GetName().Version ?? new Version(0, 0, 0);

    // dopo Current, che serve per lo User-Agent
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Ardesia/" + Current.ToString(3)); // GitHub lo richiede
        return http;
    }

    /// <summary>
    /// Vero se Ardesia è installata con ArdesiaSetup (accanto all'eseguibile c'è il programma di disinstallazione):
    /// allora si aggiorna da sola; la versione portatile invece apre la pagina della release.
    /// </summary>
    public static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>Installer scaricato da avviare quando Ardesia si chiude (vedi <see cref="LaunchPending"/>).</summary>
    public static string? PendingSetup { get; set; }

    /// <summary>
    /// Chiede a GitHub l'ultima release. <c>Newer</c> è la versione più nuova di quella in uso, se c'è;
    /// <c>Checked</c> è falso se GitHub non ha risposto (senza rete, o troppe richieste dallo stesso indirizzo):
    /// allora <c>Newer</c> viene dall'ultima risposta buona, salvata nelle impostazioni.
    /// </summary>
    public static async Task<(ReleaseInfo? Newer, bool Checked)> CheckAsync(AppSettings settings, CancellationToken ct = default)
    {
        static ReleaseInfo? IfNewer(ReleaseInfo? r) => r?.Parsed is { } v && v > Current ? r : null;

        var test = Environment.GetEnvironmentVariable(TestVariable);
        if (!string.IsNullOrEmpty(test))
        {
            var fake = Parse(test.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? await Http.GetStringAsync(test, ct)
                : await File.ReadAllTextAsync(test, ct));
            return (IfNewer(fake), true);
        }

        ReleaseInfo? latest;
        try
        {
            using var response = await Http.GetAsync(LatestUrl, ct);
            // 403/429: GitHub accetta 60 richieste all'ora per indirizzo, e le LIM di una scuola escono spesso
            // dallo stesso (molte accese insieme alla prima ora): vale l'ultima risposta buona
            if ((int)response.StatusCode is 403 or 429 || (int)response.StatusCode >= 500)
                return (IfNewer(settings.LatestRelease), false);
            // 404: nessuna release pubblicata
            latest = response.IsSuccessStatusCode ? Parse(await response.Content.ReadAsStringAsync(ct)) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (IfNewer(settings.LatestRelease), false); // senza rete: si riprova al prossimo avvio
        }
        settings.UpdateCheckedAt = DateTime.UtcNow;
        settings.LatestRelease = latest;
        settings.Save();
        return (IfNewer(latest), true);
    }

    /// <summary>Legge la risposta di GitHub (releases/latest): versione dal tag «vX.Y.Z», note, installer.</summary>
    internal static ReleaseInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string Get(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        if (root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
        var info = new ReleaseInfo
        {
            Version = Get(root, "tag_name").TrimStart('v', 'V'),
            Notes = Get(root, "body").Replace("\r\n", "\n").Trim(),
            PageUrl = Get(root, "html_url"),
        };
        if (info.Parsed == null) return null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = Get(a, "name");
                if (!name.StartsWith("ArdesiaSetup-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                info.SetupUrl = Get(a, "browser_download_url");
                info.SetupSize = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                break;
            }
        }
        return info;
    }

    /// <summary>Scarica l'installer nella cartella temporanea (prima in un file .part) e ne restituisce il percorso.</summary>
    public static async Task<string> DownloadSetupAsync(ReleaseInfo release, IProgress<double> progress, CancellationToken ct)
    {
        if (release.SetupUrl is not { Length: > 0 } url) throw new InvalidOperationException("In questa versione manca il programma di installazione.");
        var dir = Path.Combine(Path.GetTempPath(), "Ardesia-aggiornamento");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"ArdesiaSetup-{release.Version}.exe");
        var part = file + ".part";

        Stream source;
        long total;
        HttpResponseMessage? response = null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            source = File.OpenRead(uri.LocalPath); // prove con un installer locale
            total = source.Length;
        }
        else
        {
            response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            source = await response.Content.ReadAsStreamAsync(ct);
            total = response.Content.Headers.ContentLength ?? release.SetupSize;
        }
        try
        {
            await using (var target = File.Create(part))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0) progress.Report((double)done / total);
                }
                if (release.SetupSize > 0 && done != release.SetupSize)
                    throw new IOException("Il programma di installazione non è arrivato intero: riprova più tardi.");
            }
            File.Move(part, file, overwrite: true);
            return file;
        }
        catch
        {
            try { File.Delete(part); } catch { /* resta nella cartella temporanea */ }
            throw;
        }
        finally
        {
            source.Dispose();
            response?.Dispose();
        }
    }

    /// <summary>
    /// All'uscita di Ardesia: avvia l'installer scaricato in modalità silenziosa (si vede solo l'avanzamento).
    /// /ATTENDI fa aspettare all'installer che questa Ardesia sia chiusa (il suo mutex, vedi SingleInstance);
    /// /AGGIORNA=1 le fa riaprire Ardesia alla fine (installer\Ardesia.iss).
    /// </summary>
    public static void LaunchPending()
    {
        if (PendingSetup is not { } setup || !File.Exists(setup)) return;
        PendingSetup = null;
        try
        {
            Process.Start(new ProcessStartInfo(setup,
                $"/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /AGGIORNA=1 \"/ATTENDI={SingleInstance.MutexName}\"")
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // installer bloccato (antivirus, criteri della scuola): Ardesia resta com'è e lo riproporrà
        }
    }
}
