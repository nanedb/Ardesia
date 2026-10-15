# Ardesia – note per Claude

Blocco appunti per **LIM** (lavagne interattive) in stile OneNote, per una scuola che passa da Microsoft 365 a Google. Autore e utente: Giovanni Dal Bo, docente. **Rispondere sempre in italiano**; testi dell'interfaccia e commenti nel codice in italiano.

## Progetto

- WPF, **.NET 10** (`net10.0-windows`), C# con nullable. Nessuna libreria esterna: PDF, ZIP, icone sono fatti in casa.
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
- `Ardesia.exe --prova-tocco`: apre solo la prova del tocco.
- **Prima di aprire l'app per prove** copiare `%AppData%\Ardesia\settings.json` e ripristinarlo alla fine (le prove modificano ultimo file, penne, ecc.). Usare file `.ardesia` di prova nello scratchpad, mai quelli dell'utente.
- **Tocco simulato**: `tools/TouchInject.ps1 -Paths "x,y;x,y;…" [-Paths2 …] [-HoldEndMs n]` (coordinate fisiche; schermo dell'utente 1920×1080, gli screenshot di computer-use sono 1456×819 → ×1,3187).
  Attenzione: con lo stack di input predefinito (WISP) il tocco iniettato arriva a WPF come **mouse**. Per provare i percorsi «stylus/tocco» veri, aggiungere temporaneamente a `bin\Debug\net10.0-windows\Ardesia.runtimeconfig.json` la proprietà `"Switch.System.Windows.Input.Stylus.EnablePointerSupport": true` e **toglierla** alla fine (cancellare il file e ricompilare). Non attivarla nel prodotto: le LIM funzionano con WISP.
- Nell'app desktop di Claude una finestra trasparente copre l'angolo in alto a destra dello schermo e intercetta i clic: per provare pulsanti lì, ingrandire la finestra di Ardesia o usare UI Automation.

## Insidie già incontrate

- InkCanvas sposta una selezione solo se la si afferra su un tratto o sul bordo: lo spostamento dall'interno è fatto a mano (`TryBeginMove`). Gli elementi (immagini, testi) non sono hit-test visibili fuori dallo strumento Testo.
- Col tocco un `ContextMenu` aperto durante la pressione si richiude al rilascio: il menu si apre **al rilascio** (cerchietto durante l'attesa).
- Il disegno dal vivo di WPF tracciava la scia anche con la gomma: ora lo gestisce `ArdesiaDynamicRenderer` (`LiveEnabled`, `HideLiveStroke`).
- La gomma precisa spezza i tratti matita in tratti normali: `Pencil.UpgradeAll` li ripristina.

## Idee in sospeso

- **GeoGebra**: riquadro con GeoGebra in WebView2; funzione presa da una casella di testo (prima fase) o dalla scrittura a mano (seconda fase, riconoscimento Windows con conferma); grafico inserito come immagine e riapribile modificabile salvando lo stato GeoGebra. Da chiarire: internet sulle LIM, licenza del pacchetto offline.
