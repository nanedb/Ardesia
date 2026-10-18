using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ardesia.Services.Google;

namespace Ardesia;

/// <summary>
/// «Prova di Google Drive» (Ardesia.exe --prova-google [client.json]): controlla passo per passo che l'accesso a
/// Google e Drive funzionino con il progetto della scuola, e dice che cosa chiedere all'amministratore.
/// Crea su Drive una cartella di prova e alla fine la mette nel cestino; revoca l'accesso.
/// Non tocca le impostazioni di Ardesia né salva gettoni.
/// </summary>
public sealed class GoogleTestWindow : Window
{
    enum Outcome { Running, Ok, Warning, Failed, Skipped }

    sealed class Step(TextBlock icon, TextBlock title, TextBlock detail)
    {
        public TextBlock Icon { get; } = icon;
        public TextBlock Title { get; } = title;
        public TextBlock Detail { get; } = detail;
        public Outcome Outcome { get; set; } = Outcome.Running;
    }

    readonly string? _clientFile;
    readonly StackPanel _steps = new();
    readonly TextBlock _summary;
    readonly Button _start, _copy, _close;
    readonly StringBuilder _report = new();
    readonly List<string> _forAdmin = new();
    readonly string _logFile = Path.Combine(Path.GetTempPath(), "ardesia-google.log");
    CancellationTokenSource? _cts;

