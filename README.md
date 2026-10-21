# Ardesia

Blocco appunti per lavagne interattive (LIM), ispirato a OneNote, scritto da Giovanni Dal Bo per il Liceo Foscarini, che passa da Microsoft 365 a Google. Applicazione desktop per Windows (WPF / .NET 10).

- **Scaricare**: l'ultima versione è nella pagina [Releases](https://github.com/nanedb/Ardesia/releases) (`ArdesiaSetup-X.Y.Z.exe` si installa senza amministratore; `Ardesia-portatile-X.Y.Z.exe` si avvia senza installare).
- **Guida per i docenti**: nella [wiki](https://github.com/nanedb/Ardesia/wiki) e in un unico PDF, [`docs/wiki/Ardesia-guida.pdf`](docs/wiki/Ardesia-guida.pdf).
- **Segnalazioni**: chat del Dipartimento o giovanni.dalbo@liceofoscarini.it.

## Funzioni

- **Blocchi appunti** salvati in un unico file `.ardesia`, che è un archivio ZIP con struttura JSON, tratti in formato ISF e immagini. All'avvio si riapre l'ultimo blocco appunti. Ogni modifica si salva da sola pochi istanti dopo, e comunque all'uscita, senza domande. Se l'ultimo file non si trova, o al primo avvio, una schermata iniziale chiede di creare un nuovo blocco appunti (scegliendo subito posizione e nome) o di aprirne uno esistente. Ardesia si apre una volta sola: un secondo avvio porta in primo piano quella già aperta, con il file scelto.
- **Google Drive**: con l'account della scuola (accesso dal browser, ricordato in forma cifrata per l'utente di Windows) i blocchi appunti si creano e si aprono direttamente su Drive, nella cartella *Ardesia › classe › materia*, senza Google Drive per desktop. Ardesia lavora su una copia sul PC, quindi il salvataggio resta istantaneo, e carica le modifiche su Drive in sottofondo (dopo una pausa, al massimo ogni 3 minuti, e alla chiusura), anche quando la rete torna dopo un'interruzione. Avvisa se il blocco risulta aperto su un'altra LIM e, se è stato modificato in due posti, tiene entrambe le versioni. Ardesia vede su Drive solo i file che crea lei.
- **Gerarchia libera**: sezioni → sottosezioni → … e pagine → sottopagine → …, senza limiti di profondità. Si riordina dal menu contestuale (tasto destro) o trascinando gli elementi.
- **Foglio infinito** verso destra e verso il basso, con sfondo bianco, a righe (strette o larghe), a quadretti (5 mm, 1 cm o 1,5 cm) o a puntini, su sei colori (compresi «Ardesia» e «Lavagna verde»).
- **Strumenti**: penne, evidenziatori e matite (3H–3B) configurabili, gomma per tratti, gomma precisa (e il palmo come gomma sulle LIM che lo segnalano), linee raddrizzate tenendo ferma la penna, «Inserisci spazio» tra due righe, selezione a lazo (sposta, ridimensiona, cambia colore o spessore), caselle di testo formattate, immagini (da file, appunti o trascinamento), forme (linea, freccia, rettangolo, ellisse, triangolo; con Maiusc si vincolano angoli e proporzioni).
- **Geometria**:
  - **righello** da 30 cm: tracciando lungo un bordo si ottiene una linea perfettamente dritta;
  - **goniometro** con doppia scala;
  - **compasso**: trascina la punta per spostarlo, la matita per cambiare il raggio, la manopola o un punto qualsiasi della pagina per tracciare l'arco.

  Righello e goniometro si spostano e si ruotano con due dita, oppure con la maniglia rotonda.
