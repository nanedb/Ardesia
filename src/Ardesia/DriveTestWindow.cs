using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ardesia.Models;
using Ardesia.Services;
using Ardesia.Services.Google;

namespace Ardesia;

/// <summary>
/// «Prova di Google Drive» (Ardesia.exe --prova-drive [registro.txt] [--finto]): prima le prove con un Drive finto in
/// memoria (DriveSelfTest), poi con l'account vero nella cartella «Ardesia» vera (scelta dell'utente): crea un blocco
/// in Ardesia/PROVA/Prova, lo carica, lo rinomina, lo riscarica, prova il caricamento riprendibile, lo trova
/// nell'elenco; fase 3: lucchetto scritto e tolto, conflitto con un «altro PC» simulato caricando direttamente con
/// DriveApi (copia «modifiche da …» nella stessa cartella); fase 4: PDF per la classe accanto al blocco, condiviso
/// col dominio, aggiornato nello stesso file e rinominato col blocco. Alla fine blocco, PDF e copia vanno nel cestino (le cartelle restano). Se non si è collegati si apre il browser.
/// La cache della prova è in una cartella temporanea, non in quella vera. Codice d'uscita 1 se qualcosa non va.
/// </summary>
public sealed class DriveTestWindow : Window
{
    readonly StackPanel _steps = new();
    readonly ScrollViewer _scroll;
    readonly TextBlock _summary;
    readonly StringBuilder _report = new();
    readonly string _logFile;
    int _failures;

