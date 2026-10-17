using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ardesia.Services.Google;

/// <summary>Gettoni ricevuti da Google dopo l'accesso.</summary>
public sealed class GoogleTokens
{
    public required string AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string Scope { get; set; } = "";
    public string? IdToken { get; set; }

    public bool HasScope(string scope) => Scope.Split(' ').Contains(scope);
}

/// <summary>Dati dell'account letti dall'id_token.</summary>
public sealed record GoogleAccount(string Email, string? Name, string? HostedDomain);

/// <summary>Errore restituito da Google (codice OAuth come «access_denied», «invalid_grant»…).</summary>
public sealed class GoogleAuthException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Accesso OAuth 2.0 per app desktop: browser di sistema, ritorno su 127.0.0.1 (porta scelta dal sistema) e PKCE.
/// Il ritorno si riceve con un TcpListener e non con HttpListener, che passa da http.sys e senza amministratore
/// può chiedere la prenotazione dell'indirizzo.
/// </summary>
public static class GoogleAuth
{
    public const string DriveFileScope = "https://www.googleapis.com/auth/drive.file";
    public const string DefaultScopes = "openid email profile " + DriveFileScope;
    const string RevokeUri = "https://oauth2.googleapis.com/revoke";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>Apre il browser sulla pagina di accesso di Google e aspetta il ritorno.</summary>
    public static async Task<GoogleTokens> SignInAsync(GoogleClientConfig cfg, CancellationToken ct,
        string scopes = DefaultScopes, string? hostedDomain = GoogleClientConfig.SchoolDomain)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            var query = new Dictionary<string, string?>
            {
                ["client_id"] = cfg.ClientId,
                ["redirect_uri"] = redirect,
                ["response_type"] = "code",
                ["scope"] = scopes,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
                ["state"] = state,
                ["access_type"] = "offline",
                ["prompt"] = "select_account consent",
                ["hd"] = hostedDomain,
            };
            var url = cfg.AuthUri + "?" + string.Join("&", query.Where(p => p.Value != null)
                .Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value!)));
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            var answer = await WaitForRedirectAsync(listener, state, ct);
            if (answer.TryGetValue("error", out var error))
                throw new GoogleAuthException(error, error == "access_denied"
                    ? "L'accesso è stato annullato o negato nella pagina di Google."
                    : $"Google ha risposto con l'errore «{error}».");
            if (!answer.TryGetValue("code", out var code))
                throw new GoogleAuthException("no_code", "Google non ha restituito il codice di accesso.");

            return await TokenRequestAsync(cfg, new()
            {
                ["code"] = code,
                ["client_id"] = cfg.ClientId,
                ["client_secret"] = cfg.ClientSecret,
                ["redirect_uri"] = redirect,
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = verifier,
            }, ct);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Nuovo access token dal refresh token (gli access token durano un'ora).</summary>
    public static async Task<GoogleTokens> RefreshAsync(GoogleClientConfig cfg, string refreshToken, CancellationToken ct)
    {
        var t = await TokenRequestAsync(cfg, new()
        {
            ["refresh_token"] = refreshToken,
            ["client_id"] = cfg.ClientId,
            ["client_secret"] = cfg.ClientSecret,
            ["grant_type"] = "refresh_token",
        }, ct);
        t.RefreshToken ??= refreshToken;
        return t;
    }

    /// <summary>Revoca l'accesso (pulsante «Esci»). Revocare il refresh token invalida anche gli access token.</summary>
    public static async Task RevokeAsync(string token, CancellationToken ct)
    {
        using var r = await Http.PostAsync(RevokeUri, new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }), ct);
        if (!r.IsSuccessStatusCode)
            throw new GoogleAuthException("revoke_failed", $"Revoca non riuscita ({(int)r.StatusCode}).");
    }

    /// <summary>E-mail, nome e dominio dall'id_token (arriva direttamente da Google via HTTPS: non serve verificarne la firma).</summary>
    public static GoogleAccount? ReadAccount(GoogleTokens t)
    {
        var parts = t.IdToken?.Split('.');
        if (parts is not { Length: 3 }) return null;
        try
        {
            var s = parts[1].Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(s));
            var root = doc.RootElement;
            string? Get(string n) => root.TryGetProperty(n, out var v) ? v.GetString() : null;
            return Get("email") is { } email ? new GoogleAccount(email, Get("name"), Get("hd")) : null;
        }
        catch
        {
            return null;
        }
    }

    static async Task<GoogleTokens> TokenRequestAsync(GoogleClientConfig cfg, Dictionary<string, string> form, CancellationToken ct)
    {
        using var r = await Http.PostAsync(cfg.TokenUri, new FormUrlEncodedContent(form), ct);
        var body = await r.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body.Length > 0 ? body : "{}");
        var root = doc.RootElement;
        string? Get(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (!r.IsSuccessStatusCode || Get("access_token") is not { } access)
        {
            var err = Get("error") ?? ((int)r.StatusCode).ToString();
            throw new GoogleAuthException(err, $"Google ha rifiutato la richiesta del gettone: {err}" +
                (Get("error_description") is { } d ? $" ({d})" : ""));
        }
        return new GoogleTokens
        {
            AccessToken = access,
            RefreshToken = Get("refresh_token"),
            ExpiresAt = DateTime.UtcNow.AddSeconds(root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600),
            Scope = Get("scope") ?? "",
            IdToken = Get("id_token"),
        };
    }

    /// <summary>
    /// Aspetta la richiesta del browser «GET /?code=…&amp;state=…» e risponde con una paginetta.
    /// Le altre richieste (es. /favicon.ico) ricevono 404.
    /// </summary>
    static async Task<Dictionary<string, string>> WaitForRedirectAsync(TcpListener listener, string state, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            using var stream = client.GetStream();
            var target = await ReadRequestTargetAsync(stream, ct);
            var q = ParseQuery(target);
            if (!q.ContainsKey("code") && !q.ContainsKey("error"))
            {
                await RespondAsync(stream, "404 Not Found", "", ct);
                continue;
            }
            if (!q.TryGetValue("state", out var s) || s != state)
            {
                await RespondAsync(stream, "400 Bad Request", Page("Richiesta non valida", "Torna ad Ardesia e riprova."), ct);
                continue;
            }
            await RespondAsync(stream, "200 OK", q.ContainsKey("code")
                ? Page("Accesso riuscito", "Puoi chiudere questa scheda e tornare ad Ardesia.")
                : Page("Accesso non riuscito", "Torna ad Ardesia per i dettagli."), ct);
            return q;
        }
    }

    static async Task<string> ReadRequestTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var buffer = new byte[8192];
        int n = 0;
        try
        {
            while (n < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(n), timeout.Token);
                if (read == 0) break;
                n += read;
                if (Encoding.ASCII.GetString(buffer, 0, n).Contains("\r\n")) break; // basta la prima riga
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // il browser ha aperto la connessione senza mandare nulla
        }
        var line = Encoding.ASCII.GetString(buffer, 0, n).Split("\r\n")[0];
        var parts = line.Split(' ');
        return parts.Length >= 2 && parts[0] == "GET" ? parts[1] : "";
    }

    static Dictionary<string, string> ParseQuery(string target)
    {
        var result = new Dictionary<string, string>();
        int i = target.IndexOf('?');
        if (i < 0) return result;
        foreach (var pair in target[(i + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            result[Uri.UnescapeDataString(kv[0].Replace('+', ' '))] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
        }
        return result;
    }

    static async Task RespondAsync(NetworkStream stream, string status, string html, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\n" +
                                           $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(body, ct);
    }

    static string Page(string title, string text) =>
        "<!doctype html><html lang=\"it\"><meta charset=\"utf-8\"><title>Ardesia</title>" +
        "<body style=\"font-family:Segoe UI,sans-serif;text-align:center;margin-top:15vh;color:#222\">" +
        $"<h1 style=\"color:#1E5A68\">{WebUtility.HtmlEncode(title)}</h1><p style=\"font-size:1.2em\">{WebUtility.HtmlEncode(text)}</p></body></html>";

    static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
