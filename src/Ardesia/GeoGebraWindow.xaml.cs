using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Ardesia;

/// <summary>
/// GeoGebra Classic in una finestra a parte, caricato da geogebra.org dentro WebView2.
/// Restituisce l'immagine del grafico e la costruzione, per inserirla nella pagina e riaprirla in seguito.
/// </summary>
public partial class GeoGebraWindow : Window
{
    const string Ready = "Scrivi le funzioni a sinistra (per esempio y = x^2 - 3x + 2) e sistema il grafico, poi premi «Inserisci nella pagina».";

    const string PageUrl = "https://ardesia.local/geogebra.html";

    readonly string? _state;
    readonly IReadOnlyList<string> _commands;

    /// <summary>Immagine PNG del grafico (vista grafica di GeoGebra).</summary>
    public byte[]? Png { get; private set; }

    /// <summary>Costruzione GeoGebra (.ggb in Base64).</summary>
    public string? State { get; private set; }

    /// <summary>Quale app di GeoGebra: "classic" (GeoGebra Classic, scelta dall'utente: interfaccia più pulita)
    /// o "graphing" (Calcolatrice grafica).</summary>
    public string AppName { get; init; } = "classic";

    /// <summary>Solo per le prove (--prova-geogebra): file in cui scrivere cosa succede.</summary>
    public string? TraceFile { get; init; }

    /// <summary>Solo per le prove: dopo quanti secondi registrare lo stato della pagina e chiudere (0 = mai).</summary>
    public int AutoCloseSeconds { get; init; }

    static string Shorten(string s) => s.Length > 600 ? s[..600] + "…" : s;

    void Trace(string message)
    {
        if (TraceFile == null) return;
        try
        {
            File.AppendAllText(TraceFile, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // registro non scrivibile
        }
    }

    /// <param name="state">Costruzione da riaprire, oppure null per un grafico nuovo.</param>
    /// <param name="formulas">Testo da cui prendere le funzioni (una per riga), per un grafico nuovo.</param>
    /// <param name="parameters">Valori dei parametri (dal grafico di Ardesia), o null: valgono 1.</param>
    public GeoGebraWindow(string? state, string? formulas, IReadOnlyList<Editor.Graph.GraphParameter>? parameters = null)
    {
        InitializeComponent();
        _state = string.IsNullOrEmpty(state) ? null : state;
        _commands = _state == null ? ToCommands(formulas, parameters) : Array.Empty<string>();

        // Grande, ma mai più grande dello schermo (sulle LIM il ridimensionamento è alto).
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1400, area.Width * 0.92);
        Height = Math.Min(900, area.Height * 0.92);

        Loaded += async (_, _) => await StartAsync();
    }

    /// <summary>Trasforma il testo di una casella in comandi GeoGebra: prima i cursori dei parametri, poi una riga per funzione.</summary>
    public static IReadOnlyList<string> ToCommands(string? text, IReadOnlyList<Editor.Graph.GraphParameter>? parameters = null) =>
        Editor.Graph.GeoGebraExport.Commands(Editor.Graph.MathText.Lines(text), parameters);

    async Task StartAsync()
    {
        Trace("finestra caricata");
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ardesia", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, folder);
            Trace($"ambiente WebView2 creato, versione {env.BrowserVersionString}, dati in {folder}");
            await Web.EnsureCoreWebView2Async(env);
            Trace("CoreWebView2 pronto");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Trace("runtime WebView2 mancante");
            Hint.Text = "Per usare GeoGebra serve «Microsoft Edge WebView2 Runtime», che su questo computer manca. " +
                        "Si scarica gratis dal sito Microsoft (cerca «WebView2 Runtime»).";
            return;
        }
        catch (Exception ex)
        {
            Trace("errore all'avvio: " + ex);
            Hint.Text = "Non riesco ad avviare GeoGebra: " + ex.Message;
            return;
        }

