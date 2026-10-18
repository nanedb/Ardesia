# Grafico di funzione nativo – piano

Stato: **approvato dall'utente (16/10/2026)**, implementazione cominciata.

- Fatto: fasi 1 e 2 (parser, calcolo, `--prova-grafico`, elemento `"graph"` con salvataggio, PDF, inserimento
  da «Grafico», GeoGebra come pulsante a parte), tutte le opzioni già disegnate e attivabili dal menu
  contestuale, finestrella «Funzioni del grafico» (doppio clic), oggetti sconosciuti conservati (§4).
- Da fare: barretta sotto il grafico selezionato (fase 3), cursori trascinabili (fase 4; per ora i
  parametri valgono 1 e sono scritti nella legenda), gesti sugli assi (fase 5), scrittura a mano (fase 6).
  Aggiornamento 17/10/2026: cursori e scrittura a mano fatti (vedi sotto).

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

## Fase 6 – scrittura a mano (proposta approvata dall'utente il 17/10/2026, fatta lo stesso giorno)

**Fatto**:
- `Graph/HandwritingMath.cs`: `IsAvailable` (controlla nel registro `micaut.dll` e l'inchiostro COM),
  `SplitLines` (fasce orizzontali; la linea di frazione, un tratto piatto e largo, tiene attaccati numeratore e
  denominatore), `RecognizeAsync` (controllo fuori schermo, `LoadInk`, «Insert» ripremuto ogni 0,4 s finché arriva
  l'evento, al massimo ~5 s), `Recognize` sincrono per le prove, `Trace` per il registro.
- `Graph/MathMLText.cs`: MathML → testo (`x²`, `(x + 1)/(x − 2)`, `√(x)`, `(x − 1)²`, `x^(a + 1)`, `(3/2)x`,
  segno meno davanti) con le correzioni `s e π` → `sen`, `t₉` → `tg`, `ⅇ` → `e`, `Y` → `y`, `F(x)` → `f(x)`.
- Finestrella: **anteprima composta** (`MathLayout`) sotto ogni riga, avviso in evidenza (`note`), pulsante
  **«✍ Scrivi a mano»** con la striscia (si legge 1,1 s dopo l'ultimo tratto; le righe lette vanno nelle righe vuote
  o in righe nuove e spariscono dalla striscia; «Pulisci»). Pulsante spento con spiegazione se Windows non ha il
  riconoscitore.
- Foglio: «Grafico» (o la voce del menu contestuale «Disegna il grafico della funzione scritta») con **tratti
  selezionati** e nessuna casella di testo → lettura, finestrella con le righe lette e l'avviso, grafico **accanto
  alla scrittura** (`InsertGraphFromHandwriting`, `PlaceNewGraph`).
- Prove: `--prova-grafico` controlla 13 traduzioni MathML, la divisione in righe e 5 formule scritte col «font a
  tratto» (`GraphSelfTest.StrokeFont`) lette dal riconoscitore vero (5 righe in ~1,5 s). Istantanea:
  `--snapshot out.png --tab "Scrivi a mano"` (scrive due funzioni nella striscia e le fa leggere davvero).
- **Da provare a mano** sulle LIM: scrittura vera (non sintetica) col dito e con la penna, lazo + «Grafico»,
  striscia nella finestrella; e `Ardesia.exe --prova-grafico` sui PC delle LIM per sapere se hanno `micaut.dll`.

### Proposta

### Che cosa c'è in Windows (provato su questo PC, Windows 11 26300, senza librerie esterne)

Programma di prova (poi diventato `--prova-grafico`): inchiostro **sintetico** (un piccolo «font a tratto» fatto a mano, quindi più
pulito della scrittura vera) passato ai riconoscitori via COM, con `dynamic`, senza riferimenti a pacchetti.

| Tecnologia | Esito |
|---|---|
| **Math Input Control** (`micaut.dll`, COM `micaut.MathInputControl`, quello del vecchio «Pannello di input matematico») | **Riconosce la matematica** e restituisce **MathML**: `y = x² − 4` (con `msup`), `f(x) = 2x + 1`, `y = 1/x` e `y = (x+1)/(x−2)` come **frazioni vere** (`mfrac`), `y = √x` (`msqrt`), `f(x) = x³ − 3x`, `y = x² − 4x + 3`, `y = sin x` (come funzione). Sbaglia: **«sen» e «tg»** (non li conosce: `s e Π`, `t₉`; `AddFunctionName("sen")` non aiuta), lettere isolate ambigue (la mia `a` → `∂`). **Una sola espressione**: due righe insieme danno un pasticcio |
| `mraut.MathRecognizer` (il motore sotto) | Non usabile: «la classe non dispone della licenza» (riservato a `micaut`) |
| Riconoscitore di **testo** italiano (COM Tablet PC `InkRecognizerContext`, `InkObj.dll`) | Riconosce testo con alternative: `Y=2x+1`, `y=senx`, `Y=1/x`; ma **perde gli esponenti** (`x2`), le frazioni diventano simboli a caso, `(` letta come `1` |
| WinRT `Windows.UI.Input.Inking` (`InkRecognizerContainer`, `InkAnalyzer`) | Non provato: serve il TFM `net10.0-windows10.0.x`, che **scarica da NuGet** il pacchetto di proiezione (~25 MB in più nell'eseguibile) e usa lo **stesso motore di testo** del COM: niente in più per la matematica |
| `InkAnalyzer` di WPF (`IAWinFX.dll`, `IACore.dll`) | Non c'è: era nel vecchio Tablet PC SDK, non in .NET né in Windows |

Il Math Input Control ha una sua finestra (in inglese: Write, Erase, Select and Correct, Clear, Insert) e dà il
risultato solo quando si preme «Insert». Provato: si può **tenere fuori dallo schermo**, caricargli i tratti
(`LoadInk`), premere «Insert» con UI Automation e ricevere il MathML dall'evento `Insert`: circa **1–2,5 s** a riga
(gran parte è la ricerca della finestra con UI Automation, si può accorciare). Senza `Show()` non funziona.

### Proposta

**Tecnologia**: Math Input Control, nascosto; Ardesia mostra solo la sua interfaccia.
Si scrive **con le penne di Ardesia** (non nella finestra di Microsoft), così funziona come il resto su Promethean
(tutto tocco) e Newline (penna vera), con palmo-gomma e tutto quanto.

**Dove si scrive** – due ingressi, stessa finestrella di conferma:

1. **Sul foglio** (il caso principale, il più naturale alla LIM): il docente scrive `y = x² − 4` con la penna,
   la prende col lazo e preme **«Grafico»** (o la voce del menu contestuale «Disegna il grafico di questa
   funzione»). Come oggi per la casella di testo selezionata: le righe scritte diventano le funzioni e il grafico
   va **accanto alla scrittura**, che resta sul foglio com'è.
2. **Nella finestrella «Funzioni del grafico»**: pulsante **«✍ Scrivi»** che apre sotto le righe una striscia
   bianca grande dove scrivere; quando si alza la penna e passa un attimo (~1 s) la funzione riconosciuta va in una
   riga nuova e la striscia si pulisce. Utile per aggiungere una funzione a un grafico che c'è già.

**Più righe**: il riconoscitore vuole un'espressione per volta, quindi Ardesia divide i tratti in righe
(fasce orizzontali separate da uno spazio vuoto; frazioni ed esponenti restano nella loro riga perché i pezzi si
sovrappongono in orizzontale) e le riconosce una alla volta.

**Come si mostra e si corregge**: si apre la finestrella «Funzioni del grafico» con le righe già riempite
(testo modificabile, come oggi) e, accanto a ciascuna, la **formula composta** con `MathLayout` (come nella
legenda), così si vede subito se `x²` è diventato `x2`. Le righe che il parser non capisce sono in rosso come oggi.
Si corregge **con la tastiera** (o la tastiera virtuale della LIM) oppure **riscrivendo** nella striscia «✍ Scrivi».
«OK» disegna il grafico, «Annulla» non fa nulla. Nessun grafico senza conferma.

**Traduzione MathML → testo di Ardesia** (nuovo `Editor/Graph/MathMLText.cs`): `msup` → `^`, (`x^2` → `x²` per
leggibilità), `mfrac` → `(num)/(den)` con parentesi solo dove servono, `msqrt` → `√(…)`, `mroot`, `mfenced`;
correzioni note: lettere `s e n`/`s e Π` → `sen`, `t₉`/`t g` → `tg`, `ⅇ` → `e`, `Y` → `y`, `F(x)` → `f(x)`,
`×`/`·` → moltiplicazione, virgola decimale lasciata così (il parser la capisce).

**Prove automatiche**: in `--prova-grafico` il «font a tratto» della prova (cifre, x, y, f, =, +, −, parentesi,
frazione, radice, esponente) scrive alcune funzioni, le passa al riconoscitore vero e controlla il testo che
esce (`y = x² − 4`, `y = 1/x`…). Se `micaut` manca la prova lo dice e non fallisce. La traduzione MathML → testo
ha prove sue, indipendenti da Windows.

### Limiti e rischi

- **Componente vecchio e non documentato per questo uso**: il Math Input Control è dell'epoca di Windows 7; il
  programma «Pannello di input matematico» è già stato tolto da Windows 10, il controllo invece c'è ancora (qui
  versione 10.0.26100). Potrebbe sparire in un aggiornamento futuro. Premere «Insert» con UI Automation è un
  trucco: se Microsoft cambia la finestra, smette di funzionare. Ardesia controlla all'avvio della funzione se il
  componente c'è; se manca, il pulsante dice «Su questo PC Windows non ha il riconoscimento delle formule» e
  si scrive con la tastiera. **Da verificare sui PC delle LIM** (Promethean e Newline/OPS): basta lanciare
  `Ardesia.exe --prova-grafico` lì.
- Riconosce solo **il risultato migliore** (niente elenco di alternative da toccare): per correggere si
  riscrive o si usa la tastiera. Le alternative le dà solo il riconoscitore di testo, che però non vede apici e
  frazioni.
- **sen, tg, arcsen** vanno sistemati dopo, con le regole sopra; nomi poco comuni o lettere isolate (parametri
  `a`, `b`, `k`) possono uscire sbagliati: per questo la conferma è obbligatoria.
- La finestra fuori schermo per ~1 s: non dovrebbe vedersi; da controllare con due schermi (si mette fuori da
  tutti gli schermi).
- Tempo: 1–2,5 s per riga, con il cerchietto d'attesa.
- Il riconoscitore è inglese: la virgola decimale va (provato `2,5x`), il punto e virgola dei punti no (non serve).

### 18/10/2026

- Scrittura a mano: `sin⁴(x)` usciva senza esponente (il riconoscitore dà `msup` con base `sin`, `MathMLText`
  metteva la funzione tra parentesi). Corretto, con prove (anche `sen² x`, `cos³(2x)`).
- Segnalato dall'utente sul laptop Samsung con penna: il **pizzico** sul grafico selezionato zoomava tutta la pagina.
  Primo pezzo della fase 5: con «Il dito sposta la pagina» attivo e lo strumento «Seleziona», un gesto che comincia
  dentro il grafico selezionato (non sul bordo) muove gli assi: un dito li sposta, due dita li zoomano
  (`FingerGraphGesture`, un solo «Annulla»). Il tocco breve resta un tocco. Provato dall'utente sul laptop: funziona; con
  «Il dito sposta la pagina» spento (Promethean) il tocco sul grafico non muove ancora gli assi.

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
