# Grafico di funzione nativo – piano

Stato: **approvato dall'utente (16/10/2026)**, implementazione cominciata.

- Fatto: fasi 1 e 2 (parser, calcolo, `--prova-grafico`, elemento `"graph"` con salvataggio, PDF, inserimento
  da «Grafico», GeoGebra come pulsante a parte), tutte le opzioni già disegnate e attivabili dal menu
  contestuale, finestrella «Funzioni del grafico» (doppio clic), oggetti sconosciuti conservati (§4).
- Da fare: barretta sotto il grafico selezionato (fase 3), cursori trascinabili (fase 4; per ora i
  parametri valgono 1 e sono scritti nella legenda), gesti sugli assi (fase 5), scrittura a mano (fase 6).

### Dopo la prova dell'utente (16/10/2026) – da fare il giorno dopo

1. **Legenda in stile LaTeX**: formule composte bene (apici, frazioni, radici), non il testo così come è scritto.
   Senza librerie esterne: un piccolo impaginatore fatto in casa che parte dall'albero di `MathParser`.
2. **Sistemare il doppio clic** sul grafico (chiedere all'utente che cosa non va: non apre la finestrella,
   la apre due volte, o fa altro).
3. **«Apri in GeoGebra» non riconosce i parametri**: GeoGebra vuole i cursori definiti prima
   (es. `a = 1` prima di `f(x) = 1.5x + a`); passare i valori di `GraphState.Params` come prime righe.
4. **Spostamento e zoom degli assi col mouse** quando è attivo lo strumento «Seleziona» e il grafico è
   selezionato (trascinamento = sposta gli assi, rotellina = zoom), oltre ai gesti con le dita della fase 5.
5. Il resto: barretta (fase 3), cursori (fase 4), gesti (fase 5), scrittura a mano (fase 6).

### 17/10/2026

- **Scelta dell'utente**: tutti i comandi sul grafico (doppio clic, zoom, spostamento degli assi…) funzionano
  **solo con lo strumento «Seleziona»**; con una penna sul grafico si scrive e basta.
- Doppio clic col mouse: l'utente non ricorda il problema e ora apre la finestrella. Resta da provare il
  doppio tocco con dito e penna della LIM.
- Fatto: la finestrella «Funzioni del grafico» è a righe, ognuna con la **casella per mostrare o nascondere**
  la funzione (`GraphFunctionData.Hidden`, salvato solo se vero), il trattino del colore, ✕ per toglierla,
  «+ Aggiungi una funzione»; Invio = riga nuova, testo di più righe incollato = più righe. Una riga corretta
  tiene il suo colore. Le funzioni nascoste non si disegnano, non vanno nella legenda né a GeoGebra, ma i
  loro parametri restano. Istantanea: `--snapshot out.png --tab "Funzioni grafico"`.
- Fatto (punto 3): **«Apri in GeoGebra» con i parametri**. `GeoGebraExport` traduce dall'albero di `MathParser`:
  prima `a = Slider(min, max, passo)` e `SetValue(a, valore)` per ogni parametro, poi le funzioni con nomi
  inglesi (sen → sin, log → lg) e `*` espliciti. Vale anche per il pulsante GeoGebra dalla casella di testo.
  Provato con `--prova-geogebra` (ora con `g(x) = 1,5x + a`): cursore visibile, retta disegnata.
- Fatto (punto 1): **legenda in stile LaTeX** con `MathLayout` (impaginatore fatto in casa): frazioni,
  esponenti (frazioni in linea dentro gli esponenti), radici, valore assoluto, sen² x, parentesi solo dove
  servono, moltiplicazione sottintesa senza «·» (`BinNode.Implicit`), π ed e come simboli (`ConstNode`).
  `--prova-grafico registro.txt` salva anche `registro.png` con un grafico di formule difficili.
- Fatto (punto 4): **assi col mouse**, solo strumento «Seleziona» e grafico selezionato da solo: trascinamento
  dentro = sposta gli assi (manina), sul bordo (16 px) = sposta il grafico, rotellina = zoom attorno al mouse
  (Ctrl+rotellina resta lo zoom della pagina). Un solo «Annulla» per trascinamento. **Da provare a mano**;
  tocco e penna (fase 5) non ancora. Provato dall'utente col mouse: va bene.
- Fatto (fase 4): **cursori dei parametri** in fondo al grafico, una riga da 44 px per parametro (nome = valore,
  pista, pallino grande, min e max). **Scelta dell'utente: solo con lo strumento «Seleziona»** (anche col grafico
  non selezionato: prenderne un cursore lo seleziona); mouse, dito e penna (`SetupGraphSliders`, registrato per
  primo), a passi di `Step`, un solo «Annulla» per trascinamento. La fascia dei cursori toglie spazio agli assi
  tenendo la scala verticale. Nel PDF (`FunctionGraph.Export`) restano solo i valori. Da fare: cambiare min, max
  e passo (oggi −5…5, 0,1); provarli col dito sulle LIM.
