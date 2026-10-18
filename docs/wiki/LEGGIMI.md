# Bozza della guida per i docenti

Questa cartella contiene la **bozza** della documentazione di Ardesia per i colleghi (non per sviluppatori):

- le pagine Markdown pronte per la **wiki GitHub** di `nanedb/Ardesia` (`Home.md`, `_Sidebar.md` e una pagina per argomento);
- `immagini/`: le istantanee usate nelle pagine (fatte con `Ardesia.exe --snapshot … --demo --tab …`);
- `Ardesia-guida.pdf`: lo stesso contenuto in un unico PDF impaginato.

Le note «⚠ Da verificare» segnano i punti da controllare prima di pubblicare.

## Pubblicare sulla wiki (quando la bozza è approvata)

La wiki è un repository git a parte (`https://github.com/nanedb/Ardesia.wiki.git`, da creare aprendo la prima pagina dal sito). Basta copiarci le pagine `.md` (tranne questo `LEGGIMI.md`) e la cartella `immagini/`, poi fare commit e push. I collegamenti fra le pagine usano i nomi dei file con `.md` (per esempio `[Installazione](Installazione.md)`), così funzionano anche aprendo la cartella `docs/wiki` su GitHub. Copiandole nella wiki, se un collegamento non trova la pagina, togliere `.md` dai collegamenti fra le pagine.

Attenzione: il repository è **privato**, quindi anche la wiki lo è; per i colleghi probabilmente basta il PDF (o la wiki va resa leggibile in altro modo).

## Rifare il PDF

Il PDF è generato fuori dal progetto (nessuna dipendenza aggiunta ad Ardesia): Markdown → HTML con il pacchetto Python `markdown`, stampa in PDF con Microsoft Edge senza finestra (`--headless=new --print-to-pdf`), poi numeri di pagina e segnalibri con `pymupdf`. Lo script `make_pdf.py` è nella cartella delle note della sessione che ha scritto la bozza.
