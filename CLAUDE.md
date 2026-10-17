# Ardesia – note per Claude

Blocco appunti per **LIM** (lavagne interattive) in stile OneNote, per una scuola che passa da Microsoft 365 a Google. Autore e utente: Giovanni Dal Bo, docente. **Rispondere sempre in italiano**; testi dell'interfaccia e commenti nel codice in italiano.

## Progetto

- WPF, **.NET 10** (`net10.0-windows`), C# con nullable. Nessuna libreria esterna: PDF, ZIP, icone sono fatti in casa. Unica eccezione, approvata dall'utente: `Microsoft.Web.WebView2` per GeoGebra.
- Soluzione `Ardesia.slnx`, progetto in `src/Ardesia/`.
- File `.ardesia` = ZIP con `notebook.json` (struttura, sfondi, caselle di testo in XAML), `ink/{pagina}.isf` (tratti ISF), `media/{id}` (immagini). Vedi `Services/NotebookStore.cs`. Salvataggio atomico (file temporaneo + `File.Replace`).
- Gerarchia libera: `NotebookNode` con `Sections` (sottosezioni) e `Pages` (pagine/sottopagine) — `Models/Notebook.cs`.
- Colore d'accento: petrolio `#1E5A68` (non il viola di OneNote). Stili e icone in `Themes/Theme.xaml`.

### File principali

