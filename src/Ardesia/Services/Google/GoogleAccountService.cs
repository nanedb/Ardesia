using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Ardesia.Services.Google;

/// <summary>
/// L'account Google del docente su questo PC: accesso col browser, refresh token cifrato (TokenStore),
/// access token solo in memoria e rinnovato da sé (alla scadenza o alla prima risposta 401), «Esci».
/// Ogni docente ha il suo utente Windows (docs/google-drive.md §5): l'accesso si ricorda, senza uscita per inattività.
/// </summary>
public sealed class GoogleAccountService
{
    static GoogleAccountService? _current;

    /// <summary>Il servizio dell'app (legge l'account salvato la prima volta che serve).</summary>
    public static GoogleAccountService Current => _current ??= new GoogleAccountService();

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    readonly SemaphoreSlim _refreshLock = new(1, 1);
    StoredAccount? _stored;
    GoogleTokens? _tokens;
    GoogleClientConfig? _config;
    string? _configError;

    /// <summary>Cambia l'account (accesso, uscita, accesso scaduto). Può arrivare da un thread qualsiasi.</summary>
    public event EventHandler? Changed;

    /// <param name="clientFile">File del client OAuth; null = lo cerca accanto all'exe e in %AppData%\Ardesia.</param>
    public GoogleAccountService(string? clientFile = null)
    {
        _stored = TokenStore.Load();
        try
        {
            _config = GoogleClientConfig.Load(clientFile);
        }
        catch (Exception ex)
        {
            _configError = ex.Message;
        }
    }

    /// <summary>Vero se c'è il file del client Google, senza il quale non si può accedere.</summary>
    public bool IsConfigured => _config != null;

    /// <summary>Perché manca la configurazione (da mostrare nelle impostazioni).</summary>
    public string? ConfigError => _configError;

    public GoogleClientConfig? Config => _config;

    public bool IsSignedIn => _stored != null;

    public string? Email => _stored?.Email;

    /// <summary>Nome da mostrare: il nome dell'account o, se manca, l'e-mail.</summary>
    public string? DisplayName => _stored == null ? null : string.IsNullOrWhiteSpace(_stored.Name) ? _stored.Email : _stored.Name;

    public DateTime? SignedInAt => _stored?.SignedInAt;

    /// <summary>Scadenza dell'access token in memoria (ora locale), o null se non c'è ancora.</summary>
    public DateTime? AccessTokenExpiresAt => _tokens?.ExpiresAt.ToLocalTime();

    /// <summary>
    /// Accesso col browser predefinito. Controlla che l'account sia della scuola e che sia stato concesso
    /// il permesso su Drive; poi salva l'account cifrato.
    /// </summary>
    public async Task SignInAsync(CancellationToken ct)
    {
        var cfg = _config ?? throw new InvalidOperationException(_configError ?? "Manca il file del client Google.");
        var t = await GoogleAuth.SignInAsync(cfg, ct);
        var account = GoogleAuth.ReadAccount(t);
        string? problem = null;
        if (account == null)
            problem = "Google non ha indicato l'account con cui hai fatto l'accesso.";
        else if (!account.Email.EndsWith("@" + GoogleClientConfig.SchoolDomain, StringComparison.OrdinalIgnoreCase))
            problem = $"Hai fatto l'accesso con {account.Email}: serve l'account della scuola (@{GoogleClientConfig.SchoolDomain}).";
        else if (!t.HasScope(GoogleAuth.DriveFileScope))
            problem = "Nella pagina di Google non è stato concesso il permesso su Google Drive. " +
                      "Riprova e lascia spuntata la casella che riguarda i file di Drive usati da Ardesia.";
        else if (t.RefreshToken == null)
            problem = "Google non ha restituito il gettone per ricordare l'accesso. Riprova.";
        if (problem != null)
        {
            try { await GoogleAuth.RevokeAsync(t.RefreshToken ?? t.AccessToken, CancellationToken.None); } catch { /* resta scaduto da sé */ }
            throw new GoogleAuthException("not_allowed", problem);
        }

        var stored = new StoredAccount
        {
            RefreshToken = t.RefreshToken!,
            Email = account!.Email,
            Name = account.Name,
            HostedDomain = account.HostedDomain,
            Scope = t.Scope,
            SignedInAt = DateTime.Now,
        };
        TokenStore.Save(stored);
        _stored = stored;
        _tokens = t;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Un access token valido (almeno un minuto di vita), rinnovato se serve.
    /// Se Google non accetta più il refresh token (accesso revocato da myaccount.google.com, password cambiata…)
    /// l'account viene dimenticato e si lancia GoogleAuthException con codice «invalid_grant».
    /// </summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct, bool forceRefresh = false)
    {
        var known = _tokens;
        if (!forceRefresh && known != null && known.ExpiresAt > DateTime.UtcNow.AddMinutes(1)) return known.AccessToken;

        await _refreshLock.WaitAsync(ct);
        try
        {
            // Un'altra chiamata può averlo appena rinnovato mentre si aspettava.
            if (_tokens != null && _tokens != known && _tokens.ExpiresAt > DateTime.UtcNow.AddMinutes(1)) return _tokens.AccessToken;

            var stored = _stored ?? throw new GoogleAuthException("not_signed_in", "Non hai ancora fatto l'accesso a Google.");
            var cfg = _config ?? throw new InvalidOperationException(_configError ?? "Manca il file del client Google.");
            try
            {
                var t = await GoogleAuth.RefreshAsync(cfg, stored.RefreshToken, ct);
                if (t.RefreshToken != stored.RefreshToken && t.RefreshToken != null)
                {
                    stored.RefreshToken = t.RefreshToken; // di solito Google non lo cambia, ma può succedere
                    TokenStore.Save(stored);
                }
                _tokens = t;
                return t.AccessToken;
            }
            catch (GoogleAuthException ex) when (ex.Code == "invalid_grant")
            {
                Forget();
                throw new GoogleAuthException("invalid_grant",
                    "L'accesso a Google non è più valido (revocato o scaduto): accedi di nuovo.");
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Manda una richiesta autenticata. Con 401 rinnova il gettone e riprova una volta; per questo la richiesta
    /// si costruisce con una funzione (un HttpRequestMessage non si può mandare due volte).
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> makeRequest, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var token = await GetAccessTokenAsync(ct, forceRefresh: attempt > 0);
            var request = makeRequest();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await Http.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0) return response;
            response.Dispose();
        }
    }

    /// <summary>
    /// «Esci»: revoca l'accesso su Google e cancella l'account da questo PC. Se la revoca non riesce
    /// (per esempio senza rete) l'account si dimentica lo stesso e si restituisce un avviso da mostrare.
    /// </summary>
    public async Task<string?> SignOutAsync(CancellationToken ct)
    {
        var stored = _stored;
        if (stored == null) return null;
        string? warning = null;
        try
        {
            await GoogleAuth.RevokeAsync(stored.RefreshToken, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            warning = "Ardesia ha dimenticato l'account su questo PC, ma non è riuscita ad avvisare Google " +
                      "(forse manca la connessione). Se vuoi, togli l'accesso di Ardesia da myaccount.google.com › Sicurezza.";
        }
        Forget();
        return warning;
    }

    /// <summary>Solo per --prova-accesso: guasta l'access token in memoria per provare il rinnovo dopo un 401.</summary>
    internal void SpoilAccessTokenForTest()
    {
        if (_tokens != null) _tokens.AccessToken = "gettone-guasto";
    }

    void Forget()
    {
        TokenStore.Delete();
        _stored = null;
        _tokens = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
