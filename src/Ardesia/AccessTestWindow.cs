using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ardesia.Services.Google;

namespace Ardesia;

/// <summary>
/// «Prova dell'accesso a Google» (Ardesia.exe --prova-accesso [registro.txt]): la prima volta apre il browser,
/// fa l'accesso e salva l'account cifrato; rilanciata, deve trovarlo e rinnovare il gettone senza browser.
/// Prova anche il rinnovo dopo un 401 e, col pulsante «Esci da Google», la revoca. Usa l'account vero di Ardesia
/// (%LocalAppData%\Ardesia\google-account.bin), non tocca settings.json.
/// </summary>
public sealed class AccessTestWindow : Window
{
    const string UserInfoUri = "https://openidconnect.googleapis.com/v1/userinfo";

    readonly StackPanel _steps = new();
    readonly TextBlock _summary;
    readonly Button _signOut, _close;
    readonly StringBuilder _report = new();
    readonly string _logFile;
    readonly GoogleAccountService _g = GoogleAccountService.Current;
    int _failures;

    public AccessTestWindow(string? logFile)
    {
        _logFile = logFile ?? Path.Combine(Path.GetTempPath(), "ardesia-accesso.log");
        Title = "Ardesia – Prova dell'accesso a Google";
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
        head.Children.Add(new TextBlock { Text = "Prova dell'accesso a Google", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        head.Children.Add(new TextBlock
        {
            Text = "Se Ardesia non ricorda ancora un account, si apre il browser: accedi con l'account " +
                   $"@{GoogleClientConfig.SchoolDomain}. Poi chiudi e rilancia la prova: deve risultare collegato senza browser.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 14),
        });
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        _signOut = MakeButton("Esci da Google (revoca)", "PlainButton");
        _signOut.IsEnabled = false;
        _signOut.Click += async (_, _) => await SignOutAsync();
        _close = MakeButton("Chiudi", "AccentButton");
        _close.Click += (_, _) => Close();
        buttons.Children.Add(_signOut);
        buttons.Children.Add(_close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        _summary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 15, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(_summary, Dock.Bottom);
        root.Children.Add(_summary);

        root.Children.Add(new ScrollViewer { Content = _steps, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Loaded += async (_, _) => await RunAsync();
    }

    static Button MakeButton(string text, string style)
    {
        var b = new Button { Content = text, MinWidth = 120, Height = 38, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(8, 0, 0, 0) };
        if (Application.Current.TryFindResource(style) is Style st) b.Style = st;
        return b;
    }

    void Log(bool? ok, string title, string detail = "")
    {
        var icon = new TextBlock
        {
            Text = ok switch { true => "✔", false => "✖", null => "•" },
            Width = 28,
            FontSize = 18,
            FontFamily = new FontFamily("Segoe UI Symbol"),
            Foreground = ok switch
            {
                true => new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)),
                false => new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)),
                null => Brushes.Gray,
            },
        };
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (detail.Length > 0) text.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray });
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        row.Children.Add(icon);
        row.Children.Add(text);
        _steps.Children.Add(row);