    public GoogleTestWindow(string? clientFile)
    {
        _clientFile = clientFile;
        Title = "Ardesia – Prova di Google Drive";
        Width = 720;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 20;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;
        if (Application.Current.TryFindResource("TextBrush") is Brush fg) Foreground = fg;
        Background = Brushes.White;

        var root = new DockPanel { Margin = new Thickness(26, 20, 26, 20) };

        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "Prova di Google Drive", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        head.Children.Add(new TextBlock
        {
            Text = "Controlla se Ardesia può accedere a Google e a Drive con il progetto della scuola, " +
                   "e che cosa eventualmente chiedere all'amministratore.\n" +
                   $"Si apre il browser: accedi con l'account @{GoogleClientConfig.SchoolDomain}. " +
                   "Su Drive viene creata una cartella «Ardesia – prova», che alla fine va nel cestino. " +
                   "Alla fine l'accesso viene revocato: Ardesia non ricorda nulla.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 14),
        });
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        _copy = MakeButton("Copia il riepilogo", "PlainButton");
        _copy.IsEnabled = false;
        _copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(_report.ToString()); } catch { /* appunti occupati */ }
        };
        _start = MakeButton("Inizia la prova", "AccentButton");
        _start.Click += async (_, _) => await RunAsync();
        _close = MakeButton("Chiudi", "PlainButton");
        _close.Click += (_, _) =>
        {
            if (_cts != null) _cts.Cancel();
            else Close();
        };
        buttons.Children.Add(_copy);
        buttons.Children.Add(_start);
        buttons.Children.Add(_close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        _summary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 15, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(_summary, Dock.Bottom);
        root.Children.Add(_summary);

        root.Children.Add(new ScrollViewer { Content = _steps, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Closing += (_, _) => _cts?.Cancel();
    }

    static Button MakeButton(string text, string style)
    {
        var b = new Button { Content = text, MinWidth = 120, Height = 38, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(8, 0, 0, 0) };
        if (Application.Current.TryFindResource(style) is Style st) b.Style = st;
        return b;
    }

    Step Begin(string title)
    {
        var icon = new TextBlock { Text = "…", Width = 28, FontSize = 18, FontFamily = new FontFamily("Segoe UI Symbol"), Foreground = Brushes.Gray };
        var t = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        var d = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
        var text = new StackPanel();
        text.Children.Add(t);
        text.Children.Add(d);
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        row.Children.Add(icon);
        row.Children.Add(text);
        _steps.Children.Add(row);
        return new Step(icon, t, d);
    }

    void End(Step s, Outcome outcome, string detail, string? adminRequest = null)
    {
        s.Outcome = outcome;
        (s.Icon.Text, s.Icon.Foreground) = outcome switch
        {
            Outcome.Ok => ("✔", (Brush)new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32))),
            Outcome.Warning => ("⚠", new SolidColorBrush(Color.FromRgb(0xE6, 0x8A, 0x00))),
            Outcome.Failed => ("✖", new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28))),
            _ => ("–", Brushes.Gray),
        };
        s.Detail.Text = detail;
        if (adminRequest != null) _forAdmin.Add(adminRequest);
        var mark = outcome switch { Outcome.Ok => "OK", Outcome.Warning => "ATTENZIONE", Outcome.Failed => "NO", _ => "saltato" };
        _report.AppendLine($"[{mark}] {s.Title.Text}");
        if (detail.Length > 0) _report.AppendLine("      " + detail.Replace("\n", "\n      "));
    }

    async Task RunAsync()
    {
        _start.IsEnabled = false;
        _copy.IsEnabled = false;
        _steps.Children.Clear();
        _report.Clear();
        _forAdmin.Clear();
        _summary.Text = "";
        _close.Content = "Interrompi";
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _report.AppendLine($"Prova di Google Drive di Ardesia – {DateTime.Now:dd/MM/yyyy HH:mm}");
        _report.AppendLine();

        GoogleTokens? tokens = null;
        string? folderId = null;
        var drive = new DriveApi(() => tokens!.AccessToken);
        try
        {
            // 1. File del client
            var s = Begin("File del client Google");
            GoogleClientConfig cfg;
            try
            {
                cfg = GoogleClientConfig.Load(_clientFile);
                End(s, Outcome.Ok, $"Progetto «{cfg.ProjectId}», ID client {cfg.ClientId}\n{cfg.SourceFile}");
            }
            catch (Exception ex)
            {
                End(s, Outcome.Failed, ex.Message);
                return;
            }

            // 2. Accesso nel browser
            s = Begin("Accesso a Google nel browser");
            s.Detail.Text = "Si è aperto il browser: scegli l'account della scuola e consenti l'accesso. Poi torna qui.";
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromMinutes(5));
                try
                {
                    tokens = await GoogleAuth.SignInAsync(cfg, wait.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new GoogleAuthException("timeout", "");
                }
                Activate();
                End(s, Outcome.Ok, "Google ha accettato l'app: l'accesso non è bloccato dall'amministratore.");
            }
            catch (OperationCanceledException)
            {
                End(s, Outcome.Failed, "Prova interrotta.");
                return;
            }
            catch (GoogleAuthException ex)
            {
                Activate();
                if (ex.Code is "timeout" or "access_denied")
                {
                    End(s, Outcome.Failed,
                        (ex.Code == "timeout" ? "Nessuna risposta dal browser entro 5 minuti." : ex.Message) + "\n" +
                        "Che cosa diceva la pagina di Google?\n" +
                        "• «Accesso bloccato», «admin_policy_enforced» o «l'amministratore non ha approvato l'app» → serve l'amministratore (vedi sotto).\n" +
                        "• «Errore 403: org_internal» → hai usato un account che non è della scuola: riprova scegliendo quello @" + GoogleClientConfig.SchoolDomain + ".\n" +
                        "• Hai premuto «Annulla» o chiuso la scheda → riprova.",
                        "Rendere attendibile l'app «Ardesia» nella Console di amministrazione: Sicurezza → Accesso e controllo dei dati → " +
                        $"Controlli API → Gestisci l'accesso delle app di terze parti → aggiungi l'ID client {cfg.ClientId} come «Attendibile» " +
                        "(solo se la pagina di Google diceva che l'app è bloccata dall'amministratore).");
                }
                else
                {
                    End(s, Outcome.Failed, ex.Message);
                }
                return;
            }

            // 3. Account
            s = Begin("Account della scuola");
            var account = GoogleAuth.ReadAccount(tokens);
            if (account == null)
                End(s, Outcome.Warning, "Google non ha mandato i dati dell'account.");
            else if (!string.Equals(account.HostedDomain, GoogleClientConfig.SchoolDomain, StringComparison.OrdinalIgnoreCase))
                End(s, Outcome.Failed, $"{account.Email}: non è un account @{GoogleClientConfig.SchoolDomain}. Riprova con quello della scuola.");
            else
                End(s, Outcome.Ok, $"{account.Name} – {account.Email}");

            // 4. Permesso su Drive
            s = Begin("Permesso di usare Drive (solo i file di Ardesia)");
            if (tokens.HasScope(GoogleAuth.DriveFileScope))
            {
                End(s, Outcome.Ok, "Concesso (drive.file).");
            }
            else
            {
                End(s, Outcome.Failed, "Nella pagina di Google non è stata spuntata la casella per Drive. Riprova e lasciala spuntata.");
                return;
            }

            // 5. Gettone di aggiornamento
            s = Begin("Accesso ricordato (gettone di aggiornamento)");
            if (tokens.RefreshToken is not { } refresh)
            {
                End(s, Outcome.Warning, "Google non ha mandato il gettone di aggiornamento: Ardesia dovrebbe chiedere l'accesso ogni ora.");
            }
            else
            {
                try
                {
                    tokens = await GoogleAuth.RefreshAsync(cfg, refresh, ct);
                    End(s, Outcome.Ok, "Ricevuto e rinnovato: Ardesia potrà ricordare l'accesso su questo PC.");
                }
                catch (GoogleAuthException ex)
                {
                    End(s, Outcome.Failed, ex.Message);
                }
            }

            // 6. API di Drive attiva: cartella di prova
            s = Begin("Google Drive API attiva nel progetto");
            try
            {
                folderId = (await drive.CreateFolderAsync("Ardesia – prova", null, ct)).Id;
                End(s, Outcome.Ok, "Creata la cartella «Ardesia – prova» in «Il mio Drive».");
            }
            catch (DriveException ex) when (ex.Reason is "accessNotConfigured" or "SERVICE_DISABLED" || ex.Message.Contains("has not been used", StringComparison.Ordinal))
            {
                End(s, Outcome.Failed, "La Google Drive API non è attiva: nella console del progetto, API e servizi → Libreria → Google Drive API → Abilita. " +
                                       "Poi aspetta qualche minuto e riprova.");
                return;
            }
            catch (DriveException ex)
            {
                End(s, Outcome.Failed, $"Drive ha risposto {ex.Status} {ex.Reason}: {ex.Message}");
                return;
            }

            // 7. Un blocco appunti finto, con il tipo di file di Ardesia
            s = Begin("Creare, elencare e aggiornare un blocco appunti");
            const string mime = "application/vnd.ardesia.notebook";
            try
            {
                var props = new Dictionary<string, string> { ["apertoSu"] = Environment.MachineName };
                var nb = await drive.CreateFileAsync("Prova.ardesia", mime, folderId, Encoding.UTF8.GetBytes("prova 1"), ct, props);
                var list = await drive.ListAsync($"mimeType='{mime}' and trashed=false and '{folderId}' in parents", ct);
                var before = nb.HeadRevisionId;
                var updated = await drive.UpdateContentAsync(nb.Id, mime, Encoding.UTF8.GetBytes("prova 2"), ct);
                var back = Encoding.UTF8.GetString(await drive.DownloadAsync(nb.Id, ct));
                if (!list.Any(f => f.Id == nb.Id))
                    End(s, Outcome.Failed, "Il file creato non compare nell'elenco.");
                else if (back != "prova 2" || updated.HeadRevisionId == before)
                    End(s, Outcome.Failed, "L'aggiornamento del contenuto non è riuscito.");
                else
                    End(s, Outcome.Ok, "Creato, trovato nell'elenco, aggiornato (nuova revisione) e riscaricato.");
            }
            catch (DriveException ex)
            {
                End(s, Outcome.Failed, $"Drive ha risposto {ex.Status} {ex.Reason}: {ex.Message}");
            }

            // 8. PDF per la classe: condivisione col dominio
            s = Begin($"Condivisione con «chiunque in {GoogleClientConfig.SchoolDomain} con il link»");
            try
            {
                var pdf = await drive.CreateFileAsync("PDF per la classe – prova.txt", "text/plain", folderId,
                    Encoding.UTF8.GetBytes("File di prova di Ardesia: il PDF per la classe si condividerà così."), ct);
                await drive.ShareWithDomainAsync(pdf.Id, GoogleClientConfig.SchoolDomain, ct);
                End(s, Outcome.Ok, "Permessa: Ardesia potrà condividere da sé il PDF per la classe con gli alunni.\n" + pdf.WebViewLink);
            }
            catch (DriveException ex) when (ex.Status == 403)
            {
                End(s, Outcome.Failed, $"Non permessa ({ex.Reason}: {ex.Message}).",
                    $"Permettere ai docenti di condividere file di Drive con «chiunque in {GoogleClientConfig.SchoolDomain} con il link»: " +
                    "Console di amministrazione → App → Google Workspace → Drive e Documenti → Condivisione.");
            }
            catch (DriveException ex)
            {
                End(s, Outcome.Failed, $"Drive ha risposto {ex.Status} {ex.Reason}: {ex.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            Begin("Prova interrotta");
        }
        catch (Exception ex)
        {
            End(Begin("Errore imprevisto"), Outcome.Failed, ex.Message);
        }
        finally
        {
            await CleanUpAsync(drive, tokens, folderId);
            _cts = null;
            _close.Content = "Chiudi";
            _start.Content = "Ripeti la prova";
            _start.IsEnabled = true;
            _copy.IsEnabled = true;
            Summarize();
        }
    }

    async Task CleanUpAsync(DriveApi drive, GoogleTokens? tokens, string? folderId)
    {
        if (tokens == null) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        if (folderId != null)
        {
            var s = Begin("Pulizia: cartella di prova nel cestino");
            try
            {
                await drive.TrashAsync(folderId, cts.Token);
                End(s, Outcome.Ok, "«Ardesia – prova» è nel cestino di Drive (con i file che contiene).");
            }
            catch (Exception ex)
            {
                End(s, Outcome.Warning, "Non riuscito: cancella a mano la cartella «Ardesia – prova» da Drive. " + ex.Message);
            }
        }
        var r = Begin("Uscita: accesso revocato");
        try
        {
            await GoogleAuth.RevokeAsync(tokens.RefreshToken ?? tokens.AccessToken, cts.Token);
            End(r, Outcome.Ok, "Ardesia non ha più accesso al tuo account (la prova non salva nulla).");
        }
        catch (Exception ex)
        {
            End(r, Outcome.Warning, "Revoca non riuscita; puoi togliere l'accesso da myaccount.google.com → Sicurezza → App di terze parti. " + ex.Message);
        }
    }

    void Summarize()
    {
        var failed = _steps.Children.Count > 0 && _report.ToString().Contains("[NO]");
        string text;
        if (!failed)
            text = "✔ Tutto a posto: per Ardesia su Google Drive non serve l'amministratore.";
        else if (_forAdmin.Count > 0)
            text = "Da chiedere all'amministratore:\n• " + string.Join("\n• ", _forAdmin);
        else
            text = "Qualcosa non va (vedi sopra): sistemalo e premi «Ripeti la prova».";
        _summary.Text = text;
        _summary.Foreground = !failed ? new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)) : Foreground;
        _report.AppendLine();
        _report.AppendLine(text);
        try { File.WriteAllText(_logFile, _report.ToString()); } catch { /* registro facoltativo */ }
    }
}