    public DriveTestWindow(string? logFile)
    {
        _logFile = logFile ?? Path.Combine(Path.GetTempPath(), "ardesia-drive.log");
        Title = "Ardesia – Prova di Google Drive";
        Width = 760;
        Height = Math.Min(860, SystemParameters.WorkArea.Height - 20);
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
            Text = "Prima con un Drive finto, poi con il tuo account: un blocco di prova (col suo PDF per la classe) in Ardesia › PROVA › Prova, che alla fine va nel " +
                   "cestino di Drive. Le cartelle PROVA e Prova restano: cancellale dal sito di Drive quando vuoi.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 14),
        });
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var close = new Button { Content = "Chiudi", MinWidth = 120, Height = 38, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        if (Application.Current.TryFindResource("AccentButton") is Style st) close.Style = st;
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);

        _summary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 15, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(_summary, Dock.Bottom);
        root.Children.Add(_summary);

        _scroll = new ScrollViewer { Content = _steps, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(_scroll);
        Content = root;
        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) => Application.Current.Shutdown(_failures == 0 ? 0 : 1);
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
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(icon);
        row.Children.Add(text);
        _steps.Children.Add(row);
        _scroll.ScrollToEnd();

        if (ok == false) _failures++;
        _report.AppendLine($"[{ok switch { true => "OK", false => "NO", null => "--" }}] {title}");
        if (detail.Length > 0) _report.AppendLine("      " + detail.Replace("\n", "\n      "));
        try { File.WriteAllText(_logFile, _report.ToString()); } catch { /* registro facoltativo */ }
    }

    /// <summary>Solo la parte col Drive finto, senza finestra: scrive il registro e restituisce i problemi.</summary>
    public static async Task<int> RunFakeOnlyAsync(string logFile)
    {
        var report = new StringBuilder($"Prova di Google Drive (Drive finto) – {DateTime.Now:dd/MM/yyyy HH:mm}\n\n");
        int failures = await DriveSelfTest.RunAsync((ok, title, detail) =>
        {
            report.AppendLine($"[{ok switch { true => "OK", false => "NO", null => "--" }}] {title}");
            if (detail.Length > 0) report.AppendLine("      " + detail.Replace("\n", "\n      "));
        });
        report.AppendLine().AppendLine(failures == 0 ? "Esito: OK" : $"Esito: {failures} problemi");
        File.WriteAllText(logFile, report.ToString());
        return failures;
    }

    async Task RunAsync()
    {
        _report.AppendLine($"Prova di Google Drive di Ardesia – {DateTime.Now:dd/MM/yyyy HH:mm}");
        _report.AppendLine();
        Log(null, "Prima parte: Drive finto in memoria");
        await DriveSelfTest.RunAsync(Log);
        int fakeFailures = _failures;

        Log(null, "Seconda parte: il tuo Google Drive");
        var folder = Path.Combine(Path.GetTempPath(), "ardesia-prova-drive-vera-" + Guid.NewGuid().ToString("N")[..8]);
        DriveSync? sync = null;
        string? id = null, copyId = null, pdfId = null;
        try
        {
            var g = GoogleAccountService.Current;
            if (!g.IsSignedIn)
            {
                Log(null, "Nessun account collegato: si apre il browser", $"Accedi con l'account @{GoogleClientConfig.SchoolDomain} (5 minuti al massimo).");
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                await g.SignInAsync(cts.Token);
                Activate();
            }
            Log(true, $"Collegato come {g.Email}");

            // pezzi piccoli: il caricamento riprendibile si prova con poco più di un megabyte
            var api = new DriveApi(g) { ResumableThreshold = 512 * 1024, ChunkSize = 256 * 1024 };
            sync = new DriveSync(api, folder, g.Email!) { QuietDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(5) };

            var listing = await sync.ListAsync(CancellationToken.None);
            Log(true, "Elenco dei blocchi su Drive", $"{listing.Notebooks.Count} blocchi; classi: {string.Join(", ", listing.Classes.DefaultIfEmpty("nessuna"))}");

            var title = $"PROVA Prova {DateTime.Now:dd-MM HH.mm.ss}";
            id = await sync.CreateNotebookAsync("PROVA", "Prova", title, DriveSelfTest.NewNotebook(), CancellationToken.None);
            var meta = await api.GetAsync(id, CancellationToken.None);
            var subj = await api.GetAsync(meta.Parents![0], CancellationToken.None);
            var cls = await api.GetAsync(subj.Parents![0], CancellationToken.None);
            var root = await api.GetAsync(cls.Parents![0], CancellationToken.None);
            Log(meta.MimeType == DriveSync.MimeType && subj.Name == "Prova" && cls.Name == "PROVA" && root.Name == "Ardesia" && root.Id == sync.RootFolderId,
                "Blocco creato in Ardesia › PROVA › Prova", $"{root.Name}/{cls.Name}/{subj.Name}/{meta.Name} ({meta.MimeType})");

            var again = await sync.EnsureFolderAsync("prova", "PROVA", CancellationToken.None);
            Log(again.Id == subj.Id, "Cartelle riusate senza distinguere maiuscole", $"«prova/PROVA» → {again.Class}/{again.Subject}");

            DriveSelfTest.Modify(sync.CachePath(id), "Aggiunta dalla prova");
            sync.Changed(id, title);
            var flushed = await sync.FlushAsync(TimeSpan.FromSeconds(60));
            var after = await api.GetAsync(id, CancellationToken.None);
            Log(flushed && after.HeadRevisionId != meta.HeadRevisionId && after.HeadRevisionId == sync.Entry(id)!.BaseRevision,
                "Modifica caricata dalla coda", sync.Status(id).Text);

            var renamed = title + " rinominato";
            sync.Changed(id, renamed);
            await sync.FlushAsync(TimeSpan.FromSeconds(60));
            after = await api.GetAsync(id, CancellationToken.None);
            Log(after.Name == renamed + NotebookStore.Extension, "Rinomina su Drive col caricamento", after.Name);

            // ── fase 4: PDF per la classe accanto al blocco, condiviso col dominio, aggiornato nello stesso file ──
            static async Task MakePdfAsync(DriveSync s, string nb)
            {
                var book = NotebookStore.Load(s.CachePath(nb));
                PdfExporter.Export(book, book.ClassPdfPages().ToList(), s.ClassPdfCachePath(nb), book.Title);
                s.ClassPdfChanged(nb);
                await s.FlushAsync(TimeSpan.FromSeconds(60));
            }
            await MakePdfAsync(sync, id);
            pdfId = sync.Entry(id)!.ClassPdfId;
            if (pdfId == null) throw new Exception("Il PDF per la classe non è stato creato: " + sync.Entry(id)!.ClassPdfError);
            var pdfMeta = await api.GetAsync(pdfId, CancellationToken.None);
            var nbMeta = await api.GetAsync(id, CancellationToken.None);
            Log(pdfMeta.Name == renamed + ".pdf" && pdfMeta.MimeType == DriveSync.PdfMime && pdfMeta.Parents?[0] == meta.Parents[0]
                && nbMeta.AppProperties?.GetValueOrDefault(DriveSync.ClassPdfKey) == pdfId,
                "PDF per la classe caricato accanto al blocco, con lo stesso nome", $"{pdfMeta.Name} ({DriveUi.Size(pdfMeta.Size ?? 0)})");
            var perms = await api.PermissionsAsync(pdfId, CancellationToken.None);
            Log(perms.Any(p => p is { Type: "domain", Domain: GoogleClientConfig.SchoolDomain, Role: "reader", Discoverable: false }),
                $"Condiviso con {GoogleClientConfig.SchoolDomain} (lettura, solo con il link)",
                string.Join("; ", perms.Select(p => $"{p.Type} {p.Domain} {p.Role}{(p.Discoverable == false ? ", non cercabile" : "")}"))
                + (sync.Entry(id)!.ClassPdfShareError is { } shareError ? $" – rifiutata: {shareError}" : ""));
            DriveSelfTest.Modify(sync.CachePath(id), "Lezione per il PDF");
            sync.Changed(id, renamed);
            await sync.FlushAsync(TimeSpan.FromSeconds(60));
            await MakePdfAsync(sync, id);
            var pdfAgain = await api.GetAsync(pdfId, CancellationToken.None);
            Log(sync.Entry(id)!.ClassPdfId == pdfId && pdfAgain.HeadRevisionId != pdfMeta.HeadRevisionId && !pdfAgain.Trashed,
                "PDF aggiornato nello stesso file (stesso link)", DriveSync.ClassPdfLinkOf(pdfId));
            renamed = title + " rinominato due volte";
            sync.Changed(id, renamed);
            await sync.FlushAsync(TimeSpan.FromSeconds(60));
            pdfAgain = await api.GetAsync(pdfId, CancellationToken.None);
            Log(pdfAgain.Name == renamed + ".pdf", "Rinomina del blocco: rinominato anche il PDF", pdfAgain.Name);

            File.Delete(sync.CachePath(id));
            var opened = await sync.OpenAsync(id, null, CancellationToken.None);
            var nb = NotebookStore.Load(opened.CachePath);
            Log(nb.Sections.Any(s => s.Title == "Aggiunta dalla prova"), "Riscaricato da Drive (copia in cache cancellata)",
                $"{nb.Sections.Count} sezioni, {DriveUi.Size(new FileInfo(opened.CachePath).Length)}");
            opened = await sync.OpenAsync(id, null, CancellationToken.None);
            Log(opened.Warning == null, "Riaperto senza scaricare di nuovo (stessa revisione)");

            // ── fase 3: lucchetto morbido ──
            var none = CancellationToken.None;
            sync.SetOpen(id);
            var locked = await api.GetAsync(id, none);
            for (int i = 0; i < 40 && locked.AppProperties?.GetValueOrDefault(DriveSync.LockPcKey) != sync.MachineName; i++)
            {
                await Task.Delay(500);
                locked = await api.GetAsync(id, none);
            }
            Log(locked.AppProperties?.GetValueOrDefault(DriveSync.LockPcKey) == sync.MachineName
                && locked.AppProperties?.ContainsKey(DriveSync.LockTimeKey) == true,
                "Lucchetto scritto su Drive (appProperties)",
                $"apertoSu={locked.AppProperties?.GetValueOrDefault(DriveSync.LockPcKey)}, apertoAlle={locked.AppProperties?.GetValueOrDefault(DriveSync.LockTimeKey)}");
            sync.SetOpen(null);
            await sync.FlushAsync(TimeSpan.FromSeconds(30));
            var unlocked = await api.GetAsync(id, none);
            Log(unlocked.AppProperties?.ContainsKey(DriveSync.LockPcKey) != true && !sync.HasWork, "Lucchetto tolto alla chiusura del blocco");

            // ── fase 3: conflitto, con un «altro PC» che carica direttamente con DriveApi ──
            DriveFork? fork = null;
            Action<DriveFork> onFork = f => fork = f;
            DriveSync.Forked += onFork;
            try
            {
                sync.SetOpen(id);
                // prima il nostro lucchetto, poi l'«altro PC» (che lo sovrascrive col suo)
                for (int i = 0; i < 40 && (await api.GetAsync(id, none)).AppProperties?.GetValueOrDefault(DriveSync.LockPcKey) != sync.MachineName; i++)
                    await Task.Delay(500);
                var otherPc =Path.Combine(folder, "altro-pc" + NotebookStore.Extension);
                File.Copy(sync.CachePath(id), otherPc, overwrite: true);
                DriveSelfTest.Modify(otherPc, "Modifica dell'altro PC");
                await api.UpdateContentAsync(id, DriveSync.MimeType, File.ReadAllBytes(otherPc), none,
                    new Dictionary<string, string?> { [DriveSync.LockPcKey] = "ALTRO-PC-PROVA", [DriveSync.LockTimeKey] = DateTime.UtcNow.ToString("o") });
                DriveSelfTest.Modify(sync.CachePath(id), "Modifica di questo PC");
                sync.Changed(id, renamed);
                await sync.FlushAsync(TimeSpan.FromSeconds(60));
                await DriveSelfTest.WaitAsync(() => fork != null, 5000);
                copyId = fork?.NewId;
                if (copyId == null) throw new Exception("Il conflitto non è stato riconosciuto: nessuna copia.");
                var copyMeta = await api.GetAsync(copyId, none);
                var copySections = DriveSelfTest.SectionsOf(await api.DownloadAsync(copyId, none));
                var origMeta = await api.GetAsync(id, none);
                var origSections = DriveSelfTest.SectionsOf(await api.DownloadAsync(id, none));
                Log(copyMeta.Name.StartsWith(renamed + $" (modifiche da {sync.MachineName}, ") && copyMeta.Parents?[0] == meta.Parents[0]
                    && copySections.Contains("Modifica di questo PC") && !copySections.Contains("Modifica dell'altro PC")
                    && origSections.Contains("Modifica dell'altro PC") && !origSections.Contains("Modifica di questo PC"),
                    "Conflitto: la versione dell'altro PC resta, la nostra in una copia nella stessa cartella", copyMeta.Name);
                Log(sync.OpenId == copyId && copyMeta.AppProperties?.GetValueOrDefault(DriveSync.LockPcKey) == sync.MachineName
                    && origMeta.AppProperties?.GetValueOrDefault(DriveSync.LockPcKey) == "ALTRO-PC-PROVA"
                    && sync.Status(copyId).State == DriveSyncState.Conflict,
                    "Si continua sulla copia (col lucchetto); stato «Conflitto: salvata una copia»", sync.Status(copyId).Text);
                Log(copyMeta.AppProperties?.ContainsKey(DriveSync.ClassPdfKey) != true && copyMeta.AppProperties?.GetValueOrDefault(DriveSync.CopyOfKey) == id
                    && origMeta.AppProperties?.GetValueOrDefault(DriveSync.ClassPdfKey) == pdfId,
                    "Il PDF per la classe resta legato al blocco originale, non alla copia");

                listing = await sync.ListAsync(none);
                DriveLock? asked = null;
                bool canceled = false;
                try { await sync.OpenAsync(id, null, none, lk => { asked = lk; return false; }); }
                catch (OperationCanceledException) { canceled = true; }
                Log(listing.Notebooks.FirstOrDefault(n => n.Id == id)?.OpenOn == "ALTRO-PC-PROVA" && canceled && asked?.Pc == "ALTRO-PC-PROVA",
                    "Blocco aperto su un altro PC: «aperto su ALTRO-PC-PROVA» nell'elenco, domanda all'apertura",
                    asked != null ? $"ultima attività alle {asked.At:HH:mm}" : "nessuna domanda");

                sync.SetOpen(null);
                await sync.FlushAsync(TimeSpan.FromSeconds(30));
                copyMeta = await api.GetAsync(copyId, none);
                origMeta = await api.GetAsync(id, none);
                Log(copyMeta.AppProperties?.ContainsKey(DriveSync.LockPcKey) != true
                    && origMeta.AppProperties?.GetValueOrDefault(DriveSync.LockPcKey) == "ALTRO-PC-PROVA",
                    "Chiusura: tolto il nostro lucchetto, non quello dell'altro PC");
                await sync.TrashAsync(copyId, none);
                Log((await api.GetAsync(copyId, none)).Trashed, "Copia del conflitto nel cestino di Drive");
                copyId = null;
            }
            finally
            {
                DriveSync.Forked -= onFork;
            }
            opened = await sync.OpenAsync(id, null, none); // la cache del file originale si riscarica

            // caricamento riprendibile: 1,3 MB in pezzi da 256 KB, poi si rimette il blocco vero
            var big = RandomNumberGenerator.GetBytes(1300 * 1024);
            await api.UpdateContentAsync(id, DriveSync.MimeType, big, CancellationToken.None);
            var back = await api.DownloadAsync(id, CancellationToken.None);
            Log(back.AsSpan().SequenceEqual(big), "Caricamento riprendibile a pezzi (1,3 MB) e download", $"{back.Length} byte, uguali");
            await api.UpdateContentAsync(id, DriveSync.MimeType, File.ReadAllBytes(opened.CachePath), CancellationToken.None);

            listing = await sync.ListAsync(CancellationToken.None);
            var found = listing.Notebooks.FirstOrDefault(n => n.Id == id);
            Log(found is { Class: "PROVA", Subject: "Prova" } && listing.Classes.Contains("PROVA"),
                "Il blocco è nell'elenco, con classe e materia", found != null ? $"{found.Title} – {found.Folder}" : "non trovato");

            await sync.TrashAsync(id, CancellationToken.None, pdfId); // «Elimina anche il PDF»
            var trashed = await api.GetAsync(id, CancellationToken.None);
            var trashedPdf = await api.GetAsync(pdfId, CancellationToken.None);
            bool gone = false;
            try { await sync.OpenAsync(id, null, CancellationToken.None); }
            catch (DriveGoneException) { gone = true; }
            Log(trashed.Trashed && trashedPdf.Trashed && gone, "Blocco di prova e PDF per la classe nel cestino di Drive",
                "Le cartelle PROVA e Prova restano (Ardesia non elimina cartelle).");
            id = pdfId = null;
        }
        catch (Exception ex)
        {
            Log(false, "Errore", ex.Message);
        }
        finally
        {
            foreach (var leftover in new[] { id, copyId, pdfId })
                if (leftover != null && sync != null)
                {
                    try { await sync.TrashAsync(leftover, CancellationToken.None); } catch { /* resta: si cancella dal sito di Drive */ }
                }
            sync?.Dispose();
            try { Directory.Delete(folder, recursive: true); } catch { /* cartella temporanea */ }
            _report.AppendLine();
            _report.AppendLine(_failures == 0 ? "Esito: OK" : $"Esito: {_failures} problemi ({fakeFailures} col Drive finto)");
            try { File.WriteAllText(_logFile, _report.ToString()); } catch { /* registro facoltativo */ }
            _summary.Text = _failures == 0 ? "Tutto a posto." : $"{_failures} problemi: vedi sopra (registro: {_logFile}).";
        }
    }
}
