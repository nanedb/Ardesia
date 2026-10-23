# Installazione

Ardesia si distribuisce in due forme, con il numero della versione nel nome del file (qui **0.7.2**):

| File | Che cos'è | Quando usarlo |
|---|---|---|
| `ArdesiaSetup-0.7.2.exe` | Programma di installazione | Sul proprio PC e sui PC delle LIM: crea le icone, associa i file `.ardesia`, si disinstalla come le altre app |
| `Ardesia-portatile-0.7.2.exe` | Un unico file che si avvia senza installare nulla | Da una chiavetta USB, o su un PC dove non si vuole (o non si può) installare |

Entrambe contengono già tutto quello che serve: **non bisogna installare .NET né altri programmi**. Serve Windows 10 o 11 a 64 bit.

**Dove si scaricano:** nella pagina delle versioni del progetto su GitHub, <https://github.com/nanedb/Ardesia/releases>. In cima c'è l'ultima versione: sotto *Assets* clicca su `ArdesiaSetup-….exe` (o sulla versione portatile). Non serve un account GitHub.

## Installare con `ArdesiaSetup`

1. Fai doppio clic su `ArdesiaSetup-0.7.2.exe`.
2. Se compare la finestra blu **«Windows ha protetto il PC»** vedi [più sotto](#lavviso-windows-ha-protetto-il-pc).
3. **Non servono le credenziali di amministratore**: Ardesia si installa solo per l'utente con cui sei entrato in Windows. Se compare la finestra **«Seleziona modo installazione»**, scegli **«Installa solo per l'utente attuale (suggerito)»**.
4. Puoi spuntare **«Crea un'icona sul desktop»**.
5. Alla fine lascia spuntato **«Avvia Ardesia»** e premi *Fine*.

Che cosa viene installato:

- il programma in `%LocalAppData%\Programs\Ardesia` (nella cartella del tuo utente);
- nel menu Start le voci **Ardesia** e **Ardesia – Prova tocco LIM** (la [prova del tocco](Uso-sulle-LIM.md#la-prova-del-tocco-della-lim));
- l'icona sul desktop, se l'hai chiesta;
- l'associazione dei file `.ardesia`: un doppio clic su un blocco appunti lo apre in Ardesia.

**Sui PC delle LIM** ogni docente entra in Windows con il proprio utente, quindi **ognuno installa Ardesia per sé** (pochi secondi, senza amministratore).

### Aggiornare a una versione nuova

Dalla versione 0.7.2 Ardesia controlla da sola, una volta al giorno, se c'è una versione nuova. Quando c'è, in alto a destra compare il pulsante **«Nuova versione X.Y.Z»**: cliccalo quando vuoi, anche a fine lezione. Si apre una finestrella con le novità e il pulsante **«Aggiorna adesso»**: Ardesia scarica l'aggiornamento, salva, si chiude, si aggiorna e si riapre da sola in circa un minuto. Non servono le credenziali di amministratore. Se preferisci aspettare, premi «Più tardi»: il pulsante resta nella barra.

Puoi anche aggiornare a mano: scarica il nuovo `ArdesiaSetup-X.Y.Z.exe` ed eseguilo, e sostituisce la versione precedente. In entrambi i casi i tuoi blocchi appunti e le tue impostazioni (penne, ultimo file aperto…) restano.

Con la versione portatile «Nuova versione» apre la pagina da cui scaricare il file nuovo, da usare al posto del vecchio.

> **Importante – aggiornate tutti i PC.** Le versioni **0.3.2 e precedenti** non conoscono il [grafico di funzione](Grafico-di-funzione.md): se aprono un blocco appunti che contiene grafici, **li cancellano al primo salvataggio**. Se usi i grafici, aggiorna Ardesia su **tutti** i PC dove apri quel blocco (casa, LIM delle varie aule, portatile). La versione è scritta **in alto, accanto al nome del blocco appunti** (per esempio «0.7.2»).

Se Ardesia è aperta mentre installi la versione nuova, l'installazione chiede di chiuderla: chiudila (si salva da sola) e prosegui.

### Disinstallare

*Impostazioni di Windows → App → App installate → Ardesia → Disinstalla*. I blocchi appunti (i file `.ardesia`) non vengono cancellati.

## La versione portatile

`Ardesia-portatile-0.7.2.exe` si avvia con un doppio clic, anche da una chiavetta USB. Differenze rispetto all'installazione:

- non crea icone nel menu Start né sul desktop;
- non associa i file `.ardesia` (per aprirli usa *File → Apri…* dentro Ardesia);
- le impostazioni (penne, ultimo file…) si salvano comunque **sul PC** dove la usi, nel profilo del tuo utente Windows, non sulla chiavetta.

Il primo avvio può richiedere qualche secondo in più.

## L'avviso «Windows ha protetto il PC»

Ardesia non è firmata digitalmente (la firma costa ed è pensata per i programmi in vendita), quindi la prima volta Windows SmartScreen può mostrare una finestra blu:

> **Windows ha protetto il PC** – Microsoft Defender SmartScreen ha impedito l'avvio di un'app non riconosciuta…

Per proseguire:

1. clicca su **Ulteriori informazioni**;
2. compare il nome del file e il pulsante **Esegui comunque**: cliccalo.

L'avviso compare solo la prima volta per ogni file scaricato. Se il pulsante *Esegui comunque* non c'è, o se l'antivirus blocca il file, il PC è protetto da regole della scuola: chiedi al tecnico (e avvisa Giovanni).

## GeoGebra e WebView2

Il pulsante [GeoGebra](GeoGebra.md) usa **Microsoft Edge WebView2**, già presente in Windows 11 e in quasi tutti i Windows 10 aggiornati. Se manca, Ardesia lo dice nella finestra di GeoGebra: si scarica gratis dal sito Microsoft (cerca «WebView2 Runtime»). Tutto il resto di Ardesia funziona anche senza.
