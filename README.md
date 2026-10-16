# Ardesia

Blocco appunti per lavagne interattive (LIM), ispirato a OneNote. Applicazione desktop per Windows (WPF / .NET 10).

## Funzioni

- **Blocchi appunti** salvati in un unico file `.ardesia`, che è un archivio ZIP con struttura JSON, tratti in formato ISF e immagini. All'avvio si riapre l'ultimo blocco appunti. Ogni modifica si salva da sola pochi istanti dopo, e comunque all'uscita, senza domande. Se l'ultimo file non si trova, o al primo avvio, una schermata iniziale chiede di creare un nuovo blocco appunti (scegliendo subito posizione e nome) o di aprirne uno esistente.
- **Gerarchia libera**: sezioni → sottosezioni → … e pagine → sottopagine → …, senza limiti di profondità. Si riordina dal menu contestuale (tasto destro) o trascinando gli elementi.
- **Foglio infinito** verso destra e verso il basso, con sfondo bianco, a righe (strette o larghe), a quadretti (5 mm o 1 cm) o a puntini, su sei colori (compresi «Ardesia» e «Lavagna verde»).
- **Strumenti**: penna, evidenziatore, gomma per tratti, gomma precisa, selezione a lazo (sposta, ridimensiona, cambia colore o spessore), caselle di testo formattate, immagini (da file, appunti o trascinamento), forme (linea, freccia, rettangolo, ellisse, triangolo; con Maiusc si vincolano angoli e proporzioni).
- **Geometria**:
  - **righello** da 30 cm: tracciando lungo un bordo si ottiene una linea perfettamente dritta;
  - **goniometro** con doppia scala;
  - **compasso**: trascina la punta per spostarlo, la matita per cambiare il raggio, la manopola o un punto qualsiasi della pagina per tracciare l'arco.

  Righello e goniometro si spostano e si ruotano con due dita, oppure con la maniglia viola.
- **Grafici con GeoGebra**: il pulsante «Grafico» apre la calcolatrice grafica di GeoGebra; se è selezionata una casella di testo, ne disegna le funzioni (una per riga, per esempio `y = x^2 - 3x + 2`). Il grafico entra nella pagina come immagine e si riapre modificabile con un doppio clic o dal menu contestuale. Serve la connessione a internet.
- **PDF per la classe** (*File → PDF per la classe…*): Ardesia tiene aggiornato da sola un PDF del blocco appunti in una cartella scelta, per esempio una cartella di Google Drive condivisa con gli alunni, che così lo leggono da qualsiasi dispositivo (anche Mac, iPad e telefoni). Il PDF viene rifatto, se ci sono modifiche, quando si chiude Ardesia o si apre un altro blocco appunti; si possono escludere alcune sezioni, per esempio le verifiche.
- **Esportazione PDF** di una pagina, di una sezione o dell'intero blocco, con segnalibri. Se è installato Google Drive per desktop, le sue cartelle compaiono nella finestra di salvataggio.
- Zoom (Ctrl + rotellina, pizzico con due dita nello strumento «Sposta pagina»), schermo intero (F11), pannelli di navigazione nascondibili.

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

- **`ArdesiaSetup.exe`**: installazione per il solo utente corrente, quindi **senza credenziali di amministratore**. Installa in `%LocalAppData%\Programs\Ardesia`, crea le icone nel menu Start (compresa «Ardesia – Prova tocco LIM») e, se richiesto, sul desktop, e associa i file `.ardesia`. Si disinstalla da *Impostazioni → App*.
- **`Ardesia-portatile.exe`**: un unico file che si avvia senza installazione, anche da una chiavetta USB.

Entrambi includono .NET e non richiedono altro software. Servono Windows 10/11 a 64 bit: sulle LIM con Android integrato vanno usati sul PC Windows collegato alla lavagna o sul modulo OPS.
