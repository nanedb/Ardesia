using System.IO;
using System.Text.Json;

namespace Ardesia.Services.Google;

/// <summary>
/// ID client OAuth di Ardesia (tipo «App desktop»), letto dal file JSON scaricato dalla console di Google Cloud
/// (`client_secret_….json` o `google-client.json`). Per le app desktop Google non considera segreto il secret.
/// </summary>
public sealed class GoogleClientConfig
{
    /// <summary>Dominio della scuola: l'app è «interna», vale solo per questi account.</summary>
    public const string SchoolDomain = "liceofoscarini.it";

    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public string AuthUri { get; init; } = "https://accounts.google.com/o/oauth2/v2/auth";
    public string TokenUri { get; init; } = "https://oauth2.googleapis.com/token";
    public string? ProjectId { get; init; }
    public required string SourceFile { get; init; }

    /// <summary>Legge il file indicato, o lo cerca accanto all'eseguibile e in %AppData%\Ardesia.</summary>
    public static GoogleClientConfig Load(string? path = null)
    {
        path ??= Find() ?? throw new FileNotFoundException(
            "Non trovo il file del client Google (google-client.json o client_secret_….json) " +
            "né accanto ad Ardesia né in " + AppSettings.Folder + ".");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        // La console scrive {"installed": {...}} per le app desktop, {"web": {...}} per le app web.
        if (!doc.RootElement.TryGetProperty("installed", out var c))
            throw new InvalidDataException(doc.RootElement.TryGetProperty("web", out _)
                ? "Il client è di tipo «Applicazione web»: serve un client di tipo «App desktop»."
                : "Il file non sembra un client OAuth di Google.");
        string Get(string name) => c.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
        var cfg = new GoogleClientConfig
        {
            ClientId = Get("client_id"),
            ClientSecret = Get("client_secret"),
            ProjectId = Get("project_id"),
            SourceFile = path,
        };
        if (!cfg.ClientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) || cfg.ClientSecret.Length == 0)
            throw new InvalidDataException("Nel file mancano l'ID client o il secret.");
        return cfg;
    }

    static string? Find()
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, AppSettings.Folder })
        {
            try
            {
                var named = Path.Combine(dir, "google-client.json");
                if (File.Exists(named)) return named;
                if (Directory.Exists(dir) && Directory.GetFiles(dir, "client_secret_*.json").FirstOrDefault() is { } f) return f;
            }
            catch
            {
                // cartella non leggibile
            }
        }
        return null;
    }
}