- **Grafici di funzione** (in sviluppo): il pulsante «Grafico» disegna direttamente sulla pagina le funzioni della casella di testo selezionata, una per riga, scritte come sul quaderno (`y = x² − 4`, `f(x) = 1,5x + a`, `y = sen 2x`, `x = 3`). Senza casella selezionata chiede le funzioni in una finestrella con i simboli più usati. Il grafico è nitido a ogni zoom e nel PDF; con un doppio clic si cambiano le funzioni, dal menu contestuale si mostrano zeri, intersezioni, massimi e minimi, derivata, segno della funzione, griglia e tacche in π, e si ingrandisce o si riporta alla vista standard. Con lo strumento «Seleziona» gli assi si spostano e si ingrandiscono col mouse o con le dita (pizzico), e le lettere diverse da x diventano parametri con un cursore. Le funzioni si possono anche **scrivere a mano** sul foglio (riconoscimento delle formule di Windows; Ardesia mostra sempre che cosa ha letto). Funziona senza internet.
- **Grafici con GeoGebra**: il pulsante «GeoGebra» apre GeoGebra Classic, per la geometria e i casi più avanzati; se è selezionata una casella di testo, ne disegna le funzioni. Il grafico entra nella pagina come immagine e si riapre modificabile con un doppio clic o dal menu contestuale. Un grafico di Ardesia si può aprire in GeoGebra dal suo menu contestuale. Serve la connessione a internet.
- **PDF per la classe**: Ardesia tiene aggiornato da sola un PDF di ogni blocco appunti, accanto al file e con lo stesso nome, perché gli alunni (anche con Mac, iPad e telefoni) non possono aprire i `.ardesia`. Il PDF viene rifatto, se ci sono modifiche, quando si chiude Ardesia o si apre un altro blocco appunti; si possono escludere alcune sezioni, per esempio le verifiche. Per un blocco su Drive il PDF va su Drive accanto al blocco, sempre nello stesso file, e Ardesia lo condivide con il dominio della scuola: il **link per gli alunni non cambia mai** («Copia il link per gli alunni», da mettere su Classroom). Da *File → PDF per la classe…* si può scegliere un'altra posizione o spegnerlo.
- **Esportazione PDF** di una pagina, di una sezione o dell'intero blocco, con segnalibri. Se è installato Google Drive per desktop, le sue cartelle compaiono nella finestra di salvataggio.
- Zoom (Ctrl + rotellina, pizzico con due dita nello strumento «Sposta pagina»), schermo intero (F11), pannelli di navigazione nascondibili, barra degli strumenti completa (a schede) o semplificata (una riga).
- **Pensata per le LIM**: menu contestuale con la pressione prolungata, opzione «Il dito sposta la pagina» per le lavagne che distinguono penna e dito, finestra «Prova del tocco della LIM» per capire come Windows riceve penna, dito e palmo.

## Scorciatoie

| Tasti | Azione |
|---|---|
| Ctrl+S / Ctrl+O | Salva / Apri |
| Ctrl+N | Nuova pagina |
| Ctrl+P | Esporta in PDF |
| Ctrl+Z / Ctrl+Y | Annulla / Ripeti |
| Ctrl+C / Ctrl+X / Ctrl+V | Copia / Taglia / Incolla |
| Ctrl+A | Seleziona tutto |
| Canc | Elimina la selezione |
| F2 (nell'elenco) | Rinomina sezione o pagina |
| F11 | Schermo intero |

## Compilazione

```
dotnet build
dotnet run --project src/Ardesia
```

## Distribuzione

```
.\build-release.ps1
```

Crea nella cartella `dist`:

- **`ArdesiaSetup-X.Y.Z.exe`** (con il numero della versione): installazione per il solo utente corrente, quindi **senza credenziali di amministratore**. Installa in `%LocalAppData%\Programs\Ardesia`, crea le icone nel menu Start (compresa «Ardesia – Prova tocco LIM») e, se richiesto, sul desktop, e associa i file `.ardesia`. Si disinstalla da *Impostazioni → App*.
- **`Ardesia-portatile-X.Y.Z.exe`**: un unico file che si avvia senza installazione, anche da una chiavetta USB.

Entrambi includono .NET e non richiedono altro software. Servono Windows 10/11 a 64 bit: sulle LIM con Android integrato vanno usati sul PC Windows collegato alla lavagna o sul modulo OPS.

## Documentazione

- `docs/wiki/`: la guida per i docenti (pagine della wiki, immagini e PDF). Come pubblicarla nella wiki e rifare il PDF (`python tools/guida-pdf.py`) è spiegato in [`docs/wiki/LEGGIMI.md`](docs/wiki/LEGGIMI.md).
- `docs/google-drive.md`, `docs/grafico-nativo.md`: piani e note di lavoro su Google Drive e sul grafico di funzione.