        if (ok == false) _failures++;
        _report.AppendLine($"[{ok switch { true => "OK", false => "NO", null => "--" }}] {title}");
        if (detail.Length > 0) _report.AppendLine("      " + detail.Replace("\n", "\n      "));
        try { File.WriteAllText(_logFile, _report.ToString()); } catch { /* registro facoltativo */ }
    }

    async Task RunAsync()
    {
        _report.AppendLine($"Prova dell'accesso a Google di Ardesia – {DateTime.Now:dd/MM/yyyy HH:mm}");
        _report.AppendLine();
        try
        {
            if (!_g.IsConfigured)
            {
                Log(false, "File del client Google", _g.ConfigError ?? "");
                return;
            }
            Log(true, "File del client Google", _g.Config!.SourceFile);

            bool restored = _g.IsSignedIn;
            if (restored)
            {
                Log(true, $"Account ricordato: {_g.Email}",
                    $"Nome: {_g.DisplayName}. Accesso fatto il {_g.SignedInAt:dd/MM/yyyy HH:mm}. File: {TokenStore.FilePath}");
                await _g.GetAccessTokenAsync(CancellationToken.None);
                Log(true, "Gettone rinnovato senza browser", $"L'access token scade alle {_g.AccessTokenExpiresAt:HH:mm:ss}.");
            }
            else
            {
                Log(null, "Nessun account ricordato: si apre il browser", "Accedi con l'account della scuola (5 minuti al massimo).");
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                await _g.SignInAsync(cts.Token);
                Activate();
                Log(true, $"Accesso riuscito: {_g.Email}",
                    $"Nome: {_g.DisplayName}. L'access token scade alle {_g.AccessTokenExpiresAt:HH:mm:ss}.");
                CheckStoredFile();
                // Un servizio nuovo legge solo il file: come al prossimo avvio di Ardesia.
                var fresh = new GoogleAccountService();
                if (fresh.Email != _g.Email) throw new InvalidOperationException("Riletto dal disco, l'account non coincide.");
                await fresh.GetAccessTokenAsync(CancellationToken.None);
                Log(true, "Account riletto dal file e gettone rinnovato", "Come succederà al prossimo avvio di Ardesia.");
            }

            await CheckUserInfoAsync("Chiamata a Google col gettone");

            // Rinnovo dopo un 401: il gettone in memoria viene guastato apposta.
            _g.SpoilAccessTokenForTest();
            await CheckUserInfoAsync("Rinnovo automatico dopo una risposta 401");

            _summary.Text = restored
                ? "Tutto a posto: l'accesso è sopravvissuto al riavvio. Ora puoi provare «Esci da Google»."
                : "Accesso salvato. Chiudi e rilancia «Ardesia.exe --prova-accesso»: deve risultare collegato senza aprire il browser.";
            _signOut.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Log(false, "Errore", ex.Message);
            _summary.Text = "La prova non è riuscita: vedi sopra.";
            _signOut.IsEnabled = _g.IsSignedIn;
        }
        finally
        {
            _report.AppendLine();
            _report.AppendLine(_failures == 0 ? "Esito: OK" : $"Esito: {_failures} problemi");
            try { File.WriteAllText(_logFile, _report.ToString()); } catch { /* registro facoltativo */ }
        }
    }

    void CheckStoredFile()
    {
        var bytes = File.ReadAllBytes(TokenStore.FilePath);
        var text = Encoding.UTF8.GetString(bytes);
        bool plain = text.Contains(_g.Email!, StringComparison.OrdinalIgnoreCase) || text.Contains("RefreshToken");
        Log(!plain, plain ? "Il file dell'account NON è cifrato" : "File dell'account cifrato con DPAPI",
            $"{TokenStore.FilePath} ({bytes.Length} byte)");
    }

    async Task CheckUserInfoAsync(string title)
    {
        using var r = await _g.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, UserInfoUri), CancellationToken.None);
        var body = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode)
        {
            Log(false, title, $"Google ha risposto {(int)r.StatusCode}: {body}");
            return;
        }
        using var doc = JsonDocument.Parse(body);
        var email = doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
        Log(email == _g.Email, title, $"Google risponde: {email}. Il gettone scade alle {_g.AccessTokenExpiresAt:HH:mm:ss}.");
    }

    async Task SignOutAsync()
    {
        _signOut.IsEnabled = false;
        var stored = TokenStore.Load();
        var warning = await _g.SignOutAsync(CancellationToken.None);
        Log(warning == null, "Esci da Google", warning ?? "Accesso revocato su Google.");
        Log(!File.Exists(TokenStore.FilePath), "File dell'account cancellato", TokenStore.FilePath);
        Log(!_g.IsSignedIn, "Ardesia risulta scollegata");

        // Il vecchio refresh token non deve più funzionare (la revoca può metterci qualche secondo).
        if (stored != null && _g.Config is { } cfg)
        {
            string result = "il gettone funziona ancora";
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    await GoogleAuth.RefreshAsync(cfg, stored.RefreshToken, CancellationToken.None);
                }
                catch (GoogleAuthException ex) when (ex.Code == "invalid_grant")
                {
                    result = "";
                    break;
                }
                await Task.Delay(2000);
            }
            Log(result.Length == 0, "Il vecchio gettone è rifiutato da Google", result);
        }
        _summary.Text = _failures == 0 ? "Uscita riuscita: Ardesia non può più accedere al tuo Drive." : "Qualcosa non va: vedi sopra.";
        try { File.WriteAllText(_logFile, _report.ToString()); } catch { /* registro facoltativo */ }
    }
}
