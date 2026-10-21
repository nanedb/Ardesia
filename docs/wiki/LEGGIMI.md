# La guida per i docenti

Questa cartella contiene la documentazione di Ardesia per i colleghi (non per sviluppatori):

- le pagine Markdown della **wiki GitHub** di `nanedb/Ardesia` (`Home.md`, `_Sidebar.md` e una pagina per argomento);
- `immagini/`: le istantanee usate nelle pagine (fatte con `Ardesia.exe --snapshot … --demo --tab …`);
- `Ardesia-guida.pdf`: lo stesso contenuto in un unico PDF impaginato.

Qui è la copia di riferimento: si modifica qui e poi si copia nella wiki.

## Pubblicare sulla wiki

La wiki è un repository git a parte: `https://github.com/nanedb/Ardesia.wiki.git`. Ci vanno le pagine `.md` (tranne questo `LEGGIMI.md`) e la cartella `immagini/`, poi commit e push. Nelle pagine qui i collegamenti fra le pagine hanno `.md` (`[Installazione](Installazione.md)`), così funzionano anche aprendo `docs/wiki` su GitHub; nella wiki invece vanno **senza** `.md` (`[Installazione](Installazione)`): copiandole si toglie `.md` dai collegamenti fra le pagine (le ancore `#…` restano).

## Rifare il PDF

```
python tools/guida-pdf.py
```

Serve Python con i pacchetti `markdown` e `pymupdf` (meglio in un venv fuori dal progetto: nessuna dipendenza viene aggiunta ad Ardesia) e Microsoft Edge. Lo script converte le pagine in HTML, le stampa in PDF con Edge senza finestra (`--headless=new --print-to-pdf`) e aggiunge numeri di pagina e segnalibri. La versione in copertina si legge da `src/Ardesia/Ardesia.csproj`.

## Punti ancora aperti

Nella guida questi punti sono scritti in modo prudente; quando si saprà di più vanno aggiornati:

- sulle LIM vere: doppio tocco sul grafico, cursori e gesti col dito, trascinamento nell'elenco di sezioni e pagine col dito, scrittura a mano delle funzioni (il riconoscimento non è ancora affidabile, vedi `docs/grafico-nativo.md`), presenza di `micaut.dll`;
- Google Drive sulle LIM della scuola: primo accesso col browser, rete della scuola;
- il link del PDF per la classe aperto da un account di alunno;
- SmartScreen e antivirus sui PC della scuola gestiti centralmente;
- il nome esatto della voce «Gestisci versioni» sul sito di Drive per i file che non sono di Google.
