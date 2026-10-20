using System.IO;
using System.Net;
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
public sealed record DriveFile(string Id, string Name, string? MimeType, string? WebViewLink, string? HeadRevisionId, long? Version,
    IReadOnlyList<string>? Parents = null, DateTime? ModifiedTime = null, bool Trashed = false, long? Size = null,
    IReadOnlyDictionary<string, string>? AppProperties = null);

/// <summary>
/// Le chiamate a Drive che servono a DriveSync. Le prove automatiche (--prova-drive) usano un Drive finto in memoria.
/// </summary>
public interface IDriveClient
{
    Task<DriveFile> CreateFolderAsync(string name, string? parent, CancellationToken ct);

    Task<DriveFile> CreateFileAsync(string name, string mimeType, string? parent, byte[] data, CancellationToken ct,
        IDictionary<string, string>? appProperties = null);

    /// <summary>
    /// Nuovo contenuto (e, se <paramref name="newName"/> non è null, nuovo nome) per un file esistente. Nelle
    /// <paramref name="appProperties"/> un valore null toglie la chiave.
    /// </summary>
    Task<DriveFile> UpdateContentAsync(string id, string mimeType, byte[] data, CancellationToken ct,
        IDictionary<string, string?>? appProperties = null, string? newName = null, IProgress<double>? progress = null);

    /// <summary>Cambia solo le appProperties (un valore null toglie la chiave): niente revisione nuova del contenuto.</summary>
    Task<DriveFile> UpdateMetadataAsync(string id, IDictionary<string, string?> appProperties, CancellationToken ct);

    Task<DriveFile> GetAsync(string id, CancellationToken ct);

    Task<byte[]> DownloadAsync(string id, CancellationToken ct, IProgress<double>? progress = null);

    Task<List<DriveFile>> ListAsync(string q, CancellationToken ct);

    Task<DriveFile> TrashAsync(string id, CancellationToken ct);
}

/// <summary>Chiamate essenziali alle API di Drive v3 con HttpClient (niente librerie esterne).</summary>
public sealed class DriveApi : IDriveClient
{
    public const string FolderMime = "application/vnd.google-apps.folder";
    const string Api = "https://www.googleapis.com/drive/v3/files";
    const string Upload = "https://www.googleapis.com/upload/drive/v3/files";
    const string Fields = "id,name,mimeType,webViewLink,headRevisionId,version,parents,modifiedTime,trashed,size,appProperties";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>Manda una richiesta autenticata (la richiesta si costruisce con una funzione: con un 401 si rifà).</summary>
    readonly Func<Func<HttpRequestMessage>, HttpCompletionOption, CancellationToken, Task<HttpResponseMessage>> _send;