- Icona del pulsante «GeoGebra»: disegnata nel XAML nello stile del logo (ellisse con cinque punti), scelta
  dell'utente; il logo è un marchio di GeoGebra, da verificare se Ardesia venisse distribuita fuori dalla scuola.

Un elemento «grafico» disegnato da Ardesia direttamente sul foglio, vettoriale, che segue lo zoom
della pagina e resta **sotto l'inchiostro**. Affianca GeoGebra, che resta per geometria e casi avanzati.

## Cosa deve fare (scelte dell'utente)

- **Funzioni**: `y = f(x)` (anche più d'una nello stesso grafico, ognuna col suo colore) e rette verticali `x = k`.
- **Parametri con cursore**: ogni lettera diversa da `x` (es. `a`, `b` in `y = a·x² + b`) diventa un cursore
  sotto il grafico, da trascinare col dito o con la penna; il valore resta salvato.
- **Su richiesta, con caselle da spuntare** (tutte spente di default):
  zeri · intersezioni (tra le funzioni e con l'asse y) · massimi e minimi · derivata · segno della funzione
  (zone colorate: tenue sopra l'asse dove f > 0, di altro colore dove f < 0) · griglia · tacche in π.
- **Assi** con frecce ed etichette `x`, `y`, come sul quaderno; numeri sulle tacche.
- **Zoom e spostamento del grafico** (non della pagina) **solo con il grafico selezionato**: due dita = zoom
  degli assi, un dito / penna = sposta gli assi; pulsanti `+`, `−`, «Vista standard».
  Non selezionato si comporta come un'immagine: ci si scrive sopra, si prende col lazo.
- **Inserimento**: dalla casella di testo selezionata, una funzione per riga, come oggi.
  Scrittura a mano: seconda fase (vedi sotto).
- **Pulsante «Grafico»** (barra semplificata e scheda Inserisci) crea il grafico di Ardesia.
  GeoGebra diventa una voce separata «GeoGebra…» (scheda Inserisci e menu contestuale).
  I grafici GeoGebra già salvati continuano ad aprirsi in GeoGebra come oggi.

## Decisi all'approvazione

1. **Barretta sotto il grafico selezionato** (a misura di dito, come la barra delle penne) con `+ − ⌂`,
   le caselle (Zeri, Intersezioni, Max/min, Derivata, Segno, Griglia, π) e «Funzioni…», che apre una piccola
   finestra con una casella di testo (una funzione per riga). Le stesse voci anche nel menu contestuale.
2. **Derivata solo come curva**: numerica (differenze centrali), tratteggiata dello stesso colore, etichetta
   `f'`. Niente formula della derivata (sarebbe un CAS): per quella si usa GeoGebra.
3. **Cursori trascinabili anche col grafico non selezionato**, ma solo toccando il pallino.

## Architettura

### 1. Parser di espressioni – `Editor/Graph/MathExpr.cs` (nuovo, fatto in casa)

- **Normalizzazione** condivisa con GeoGebra: spostare `GeoGebraWindow.ToCommands` in una classe comune
  (`MathText.Normalize`) usata da entrambi: apici `x²` → `x^(2)`, `·` `×` → `*`, `÷` → `/`, `−` `–` → `-`,
  virgola decimale `1,5` → `1.5`.
- Riconosce `y = …`, `f(x) = …`, solo `…` (sottinteso `y =`) e `x = k`.
- **Discesa ricorsiva** → albero (`Num`, `Var`, `Unary`, `Binary`, `Call`), poi compilato in un
  `Func<double, double[], double>` (x, valori dei parametri) per disegnare veloce durante i trascinamenti.
- Precedenze: `+ −` < `* /` < meno unario < `^` (associativo a destra, `-x^2 = -(x^2)`).
- **Moltiplicazione sottintesa**: `2x`, `3(x+1)`, `(x+1)(x-1)`, `2πx`, `x sin x`? → sì per numero·lettera,
  numero·parentesi, parentesi·parentesi, lettera·parentesi solo se la lettera non è una funzione.
- Funzioni: `sin cos tan` (anche `sen`, `tg`), `asin acos atan` (`arcsin`…), `sqrt` / `√`, `abs` / `|x|`,
  `ln`, `log` (base 10), `log_2(x)`?, `exp`, `e^x`, `floor`, `sign`. Costanti `π` / `pi`, `e`.
- Parametri = lettere singole diverse da `x`, `e` e dai nomi di funzione; ordinati alfabeticamente.
- Errori con messaggio in italiano e posizione («Manca una parentesi chiusa», «Non conosco “sinn”»):
  la riga sbagliata si mostra in rosso nella legenda, le altre si disegnano lo stesso.

### 2. Calcolo – `Editor/Graph/GraphMath.cs`

- **Campionamento adattivo** della curva nella finestra visibile: ~1 punto ogni 2 px, suddivisione dove
  l'angolo cambia molto; **spezza la curva** dove il valore non è finito o dove salta oltre l'altezza
  della vista (asintoti di `1/x`, `tan x`) per non tracciare la riga verticale finta.
- **Zeri**: cambi di segno sul campionamento + bisezione; più minimi di |f| vicini a 0 (zeri doppi, `x²`).
- **Intersezioni**: zeri di `f − g` per ogni coppia; con l'asse y: `f(0)`; con `x = k`: `f(k)`.
- **Max/min**: zeri della derivata numerica con cambio di segno, rifiniti con ricerca della sezione aurea.
- Coordinate dei punti arrotondate in modo leggibile (`(1,5; −2)`, con virgola decimale e `;`);
  se un valore è vicino a un multiplo semplice di π o a una frazione semplice, mostrarlo così (`π/2`) – facoltativo.
- **Tacche**: passo 1–2–5 × 10ⁿ scelto dallo zoom; con «π» attivo passo π/k sull'asse x.

### 3. Elemento WPF – `Editor/Graph/FunctionGraph.cs`

- `FunctionGraph : FrameworkElement` con `OnRender(DrawingContext)`: tutto vettoriale (StreamGeometry),
  quindi nitido a ogni zoom della pagina, perché vive dentro `Ink.Children` sotto la stessa `ZoomTransform`.
  Sta **sotto l'inchiostro** come immagini e caselle (gli `InkCanvas.Children` sono già sotto i tratti).
- Proprietà: funzioni (testo + colore), vista (`XMin`, `XMax`, `YMin`, `YMax`), parametri (nome, valore,
  min, max, passo), opzioni (le caselle). Ogni cambio → `InvalidateVisual()`.
- Contenuto: sfondo bianco leggero (o trasparente sullo sfondo scuro, seguendo `BackgroundFactory.IsDark`),
  griglia, assi con frecce ed etichette, curve (spessore ~2,5 px a zoom 100 %, per la LIM), zone del segno,
  punti notevoli con etichetta (cerchio pieno + coordinate in un riquadrino), legenda in alto a sinistra
  con le funzioni scritte «bene» (apici, `·`), cursori in basso.
- **Cursori**: disegnati nella parte bassa dell'elemento (una riga per parametro: nome = valore, pista,
  pallino grande per il dito). Si trascinano **anche con grafico non selezionato**, solo
  sul pallino (zona piccola, non dà fastidio alla scrittura); il resto del grafico resta «carta».
- Il riquadro dell'elemento si ridimensiona con le maniglie della selezione di InkCanvas (come le immagini).

### 4. Salvataggio – `Models/Notebook.cs`, `Editor/ElementFactory.cs`

- Nuovo `Type = "graph"` in `ElementData`; `Content` = JSON compatto dello stato:

  ```json
  { "v": 1,
    "functions": [ { "text": "y = a·x² + b", "color": "#1E5A68" }, { "text": "x = 2", "color": "#C0392B" } ],
    "view": { "xmin": -10, "xmax": 10, "ymin": -7, "ymax": 7 },
    "params": [ { "name": "a", "value": 1, "min": -5, "max": 5, "step": 0.1 } ],
    "show": { "zeros": false, "intersections": false, "extrema": false, "derivative": false,
              "sign": false, "grid": true, "pi": false } }
  ```

  Nessun file in `media/`: non serve. Record `GraphState` (System.Text.Json come il resto di `notebook.json`).
- `ElementFactory.Create` / `ToData`: nuovo `case "graph"`. Un file più vecchio di Ardesia che lo apre
  ignora il tipo sconosciuto (oggi `Create` restituisce `null`): **perde il grafico al primo salvataggio**.
  Da valutare: tenere comunque l'elemento sconosciuto in memoria e riscriverlo invariato (piccola modifica
  da fare **subito**, prima di pubblicare la versione col grafico, così anche le versioni intermedie lo preservano).
- **Annulla/ripeti** gratis: `UndoManager` fotografa gli `ElementData`. Lo zoom degli assi e il trascinamento
  dei cursori fanno uno snapshot **alla fine** del gesto, non a ogni movimento.

### 5. PDF – `Services/PdfExporter.cs`

- Non serve quasi nulla: `Build` ricostruisce la pagina con `ElementFactory.Create(…, export: true)` e la
  rende con `RenderTargetBitmap` a doppia risoluzione, quindi il grafico vettoriale esce nitido.
  Con `export: true` i cursori si disegnano come testo `a = 1,5` (senza pista) e niente barretta.
- Lo stesso vale per il PDF per la classe e per `--snapshot`.

### 6. Editor – `Editor/PageEditor.xaml.cs`

- **Inserimento** (`OpenGraph` diventa `InsertGraph`): prende le righe dalla casella selezionata (come oggi),
  crea il `FunctionGraph` accanto (stessa regola di posizionamento di oggi) e lo seleziona.
  Senza casella selezionata: grafico vuoto con «Funzioni…» già aperta.
- **Selezione e spostamento**: è un `UIElement` in `Ink.Children`, quindi funzionano lazo, `TryBeginMove`,
  copia/incolla, elimina, duplica come per le immagini. `ElementAt` lo deve riconoscere (riquadro intero).
  Attenzione all'insidia nota: gli elementi non sono hit-test visibili fuori dallo strumento Testo → i
  gesti sul grafico si intercettano in `ContentHost.Preview*` controllando se il punto cade sul grafico
  selezionato, come fa lo spostamento della selezione.
- **Gesti con grafico selezionato** (dentro il riquadro, lontano dalle maniglie):
  un dito / penna / mouse = sposta gli assi; due dita = zoom degli assi (anche diverso su x e y?
  → proposta: uniforme, più semplice); rotella del mouse con Ctrl = zoom del grafico.
  Fuori dal riquadro o con grafico non selezionato: tutto come ora (pagina, penne, «dito sposta la pagina»).
  Va provato su entrambe le LIM: su Promethean penna e dito sono entrambi tocco (nessun problema: con
  grafico selezionato muovono gli assi), il palmo arriva come gomma (non deve muovere gli assi).
- **Barretta del grafico selezionato** (vedi «Da decidere», punto 1), costruita come le altre barrette
  a misura di dito; si nasconde all'esportazione e alla deselezione.
- **Menu contestuale** sul grafico: «Funzioni…», le caselle, «Vista standard», «Apri in GeoGebra»
  (converte le funzioni in comandi GeoGebra e crea un grafico GeoGebra a parte: uscita di sicurezza).
- Doppio tocco sul grafico: apre «Funzioni…».

### 7. MainWindow

- «Grafico» → `InsertGraph`; nuova voce «GeoGebra…» → `OpenGraph` di oggi (scheda Inserisci, menu
  contestuale). Barra semplificata: solo «Grafico»? → proposta sì, GeoGebra dal menu contestuale.

## Fasi

1. **Parser + calcolo** con prove automatiche: `Ardesia.exe --prova-grafico` (o piccolo progetto di test)
   che valuta un elenco di espressioni note (`2x+1`, `-x^2`, `x²−4`, `sen x`, `1/x`, `|x−1|`, `√x`, `2πx`,
   `1,5x`) e confronta zeri/estremi attesi.
2. **Elemento sul foglio**: disegno, salvataggio/caricamento, PDF, selezione e spostamento, inserimento dalla
   casella, `--snapshot --demo` con un grafico di prova.
3. **Opzioni**: barretta, caselle, punti notevoli, derivata, segno, griglia, π, «Funzioni…».
4. **Cursori** dei parametri.
5. **Gesti** zoom/spostamento degli assi, prova su Promethean e Newline (con `TouchInject.ps1` e
   `EnablePointerSupport` temporaneo, poi sulle LIM vere).
6. **Scrittura a mano** (seconda fase, come per GeoGebra): un riquadro dove scrivere la funzione;
   riconoscimento con `InkAnalyzer`/riconoscitore matematico di Windows se disponibile senza librerie
   esterne (da verificare: il riconoscitore di espressioni matematiche di Windows non è esposto a WPF
   in modo semplice) → testo proposto, il docente conferma o corregge prima di disegnare.

## Rischi

- **Gesti sul grafico vs. scrittura**: è il punto delicato. Limitarli al grafico selezionato evita di
  disegnare per sbaglio, ma va provato sulle LIM.
- **Prestazioni**: ridisegnare tutto a ogni movimento di cursore con molte funzioni; il campionamento
  compilato e limitato alla larghezza visibile dovrebbe bastare (≈ 1000 valutazioni per curva).
- **Compatibilità**: versioni vecchie di Ardesia cancellano il grafico salvando (vedi §4).