        var core = Web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        // I link (guida, sito GeoGebra…) si aprono nel browser, non dentro la finestra.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenInBrowser(e.Uri);
        };
        if (TraceFile != null)
        {
            // Console e errori JavaScript della pagina nel registro.
            await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
            core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled").DevToolsProtocolEventReceived +=
                (_, e) => Trace("console: " + Shorten(e.ParameterObjectAsJson));
            core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown").DevToolsProtocolEventReceived +=
                (_, e) => Trace("errore JavaScript: " + Shorten(e.ParameterObjectAsJson));
        }
        core.NavigationCompleted += (_, e) => Trace($"navigazione completata: riuscita={e.IsSuccess}, stato={e.WebErrorStatus}");
        core.ProcessFailed += (_, e) => Trace($"processo WebView2 fallito: {e.ProcessFailedKind} {e.Reason}");
        core.WebMessageReceived += (_, e) =>
        {
            var message = e.TryGetWebMessageAsString();
            Trace("messaggio dalla pagina: " + message);
            switch (message)
            {
                case "ready":
                    InsertButton.IsEnabled = true;
                    Hint.Text = Ready;
                    break;
                case "offline":
                    Hint.Text = "Non riesco a caricare GeoGebra: serve la connessione a internet.";
                    break;
            }
        };
        // La pagina deve avere un indirizzo https vero: con NavigateToString l'origine è «opaca»
        // e GeoGebra si blocca all'avvio leggendo i cookie. La serviamo noi, senza scrivere file.
        var html = BuildPage();
        core.AddWebResourceRequestedFilter(PageUrl, CoreWebView2WebResourceContext.Document);
        core.WebResourceRequested += (_, e) =>
        {
            if (e.Request.Uri != PageUrl) return;
            var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html));
            e.Response = core.Environment.CreateWebResourceResponse(stream, 200, "OK", "Content-Type: text/html; charset=utf-8");
        };
        core.Navigate(PageUrl);
        Trace("pagina richiesta");

        if (AutoCloseSeconds > 0)
        {
            await Task.Delay(AutoCloseSeconds * 1000);
            var page = await core.ExecuteScriptAsync(
                "JSON.stringify({ testo: document.body ? document.body.innerText.slice(0, 200) : null, " +
                "ggbApplet: typeof GGBApplet, api: typeof ggb, size: [innerWidth, innerHeight], " +
                "oggetti: typeof ggb === 'object' ? ggb.getAllObjectNames() : null, " +
                "tastiera: document.querySelector('.TabbedKeyBoard') ? document.querySelector('.TabbedKeyBoard').offsetHeight : 0, " +
                "divisore: document.querySelector('.gwt-SplitLayoutPanel-HDragger') ? Math.round(document.querySelector('.gwt-SplitLayoutPanel-HDragger').getBoundingClientRect().x) : null })");
            Trace("stato della pagina: " + page);
            Trace("esportazione: " + await core.ExecuteScriptAsync(
                "typeof ggb === 'object' ? JSON.stringify({ png: ggb.getPNGBase64(2, false, 144).length, ggb: ggb.getBase64().length }) : null"));
            Trace($"dimensioni del controllo: {Web.ActualWidth}×{Web.ActualHeight}, pulsante attivo={InsertButton.IsEnabled}");
            // Immagine della finestra di GeoGebra, accanto al registro.
            using (var shot = File.Create(Path.ChangeExtension(TraceFile!, ".png")))
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, shot);
            Close();
        }
    }

    static void OpenInBrowser(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http")) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // nessun browser predefinito
        }
    }

    string BuildPage()
    {
        var state = JsonSerializer.Serialize(_state);
        var commands = JsonSerializer.Serialize(_commands);
        return $$"""
            <!doctype html>
            <html><head><meta charset="utf-8">
            <style>
              html, body { margin: 0; height: 100%; overflow: hidden; font-family: 'Segoe UI', sans-serif; }
              #ggb { width: 100%; height: 100%; }
              .msg { padding: 40px; font-size: 18px; color: #555; }
            </style>
            </head><body>
            <div id="ggb"><p class="msg">Caricamento di GeoGebra…</p></div>
            <script>
              const state = {{state}};
              const commands = {{commands}};
              function offline() {
                document.getElementById('ggb').innerHTML =
                  '<p class="msg">Non riesco a caricare GeoGebra: serve la connessione a internet.</p>';
                chrome.webview.postMessage('offline');
              }
              // GeoGebra non ha parametri per questo: si simula il gesto sui suoi controlli (se mancano, niente).
              function press(el, x, y, kinds) {
                const o = { bubbles: true, cancelable: true, clientX: x, clientY: y, button: 0, buttons: 1,
                            pointerType: 'mouse', isPrimary: true, view: window };
                for (const k of kinds) el.dispatchEvent(k.startsWith('pointer') ? new PointerEvent(k, o) : new MouseEvent(k, o));
              }
              function closeKeyboard() {
                const b = document.querySelector('.closeTabbedKeyboardButton');
                if (!b || !b.offsetParent) return;
                const r = b.getBoundingClientRect();
                press(b, r.x + r.width / 2, r.y + r.height / 2, ['pointerdown', 'mousedown', 'pointerup', 'mouseup', 'click']);
              }
              function narrowAlgebra() {
                const d = document.querySelector('.gwt-SplitLayoutPanel-HDragger');
                if (!d) return;
                const r = d.getBoundingClientRect(), y = r.y + r.height / 2;
                const target = Math.max(260, window.innerWidth * 0.2);
                if (r.x <= target) return;
                press(d, r.x + r.width / 2, y, ['mousedown']);
                press(d, (r.x + target) / 2, y, ['mousemove']);
                press(d, target, y, ['mousemove', 'mouseup']);
              }
              function start() {
                if (typeof GGBApplet === 'undefined') { offline(); return; }
                document.getElementById('ggb').innerHTML = '';
                const params = {
                  appName: '{{AppName}}', {{(AppName == "classic" ? "perspective: 'AG'," : "")}} language: 'it',
                  width: window.innerWidth, height: window.innerHeight,
                  showMenuBar: false, showToolBar: true, showAlgebraInput: true,
                  enableShiftDragZoom: true, showResetIcon: false, useBrowserForJS: false,
                  appletOnLoad: function (api) {
                    window.ggb = api;
                    for (const c of commands) api.evalCommand(c);
                    window.addEventListener('resize', () => api.setSize(window.innerWidth, window.innerHeight));
                    // Più spazio al grafico: tastiera chiusa e pannello Algebra più stretto.
                    setTimeout(() => { closeKeyboard(); narrowAlgebra(); }, 300);
                    chrome.webview.postMessage('ready');
                  }
                };
                if (state) params.ggbBase64 = state;
                new GGBApplet(params, true).inject('ggb');
              }
            </script>
            <script src="https://www.geogebra.org/apps/deployggb.js" onload="start()" onerror="offline()"></script>
            </body></html>
            """;
    }

    async void Insert_Click(object sender, RoutedEventArgs e)
    {
        InsertButton.IsEnabled = false;
        try
        {
            // Immagine al doppio della risoluzione, per restare nitida anche ingrandita sulla LIM.
            var png = JsonSerializer.Deserialize<string>(await Web.CoreWebView2.ExecuteScriptAsync("ggb.getPNGBase64(2, false, 144)"));
            var state = JsonSerializer.Deserialize<string>(await Web.CoreWebView2.ExecuteScriptAsync("ggb.getBase64()"));
            if (string.IsNullOrEmpty(png) || string.IsNullOrEmpty(state))
                throw new InvalidOperationException("GeoGebra non ha restituito il grafico.");
            Png = Convert.FromBase64String(png);
            State = state;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Hint.Text = "Non riesco a prendere il grafico da GeoGebra: " + ex.Message;
            InsertButton.IsEnabled = true;
        }
    }
}
