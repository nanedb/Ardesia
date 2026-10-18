using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ardesia.Services.Google;

/// <summary>Errore delle API di Drive, con il motivo («accessNotConfigured», «insufficientFilePermissions»…).</summary>
public sealed class DriveException(int status, string reason, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Reason { get; } = reason;
}

/// <summary>Un file di Drive, con i soli campi che servono ad Ardesia.</summary>
public sealed record DriveFile(string Id, string Name, string? MimeType, string? WebViewLink, string? HeadRevisionId, long? Version);

/// <summary>Chiamate essenziali alle API di Drive v3 con HttpClient (niente librerie esterne).</summary>
public sealed class DriveApi(Func<string> accessToken)
{
    public const string FolderMime = "application/vnd.google-apps.folder";
    const string Api = "https://www.googleapis.com/drive/v3/files";
    const string Upload = "https://www.googleapis.com/upload/drive/v3/files";
    const string Fields = "id,name,mimeType,webViewLink,headRevisionId,version";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public Task<DriveFile> CreateFolderAsync(string name, string? parent, CancellationToken ct) =>
        SendFileAsync(HttpMethod.Post, $"{Api}?fields={Fields}", Json(Metadata(name, FolderMime, parent)), ct);

    /// <summary>Nuovo file con contenuto (caricamento «multipart»: metadati e dati in una sola richiesta).</summary>
    public Task<DriveFile> CreateFileAsync(string name, string mimeType, string? parent, byte[] data, CancellationToken ct,
        IDictionary<string, string>? appProperties = null) =>
        SendFileAsync(HttpMethod.Post, $"{Upload}?uploadType=multipart&fields={Fields}",
            Multipart(Metadata(name, mimeType, parent, appProperties), mimeType, data), ct);

    /// <summary>Sostituisce il contenuto di un file esistente (stesso ID, quindi stesso link e stesse condivisioni).</summary>
    public Task<DriveFile> UpdateContentAsync(string id, string mimeType, byte[] data, CancellationToken ct,
        IDictionary<string, string>? appProperties = null) =>
        SendFileAsync(HttpMethod.Patch, $"{Upload}/{id}?uploadType=multipart&fields={Fields}",
            Multipart(Metadata(null, null, null, appProperties), mimeType, data), ct);

    public Task<DriveFile> GetAsync(string id, CancellationToken ct) =>
        SendFileAsync(HttpMethod.Get, $"{Api}/{id}?fields={Fields}", null, ct);

    public async Task<byte[]> DownloadAsync(string id, CancellationToken ct)
    {
        using var r = await SendAsync(HttpMethod.Get, $"{Api}/{id}?alt=media", null, ct);
        return await r.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Elenco con una ricerca di Drive (es. «mimeType='…' and trashed=false»).</summary>
    public async Task<List<DriveFile>> ListAsync(string q, CancellationToken ct)
    {
        var url = $"{Api}?q={Uri.EscapeDataString(q)}&fields={Uri.EscapeDataString($"files({Fields})")}&pageSize=100&orderBy=modifiedTime%20desc";
        using var r = await SendAsync(HttpMethod.Get, url, null, ct);
        var json = JsonNode.Parse(await r.Content.ReadAsStringAsync(ct));
        return json?["files"]?.AsArray().Select(f => ToFile(f!)).ToList() ?? new();
    }

    /// <summary>Lettura per chi, nel dominio, ha il link (il file non compare nelle ricerche degli altri).</summary>
    public async Task ShareWithDomainAsync(string id, string domain, CancellationToken ct)
    {
        var body = new JsonObject { ["type"] = "domain", ["domain"] = domain, ["role"] = "reader", ["allowFileDiscovery"] = false };
        using var _ = await SendAsync(HttpMethod.Post, $"{Api}/{id}/permissions?sendNotificationEmail=false", Json(body), ct);
    }

    /// <summary>Nel cestino (non cancellato: si recupera da Drive per 30 giorni).</summary>
    public Task<DriveFile> TrashAsync(string id, CancellationToken ct) =>
        SendFileAsync(HttpMethod.Patch, $"{Api}/{id}?fields={Fields}", Json(new JsonObject { ["trashed"] = true }), ct);

    async Task<DriveFile> SendFileAsync(HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        using var r = await SendAsync(method, url, content, ct);
        return ToFile(JsonNode.Parse(await r.Content.ReadAsStringAsync(ct))!);
    }

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken());
        var r = await Http.SendAsync(req, ct);
        if (r.IsSuccessStatusCode) return r;
        var body = await r.Content.ReadAsStringAsync(ct);
        r.Dispose();
        string reason = "", message = body;
        try
        {
            var err = JsonNode.Parse(body)?["error"];
            message = err?["message"]?.GetValue<string>() ?? body;
            reason = err?["errors"]?[0]?["reason"]?.GetValue<string>()
                     ?? err?["status"]?.GetValue<string>() ?? "";
        }
        catch (JsonException)
        {
            // risposta non JSON
        }
        throw new DriveException((int)r.StatusCode, reason, message);
    }

    static JsonObject Metadata(string? name, string? mimeType, string? parent, IDictionary<string, string>? appProperties = null)
    {
        var m = new JsonObject();
        if (name != null) m["name"] = name;
        if (mimeType != null) m["mimeType"] = mimeType;
        if (parent != null) m["parents"] = new JsonArray(parent);
        if (appProperties != null)
        {
            var p = new JsonObject();
            foreach (var (k, v) in appProperties) p[k] = v;
            m["appProperties"] = p;
        }
        return m;
    }

    static StringContent Json(JsonNode node) => new(node.ToJsonString(), Encoding.UTF8, "application/json");

    static MultipartContent Multipart(JsonNode metadata, string mimeType, byte[] data)
    {
        var boundary = "ardesia-" + Guid.NewGuid().ToString("N");
        var multi = new MultipartContent("related", boundary);
        // senza virgolette intorno al separatore, come negli esempi di Google
        multi.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/related; boundary={boundary}");
        multi.Add(Json(metadata));
        var media = new ByteArrayContent(data);
        media.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        multi.Add(media);
        return multi;
    }

    static DriveFile ToFile(JsonNode f) => new(
        f["id"]?.GetValue<string>() ?? "",
        f["name"]?.GetValue<string>() ?? "",
        f["mimeType"]?.GetValue<string>(),
        f["webViewLink"]?.GetValue<string>(),
        f["headRevisionId"]?.GetValue<string>(),
        long.TryParse(f["version"]?.ToString(), out var v) ? v : null);
}
