# Testo, immagini, copia e incolla

## Caselle di testo

Per scrivere con la tastiera (anche quella a schermo della LIM):

1. scegli lo strumento **Testo** (scheda *Disegna*) oppure *Inserisci → Casella di testo*;
2. **tocca il foglio** dove vuoi scrivere: compare una casella;
3. scrivi.

Il carattere dei testi nuovi è **Latin Modern**, quello dei libri scritti con LaTeX (le caselle scritte con le versioni precedenti tengono il loro). Il formato si cambia dalla scheda **Home**, gruppo *Testo*: tipo di carattere, dimensione, **Grassetto** (Ctrl+B), *Corsivo* (Ctrl+I), Sottolineato (Ctrl+U), **Colore del testo**.

- Per **correggere** un testo, con lo strumento Testo tocca la casella (oppure, con lo strumento Seleziona, fai doppio clic sul testo).
- Per **spostarlo**, con lo strumento **Seleziona** tocca il testo (o circondalo) e trascinalo.
- La casella è **larga quanto il testo**: si allarga mentre scrivi e, arrivata al bordo destro della parte visibile del foglio, va a capo. Se la allarghi o la stringi da un **lato**, la larghezza resta quella che hai scelto; trascinandola da un **angolo** ingrandisci o rimpicciolisci anche il testo. Le caselle scritte con le versioni precedenti restano larghe come erano.
- Si può anche **ruotare**, come le altre selezioni (vedi [Scrivere e disegnare](Scrivere-e-disegnare.md#seleziona-spostare-ingrandire-ruotare-cambiare-colore)).
- Un testo incollato dagli appunti (Ctrl+V) diventa una casella di testo.

### Formule in LaTeX

In una casella di testo si possono scrivere formule in **LaTeX**, tra due **dollari**: quando premi **Invio**, la formula si trasforma e compare impaginata come sul libro, in riga col testo. Il cursore resta sulla stessa riga: per andare a capo premi **Invio** un'altra volta.

| Scrivi | e dopo Invio diventa |
|---|---|
| `L'area è $\pi r^2$` | L'area è *π r²* |
| `$x_{1,2} = \frac{-b \pm \sqrt{b^2-4ac}}{2a}$` | la formula risolutiva, con la frazione e la radice |
| `$\lim_{x \to 0} \frac{\sin x}{x} = 1$` | il limite col «x → 0» sotto |

- Si possono mettere più formule nella stessa riga, in mezzo alle parole. Vanno bene anche i doppi dollari `$$…$$`.
- Una riga scritta **tutta in LaTeX** (per esempio `\int_0^1 x^2 \, dx`) diventa una formula anche senza dollari.
- Si conoscono frazioni, radici, esponenti e pedici, lettere greche, simboli (`\leq`, `\in`, `\infty`, `\Rightarrow`…), limiti, somme e integrali, sistemi (`\begin{cases}…\end{cases}`), matrici (`pmatrix`), vettori (`\vec{v}`), `\mathbb{R}` e le funzioni anche all'italiana (`\sen`, `\tg`). Le parentesi diventano alte quanto serve.
- Un comando scritto male resta in **rosso**: si vede subito l'errore di battitura.
- Per **correggere** una formula: premi **Backspace** subito dopo la formula, oppure fai **doppio clic** sulla formula. Torna il testo con i dollari: correggi e premi di nuovo **Invio**.
- Le formule cambiano dimensione e colore insieme al testo e nel PDF escono nitide.
- Le versioni di Ardesia precedenti alla 0.8.0 mostrano il testo LaTeX al posto della formula, senza perderlo.

Una casella di testo con delle funzioni, una per riga (per esempio `y = x² − 4`, oppure in LaTeX `$y = \frac{1}{x}$`), si trasforma in grafico con **Grafico** o **GeoGebra**: vedi [Grafico di funzione](Grafico-di-funzione.md).

## Immagini

Un'immagine si mette nella pagina in tre modi:

- *Inserisci → Immagine* (o il pulsante immagine della barra semplificata, o «Inserisci un'immagine qui…» nel menu contestuale) e scegli il file (PNG, JPG, BMP, GIF, TIFF);
- **trascinando** il file da Esplora file sulla pagina;
- **incollando** (Ctrl+V) un'immagine copiata, per esempio da una pagina web o da uno strumento di cattura dello schermo.

Le immagini stanno **sotto** l'inchiostro: ci si scrive sopra con le penne. Si selezionano, si spostano, si ridimensionano (dagli angoli senza deformarle) e si ruotano con lo strumento **Seleziona**. Le immagini sono salvate dentro il file `.ardesia`.

## Dalla scrittura a mano al testo

Ciò che hai scritto con la penna si può trasformare in una **casella di testo**: con lo strumento **Seleziona** prendi la scrittura (toccala o circondala) e premi **In testo** nella barretta sotto la selezione, oppure scegli *Trasforma in testo* nel menu contestuale. La casella prende il posto della scrittura, con lo stesso colore e più o meno la stessa grandezza.

Il riconoscimento è quello della grafia italiana di Windows: scrivi in modo chiaro e staccato. Se il testo non è giusto, si corregge come ogni altra casella; con **Ctrl+Z** torna la scrittura a mano.

Se nella selezione c'è un **indirizzo web** (scritto a mano o in una casella, per esempio `www.geogebra.org`), nella barretta compare **Apri …** con il nome del sito: un tocco e si apre nel browser.

## Copia, taglia, incolla, duplica

Con lo strumento **Seleziona** prendi tratti, testi, immagini o grafici, poi:

| Comando | Tasti | |
|---|---|---|
| Copia | Ctrl+C | |
| Taglia | Ctrl+X | |
| Incolla | Ctrl+V | Il contenuto incollato resta selezionato: lo puoi trascinare subito dove vuoi |
| Duplica | – | Dal menu contestuale: una copia poco più in basso a destra, senza toccare gli appunti |
| Elimina | Canc | |

Si può copiare da una pagina e incollare in un'altra, anche di un altro blocco appunti.

## Il menu contestuale

Si apre con il **clic destro** del mouse o, sulla LIM, con la **pressione prolungata**: tieni il dito (o la penna) fermo per quasi un secondo; compare un **cerchietto** e il menu si apre **quando alzi il dito**. Il menu compare un po' a destra del dito, per non coprire la prima voce.

Le voci cambiano a seconda di che cosa c'è sotto il dito.

**Su uno spazio vuoto:**

- Incolla qui
- Scrivi un testo qui
- Inserisci un'immagine qui…
- Seleziona tutto

**Su un oggetto o una selezione:**

- Taglia, Copia, Incolla qui, **Duplica**, Elimina
- **Ruota di 90° a destra**, **Ruota di 90° a sinistra**
- **Porta in primo piano**, **Porta in fondo** (quale oggetto sta sopra l'altro)
- Seleziona tutto

e in più, a seconda dell'oggetto:

| Oggetto | Voci in più |
|---|---|
| Casella di testo | **Apri il link** (se contiene un indirizzo web) · **Disegna il grafico di questa funzione** · **Disegna il grafico con GeoGebra** |
| Tratti scritti a mano | **Trasforma in testo** · **Apri il link** (se è scritto un indirizzo web) · **Disegna il grafico della funzione scritta** · **Disegna il grafico con GeoGebra** (vedi [Scrivere a mano le funzioni](Scrittura-a-mano-delle-funzioni.md)) |
| Grafico di Ardesia | Funzioni del grafico…, le opzioni da spuntare, zoom, Vista standard, Apri in GeoGebra (vedi [Grafico di funzione](Grafico-di-funzione.md#il-menu-del-grafico)) |
| Grafico GeoGebra | **Modifica il grafico in GeoGebra** |

> Per **eliminare** un oggetto col dito: tienilo premuto e scegli *Elimina*.