    /// <summary>Gettone fisso (prova della fase 0).</summary>
    public DriveApi(Func<string> accessToken)
    {
        _send = (make, option, ct) =>
        {
            var req = make();
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken());
            return Http.SendAsync(req, option, ct);
        };
    }

    /// <summary>Con l'account di Ardesia: gettone rinnovato da sé, anche dopo un 401.</summary>
    public DriveApi(GoogleAccountService account)
    {
        _send = (make, option, ct) => account.SendAsync(make, ct, Http, option);
    }

    /// <summary>Sopra questa misura il caricamento è «riprendibile», a pezzi (blocchi con immagini, rete lenta della scuola).</summary>
    public int ResumableThreshold { get; set; } = 5 * 1024 * 1024;

    /// <summary>Misura dei pezzi del caricamento riprendibile (multiplo di 256 KB, come vuole Google).</summary>
    public int ChunkSize { get; set; } = 2 * 1024 * 1024;

    public Task<DriveFile> CreateFolderAsync(string name, string? parent, CancellationToken ct) =>
        SendFileAsync(HttpMethod.Post, $"{Api}?fields={Fields}", () => Json(Metadata(name, FolderMime, parent)), ct);

    /// <summary>Nuovo file con contenuto (caricamento «multipart»: metadati e dati in una sola richiesta).</summary>
    public Task<DriveFile> CreateFileAsync(string name, string mimeType, string? parent, byte[] data, CancellationToken ct,
        IDictionary<string, string>? appProperties = null)
    {
        var meta = Metadata(name, mimeType, parent, appProperties?.Select(p => KeyValuePair.Create(p.Key, (string?)p.Value)));
        return data.Length > ResumableThreshold
            ? ResumableAsync(HttpMethod.Post, Upload, meta, mimeType, data, ct, null)
            : SendFileAsync(HttpMethod.Post, $"{Upload}?uploadType=multipart&fields={Fields}", () => Multipart(meta, mimeType, data), ct);
    }

    /// <summary>Sostituisce il contenuto di un file esistente (stesso ID, quindi stesso link e stesse condivisioni).</summary>
    public Task<DriveFile> UpdateContentAsync(string id, string mimeType, byte[] data, CancellationToken ct,
        IDictionary<string, string?>? appProperties = null, string? newName = null, IProgress<double>? progress = null)
    {
        var meta = Metadata(newName, null, null, appProperties);
        return data.Length > ResumableThreshold
            ? ResumableAsync(HttpMethod.Patch, $"{Upload}/{id}", meta, mimeType, data, ct, progress)
            : SendFileAsync(HttpMethod.Patch, $"{Upload}/{id}?uploadType=multipart&fields={Fields}", () => Multipart(meta, mimeType, data), ct);
    }

    public Task<DriveFile> UpdateMetadataAsync(string id, IDictionary<string, string?> appProperties, CancellationToken ct) =>
        SendFileAsync(HttpMethod.Patch, $"{Api}/{id}?fields={Fields}", () => Json(Metadata(null, null, null, appProperties)), ct);

    public Task<DriveFile> GetAsync(string id, CancellationToken ct) =>
        SendFileAsync(HttpMethod.Get, $"{Api}/{id}?fields={Fields}", null, ct);

    public async Task<byte[]> DownloadAsync(string id, CancellationToken ct, IProgress<double>? progress = null)
    {
        using var r = await SendAsync(HttpMethod.Get, $"{Api}/{id}?alt=media", null, ct, HttpCompletionOption.ResponseHeadersRead);
        var total = r.Content.Headers.ContentLength;
        await using var s = await r.Content.ReadAsStreamAsync(ct);
        using var ms = total is > 0 and < int.MaxValue ? new MemoryStream((int)total.Value) : new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (total is > 0) progress?.Report((double)ms.Length / total.Value);
        }
        return ms.ToArray();
    }

    /// <summary>Elenco (tutte le pagine) con una ricerca di Drive (es. «mimeType='…' and trashed=false»).</summary>
    public async Task<List<DriveFile>> ListAsync(string q, CancellationToken ct)
    {
        var all = new List<DriveFile>();
        string? page = null;
        do
        {
            var url = $"{Api}?q={Uri.EscapeDataString(q)}&fields={Uri.EscapeDataString($"nextPageToken,files({Fields})")}" +
                      "&pageSize=200&orderBy=modifiedTime%20desc" + (page != null ? "&pageToken=" + Uri.EscapeDataString(page) : "");
            using var r = await SendAsync(HttpMethod.Get, url, null, ct);
            var json = JsonNode.Parse(await r.Content.ReadAsStringAsync(ct));
            if (json?["files"] is JsonArray files) all.AddRange(files.Select(f => ToFile(f!)));
            page = json?["nextPageToken"]?.GetValue<string>();
        }
        while (page != null);
        return all;
    }

    /// <summary>Lettura per chi, nel dominio, ha il link (il file non compare nelle ricerche degli altri).</summary>
    public async Task ShareWithDomainAsync(string id, string domain, CancellationToken ct)
    {
        var body = new JsonObject { ["type"] = "domain", ["domain"] = domain, ["role"] = "reader", ["allowFileDiscovery"] = false };
        using var _ = await SendAsync(HttpMethod.Post, $"{Api}/{id}/permissions?sendNotificationEmail=false", () => Json(body), ct);
    }

    /// <summary>Nel cestino (non cancellato: si recupera da Drive per 30 giorni).</summary>
    public Task<DriveFile> TrashAsync(string id, CancellationToken ct) =>
        SendFileAsync(HttpMethod.Patch, $"{Api}/{id}?fields={Fields}", () => Json(new JsonObject { ["trashed"] = true }), ct);

    /// <summary>
    /// Caricamento riprendibile: si apre una sessione, poi si mandano i pezzi. Se un pezzo non arriva (rete che cade)
    /// si chiede a Google fin dove è arrivato e si riparte da lì, qualche volta; poi l'errore passa alla coda di DriveSync.
    /// </summary>
    async Task<DriveFile> ResumableAsync(HttpMethod method, string url, JsonNode meta, string mimeType, byte[] data, CancellationToken ct,
        IProgress<double>? progress)
    {
        Uri session;
        using (var start = await SendAsync(method, $"{url}?uploadType=resumable&fields={Fields}", () => Json(meta), ct,
                   extraHeaders: h =>
               {
                   h.Add("X-Upload-Content-Type", mimeType);
                   h.Add("X-Upload-Content-Length", data.Length.ToString());
               }))
        {
            session = start.Headers.Location ?? throw new DriveException(0, "noSession", "Google non ha aperto la sessione di caricamento.");
        }

        long sent = 0;
        int failures = 0;
        while (true)
        {
            try
            {
                int len = (int)Math.Min(ChunkSize, data.Length - sent);
                long from = sent;
                using var r = await _send(() =>
                {
                    var content = new ByteArrayContent(data, (int)from, len);
                    content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
                    content.Headers.ContentRange = len > 0
                        ? new ContentRangeHeaderValue(from, from + len - 1, data.Length)
                        : new ContentRangeHeaderValue(data.Length);
                    return new HttpRequestMessage(HttpMethod.Put, session) { Content = content };
                }, HttpCompletionOption.ResponseContentRead, ct);
                if (r.IsSuccessStatusCode) return ToFile(JsonNode.Parse(await r.Content.ReadAsStringAsync(ct))!);
                if ((int)r.StatusCode == 308)
                {
                    sent = ReceivedUpTo(r);
                    failures = 0;
                    progress?.Report((double)sent / data.Length);
                    continue;
                }
                await ThrowAsync(r, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && failures < 4 && IsTransient(ex))
            {
                failures++;
                await Task.Delay(TimeSpan.FromSeconds(2 * failures), ct);
                sent = await QueryReceivedAsync(session, data.Length, ct);
            }
        }
    }

    /// <summary>Quanti byte ha già ricevuto Google in una sessione riprendibile.</summary>
    async Task<long> QueryReceivedAsync(Uri session, long total, CancellationToken ct)
    {
        using var r = await _send(() =>
        {
            var content = new ByteArrayContent(Array.Empty<byte>());
            content.Headers.ContentRange = new ContentRangeHeaderValue(total);
            return new HttpRequestMessage(HttpMethod.Put, session) { Content = content };
        }, HttpCompletionOption.ResponseContentRead, ct);
        if ((int)r.StatusCode == 308) return ReceivedUpTo(r);
        if (r.IsSuccessStatusCode) return total;
        await ThrowAsync(r, ct);
        return 0;
    }

    static long ReceivedUpTo(HttpResponseMessage r) =>
        r.Headers.TryGetValues("Range", out var values) && values.FirstOrDefault() is { } range
            && long.TryParse(range[(range.IndexOf('-') + 1)..], out var last)
            ? last + 1
            : 0;

    /// <summary>Errori per cui vale la pena riprovare: rete, tempo scaduto, Google occupato o con troppe richieste.</summary>
    public static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException or IOException or TaskCanceledException => true,
        DriveException d => d.Status is 0 or 408 or 429 or >= 500 || d.Reason is "userRateLimitExceeded" or "rateLimitExceeded",
        _ => false,
    };

    async Task<DriveFile> SendFileAsync(HttpMethod method, string url, Func<HttpContent>? content, CancellationToken ct)
    {
        using var r = await SendAsync(method, url, content, ct);
        return ToFile(JsonNode.Parse(await r.Content.ReadAsStringAsync(ct))!);
    }

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Func<HttpContent>? content, CancellationToken ct,
        HttpCompletionOption option = HttpCompletionOption.ResponseContentRead, Action<HttpRequestHeaders>? extraHeaders = null)
    {
        var r = await _send(() =>
        {
            var req = new HttpRequestMessage(method, url) { Content = content?.Invoke() };
            extraHeaders?.Invoke(req.Headers);
            return req;
        }, option, ct);
        if (r.IsSuccessStatusCode) return r;
        await ThrowAsync(r, ct);
        return r; // non si arriva qui
    }

    static async Task ThrowAsync(HttpResponseMessage r, CancellationToken ct)
    {
        var body = await r.Content.ReadAsStringAsync(ct);
        var status = (int)r.StatusCode;
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
        catch (InvalidOperationException)
        {
            // «error» non è un oggetto (es. risposta di OAuth)
        }
        if (status == (int)HttpStatusCode.Unauthorized && message.Length == 0) message = "Accesso a Google non valido.";
        throw new DriveException(status, reason, message);
    }

    static JsonObject Metadata(string? name, string? mimeType, string? parent, IEnumerable<KeyValuePair<string, string?>>? appProperties = null)
    {
        var m = new JsonObject();
        if (name != null) m["name"] = name;
        if (mimeType != null) m["mimeType"] = mimeType;
        if (parent != null) m["parents"] = new JsonArray(parent);
        if (appProperties != null)
        {
            var p = new JsonObject();
            foreach (var (k, v) in appProperties) p[k] = v; // null → JSON null: Drive toglie la chiave
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
        long.TryParse(f["version"]?.ToString(), out var v) ? v : null,
        f["parents"] is JsonArray parents ? parents.Select(p => p!.GetValue<string>()).ToList() : null,
        f["modifiedTime"]?.GetValue<string>() is { } m && DateTime.TryParse(m, null, System.Globalization.DateTimeStyles.RoundtripKind, out var mt)
            ? mt.ToLocalTime() : null,
        f["trashed"]?.GetValue<bool>() ?? false,
        long.TryParse(f["size"]?.ToString(), out var s) ? s : null,
        f["appProperties"] is JsonObject props
            ? props.Where(p => p.Value != null).ToDictionary(p => p.Key, p => p.Value!.ToString())
            : null);
}