| File | Contenuto |
|---|---|
| `Editor/PageEditor.xaml(.cs)` | Il foglio: InkCanvas, strumenti, zoom, foglio infinito, appunti, menu contestuale, pressione prolungata, palmo-gomma, «dito sposta la pagina», linee raddrizzate, righello dal vivo, spostamento selezione |
| `Editor/Pencil.cs` | Matita 3H–3B (grana), `ArdesiaInkCanvas` + `ArdesiaDynamicRenderer` (disegno dal vivo: normale / matita / nascosto) |
| `Editor/GeometryTools.cs` | Righello, goniometro, compasso |
| `MainWindow.xaml(.cs)` | Barra completa e semplificata, penne configurabili (popup), sezioni/pagine, file, PDF, scorciatoie |
| `GeoGebraWindow.xaml(.cs)` | **GeoGebra Classic** (`appName: 'classic'`, prospettiva Algebra + Grafici: scelto dall'utente perché più pulito della Calcolatrice grafica) in WebView2, caricato **online** da geogebra.org (le LIM hanno sempre internet); restituisce PNG (doppia risoluzione) e costruzione `.ggb` in Base64 |
| `ClassPdfDialog` + `MainWindow.UpdateClassPdf` | PDF per la classe: **attivo di default** per ogni blocco appunti (scelta dell'utente), accanto al file con lo stesso nome (`ClassPdfFile`); impostazioni salvate **nel blocco appunti** (`ClassPdfDisabled`, `ClassPdfPath` solo se diverso da quello accanto al file, `ClassPdfExcluded`); rifatto in `ConfirmClose` (chiusura / cambio blocco) solo se ci sono modifiche o manca il file, scrivendo prima un `.tmp`. Non a ogni salvataggio automatico: bloccherebbe la LIM. Serve perché gli alunni (anche con Mac/iPad) non possono aprire i `.ardesia` |
| `SettingsWindow` (File › Impostazioni…) | Due schede: **Questo PC** (`AppSettings`: «Il dito sposta la pagina», prova del tocco, file recenti) e **Questo blocco appunti** (PDF per la classe, salvato nel file). Il riquadro del PDF è `Controls/ClassPdfPanel`, condiviso con `ClassPdfDialog`; le voci sparse nei menu restano (scelta dell'utente) |
| `StartWindow`, `ExportDialog`, `TouchTestWindow` | Schermata iniziale, esportazione PDF, prova del tocco della LIM |
| `Services/AppSettings.cs` | Impostazioni in `%AppData%\Ardesia\settings.json` (penne, ultimo file, barra semplificata, FingerPans…) |

## Comportamento richiesto dall'utente (non cambiarlo senza chiedere)

- All'avvio si riapre l'ultimo file; se manca, `StartWindow` chiede **prima** di creare/aprire un file (posizione e nome). Ogni modifica si salva da sola (~1,5 s) e all'uscita, **senza domande**.
- Finestra principale ingrandita e mai più grande dello schermo (sulle LIM il ridimensionamento è alto).
- Icone penne: simboli Segoe Fluent riempiti col colore della penna + profilo nero; barretta sotto = spessore, dello stesso colore.
- Linea raddrizzata tenendo ferma la penna: **subito**, senza anteprima. Righello: linea dritta e appoggiata al bordo già mentre si disegna.

## Le LIM della scuola (misurate con «Prova tocco LIM»)

- **Promethean** (Windows vede «X75A03»): penna e dito arrivano **entrambi come tocco**, senza larghezza. Il **palmo** arriva come penna *rovesciata* (gomma), ~19–20 mm → in Ardesia cancella. «Il dito sposta la pagina» va lasciato **spento**.
- **Newline Q Pro 75″** (TT-7523QA, Windows vede «Honghe IstE75», Android 14 integrato): la **penna arriva come penna** (4–5 mm), il **dito come tocco** (~20 mm), il palmo è ignorato dalla LIM. Qui «Il dito sposta la pagina» va **attivato**.
- Ardesia gira sul PC Windows collegato alla LIM (HDMI + USB «Touch») o sul modulo OPS, non sull'Android della lavagna.

## Compilare, provare, pubblicare

```powershell
dotnet build                                   # se fallisce per file bloccato, Ardesia è aperta: non chiuderla senza chiedere,
                                               # compila altrove con -p:OutDir=<scratchpad>\build\
.\build-release.ps1                            # publish single-file + Inno Setup → dist\ArdesiaSetup.exe, dist\Ardesia-portatile.exe
```

- Versione in **due posti**: `<Version>` in `src/Ardesia/Ardesia.csproj` e `#define AppVersion` in `installer/Ardesia.iss`.
- L'installer è **per utente, senza amministratore** (`PrivilegesRequired=lowest`). Inno Setup è in `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`.
- Repository privato GitHub `nanedb/Ardesia` (gh già autenticato). Commit, push e release **solo su richiesta**; release con `gh release create vX.Y.Z dist\ArdesiaSetup.exe dist\Ardesia-portatile.exe --notes-file …`. Messaggi di commit in italiano con riga `Co-Authored-By`.
- Su PowerShell: `R` è un alias di sistema (non usarlo come nome di funzione); il codice ha terminazioni di riga miste, quindi per sostituzioni multi-riga preferire lo strumento Edit; per i messaggi di commit usare `git commit -F file`.

### Prove automatiche

- `Ardesia.exe --snapshot out.png --demo [--tab Disegna]`: crea contenuti di prova, salva lo screenshot della finestra, salva/ricarica un `.ardesia` ed esporta un PDF nella stessa cartella, poi esce. Non tocca le impostazioni.
- `--tab Impostazioni` / `--tab "Impostazioni blocco"`: fotografa la finestra Impostazioni (scheda Questo PC / Questo blocco appunti) invece del foglio.
- `Ardesia.exe --prova-tocco`: apre solo la prova del tocco.
- `Ardesia.exe --prova-google <client_secret….json>`: prova di accesso a Google e Drive (piano in `docs/google-drive.md`, **in pausa**). Il JSON del client e lo screenshot «ID client OAuth.png» non vanno mai nel repository.
- **Prima di aprire l'app per prove** copiare `%AppData%\Ardesia\settings.json` e ripristinarlo alla fine (le prove modificano ultimo file, penne, ecc.). Usare file `.ardesia` di prova nello scratchpad, mai quelli dell'utente.
- **Tocco simulato**: `tools/TouchInject.ps1 -Paths "x,y;x,y;…" [-Paths2 …] [-HoldEndMs n]` (coordinate fisiche; schermo dell'utente 1920×1080, gli screenshot di computer-use sono 1456×819 → ×1,3187).
  Attenzione: con lo stack di input predefinito (WISP) il tocco iniettato arriva a WPF come **mouse**. Per provare i percorsi «stylus/tocco» veri, aggiungere temporaneamente a `bin\Debug\net10.0-windows\Ardesia.runtimeconfig.json` la proprietà `"Switch.System.Windows.Input.Stylus.EnablePointerSupport": true` e **toglierla** alla fine (cancellare il file e ricompilare). Non attivarla nel prodotto: le LIM funzionano con WISP.
- Nell'app desktop di Claude una finestra trasparente copre l'angolo in alto a destra dello schermo e intercetta i clic: per provare pulsanti lì, ingrandire la finestra di Ardesia o usare UI Automation.

## Insidie già incontrate

- InkCanvas sposta una selezione solo se la si afferra su un tratto o sul bordo: lo spostamento dall'interno è fatto a mano (`TryBeginMove`). Gli elementi (immagini, testi) non sono hit-test visibili fuori dallo strumento Testo.
- Il lazo di InkCanvas prende un oggetto solo se ne racchiude quasi tutto il riquadro, e le caselle di testo sono larghe 520 px: `SetupTextSelection`/`FinishLasso` le aggiungono se il lazo ne racchiude il **testo**; un tocco/clic sul testo le seleziona. `ElementAt` per le caselle conta solo l'area del testo (`TextBounds`).
- Icona delle finestre: `BitmapFrame.Create` (non `BitmapImage`, che legge solo l'immagine da 16 px e rende l'icona minuscola nella barra delle applicazioni).
- GeoGebra all'avvio apre la tastiera e un pannello Algebra largo: non ci sono parametri, la pagina simula il clic su «Chiudi» e il trascinamento del divisore (`closeKeyboard`, `narrowAlgebra`).
- Col tocco un `ContextMenu` aperto durante la pressione si richiude al rilascio: il menu si apre **al rilascio** (cerchietto durante l'attesa).
- Il disegno dal vivo di WPF tracciava la scia anche con la gomma: ora lo gestisce `ArdesiaDynamicRenderer` (`LiveEnabled`, `HideLiveStroke`).
- La gomma precisa spezza i tratti matita in tratti normali: `Pencil.UpgradeAll` li ripristina.

## GeoGebra

- Elemento `"geogebra"` (`GraphImage` in `ElementFactory.cs`): `MediaId` = PNG, `Content` = costruzione `.ggb` in Base64. Si apre in una **finestra a parte** (WebView2 sul foglio coprirebbe l'inchiostro e non seguirebbe lo zoom).
- Pulsante **«GeoGebra»** (scheda Inserisci) e voce «Disegna il grafico con GeoGebra» nel menu contestuale di una casella di testo; prende le funzioni dalla casella selezionata, una per riga (normalizzate da `MathText`: x² → x^(2), virgola decimale → punto) e mette il grafico accanto. Doppio clic o menu contestuale su un grafico GeoGebra lo riapre. Il pulsante «Grafico» ora crea invece il grafico di Ardesia (sotto).
- La pagina è servita da Ardesia all'indirizzo finto `https://ardesia.local/geogebra.html` (`WebResourceRequested`): con `NavigateToString` l'origine è opaca e GeoGebra si blocca leggendo `document.cookie` (pagina bianca).
- Prova: `Ardesia.exe --prova-geogebra [registro.log] [--graphing]` apre solo la finestra con due funzioni, registra avvio, console, errori JavaScript ed esportazione, salva uno screenshot (`registro.png`, da `CapturePreviewAsync`) e si chiude dopo 25 s. La finestra compare sullo schermo dell'utente: avvisarlo prima.
- Nel pannello Browser nascosto GeoGebra non disegna e `getPNGBase64` fallisce: per provare la pagina tenere il pannello visibile.

## Grafico di funzione di Ardesia (lavoro in corso)

Alternativa leggera a GeoGebra, disegnata da Ardesia sul foglio: vettoriale, segue lo zoom, sta sotto l'inchiostro, esce nitida nel PDF. **Piano approvato, scelte dell'utente e lista delle cose da fare in `docs/grafico-nativo.md`: leggerlo prima di continuare.**

| File (`src/Ardesia/Editor/Graph/`) | Contenuto |
|---|---|
| `MathExpr.cs` | `MathText.Normalize` (anche per GeoGebra), `MathParser` a discesa ricorsiva: `y = …`, `f(x) = …`, `x = k`, moltiplicazione sottintesa (2x, ax, 3(x+1)), funzioni senza parentesi (sen 2x), `|x|`, `√`, nomi italiani (sen, tg, arcsen); errori `MathSyntaxException` in italiano. Le lettere diverse da x sono parametri |
| `GraphMath.cs` | Campionamento che spezza la curva agli asintoti, zeri (anche doppi), estremi, derivata numerica, tacche 1-2-5 e in π, numeri all'italiana («(1,5; −2)») |
| `GraphState.cs` | Stato salvato come JSON in `ElementData.Content` (tipo `"graph"`): funzioni con colore, vista, parametri, opzioni |
| `FunctionGraph.cs` | L'elemento (`FrameworkElement`, `OnRender`): griglia, segno, assi con frecce, curve, derivata tratteggiata, punti notevoli con etichette che si evitano, legenda |
| `GraphFunctionsDialog.cs` | Finestrella «Funzioni del grafico» (una per riga, tasti x² √ π \|x\| sen…, controllo degli errori) |
| `GraphSelfTest.cs` | Prove: `Ardesia.exe --prova-grafico [registro.txt]` (codice d'uscita 1 se qualcosa non va) |

- In `PageEditor`: `InsertGraph` (pulsante «Grafico»), `EditGraphFunctions` (doppio clic), `AddGraphMenuItems` (menu contestuale: caselle che lasciano il menu aperto, zoom, vista standard, «Apri in GeoGebra» = `OpenGraph(from:)`). `--snapshot --demo` mette un grafico di prova sulla prima pagina.
- Tipi di elemento sconosciuti (da versioni più nuove) diventano `PreservedElement` e si riscrivono invariati. Le versioni ≤ 0.3.2 invece **cancellano** i grafici salvando: non aprire con quelle un file di prova con grafici.
- **Da fare (richieste dell'utente dopo la prima prova, 16/10/2026)**: legenda in stile LaTeX; sistemare il doppio clic (chiedere che cosa non va); «Apri in GeoGebra» non riconosce i parametri; spostamento e zoom degli assi col mouse con lo strumento «Seleziona»; poi barretta, cursori, gesti con le dita, scrittura a mano. Dettagli in `docs/grafico-nativo.md`.
