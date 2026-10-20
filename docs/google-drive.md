# Blocchi appunti su Google Drive (senza Drive per desktop) – piano

Stato: **fase 1 (accesso) fatta il 19/10/2026, rilasciata nella 0.6.0 (20/10/2026)**; **fase 2 (Drive di base) scritta e
provata il 20/10/2026**; **fase 3 (robustezza) scritta il 20/10/2026**; da rilasciare insieme alla fase 4. Per il PDF condiviso (fase 4) si aspettano gli account
`@liceofoscarini.it` degli alunni (l'amministratore li sta creando).
Domande aperte risolte.

**Fase 0 fatta (17/10/2026)**: progetto `ardesia-511918`, client desktop
`774277790696-1g2mib0ps95832gutce7c931t5alb5fr.apps.googleusercontent.com` (il JSON col secret è fuori dal
repository, vedi `.gitignore`). `Ardesia.exe --prova-google <client.json>` (`GoogleTestWindow`) è passata tutta:
app non bloccata dall'amministratore, account `@liceofoscarini.it`, `drive.file`, refresh token, Drive API attiva,
crea/elenca/aggiorna/scarica un file col tipo di Ardesia, condivisione col dominio permessa.
**Non serve l'amministratore.** Già scritti e riusabili per la fase 1: `Services/Google/GoogleClientConfig.cs`,
`GoogleAuth.cs` (PKCE + loopback con `TcpListener`), `DriveApi.cs`.

**Fase 1 (accesso) scritta (19/10/2026)**, ripresa perché non dipende dagli account degli alunni:
- `Services/Google/TokenStore.cs`: account (refresh token, e-mail, nome, dominio, permessi, data) in JSON cifrato
  con DPAPI via P/Invoke a `crypt32` (`CryptProtectData`, utente corrente + entropia fissa), in
  `%LocalAppData%\Ardesia\google-account.bin`, scrittura atomica.
- `Services/Google/GoogleAccountService.cs` (`GoogleAccountService.Current`): `SignInAsync` (controlla dominio
  `@liceofoscarini.it`, permesso `drive.file` concesso e refresh token, altrimenti revoca e spiega),
  `GetAccessTokenAsync` (rinnovo a un minuto dalla scadenza; `invalid_grant` → account dimenticato),
  `SendAsync` (richiesta autenticata, con 401 rinnova e riprova una volta), `SignOutAsync` (revoca + cancella il
  file; senza rete dimentica lo stesso e avvisa), evento `Changed`. L'access token resta solo in memoria.
- Client: `GoogleClientConfig.Load` cerca `google-client.json` / `client_secret_*.json` accanto all'exe e in
  `%AppData%\Ardesia` (per le prove: `%AppData%\Ardesia\google-client.json`), altrimenti usa `GoogleConfig.cs`
  (**scelta dell'utente: ID client e secret incorporati nel codice**, così sulle LIM non serve copiare file).
- Interfaccia: Impostazioni › Questo PC › «Google Drive» (stato, «Accedi con Google» / «Annulla» / «Esci»;
  valgono subito, anche con Annulla); nella barra in alto il nome di chi è collegato, con menu «Esci da Google».
- Prova `Ardesia.exe --prova-accesso [registro.txt]` (`AccessTestWindow`): primo avvio → browser, accesso, file
  cifrato, rilettura dal disco; avvii seguenti → account ricordato e rinnovo senza browser; poi userinfo, rinnovo
  dopo un 401 finto, pulsante «Esci da Google (revoca)» che verifica cancellazione e rifiuto del vecchio gettone.
- **Provata il 19/10/2026** con `giovanni.dalbo@liceofoscarini.it`: `--prova-accesso` tutta OK (accesso, file
  cifrato, riavvio senza browser, rinnovo dopo 401, «Esci» con vecchio gettone rifiutato); nell'app vera accesso da
  Impostazioni, nome nella barra, ancora collegato dopo chiusura e riapertura, «Esci da Google» dalla barra.
- Non fatto in fase 1: «Esci» non carica code (non ci sono ancora), «Svuota la cache di Drive» (fase 2).

**Fase 2 (Drive di base) scritta e provata il 20/10/2026** (ramo `claude/google-drive-fase-2-372a7a`, non ancora rilasciata):
- `Services/Google/DriveApi.cs`: interfaccia `IDriveClient`; costruttore con `GoogleAccountService` (gettone rinnovato,
  401 → riprova; `SendAsync` accetta ora un HttpClient e `HttpCompletionOption`); elenco a pagine; caricamento
  **riprendibile** a pezzi sopra 5 MB (pezzi da 2 MB, se un pezzo non arriva si chiede a Google fin dove è arrivato);
  download con avanzamento; `IsTransient` (rete, 408, 429, 5xx, limiti di frequenza).
- `Services/Google/DriveSync.cs` (`DriveSync.Current`, uno per account, sul thread della UI): cache in
  `%LocalAppData%\Ardesia\Drive\{hash e-mail}\{fileId}.ardesia` + `sync.json` (titolo, nome su Drive, cartella,
  revisione di base, «da caricare», ultimo caricamento/errore, elenco delle cartelle per classi e materie senza rete),
  scritto in modo atomico. Coda: ~30 s dopo l'ultima modifica, al massimo ogni ~3 min, `UploadSoon` al cambio di
  blocco; istantanea letta sul thread della UI tra un salvataggio e l'altro; tentativi a 5 s, 15 s, 1 min, 5 min;
  403/404 → avviso, niente più tentativi; dopo un riavvio i «da caricare» ripartono. Cartelle `Ardesia/{classe}/{materia}`
  riusate senza distinguere maiuscole (cartella «Ardesia» cestinata → se ne crea un'altra), nomi con i caratteri non
  ammessi → «-», anno scolastico che cambia a settembre. Apertura (§4.3): stessa revisione → cache; cambiata → download;
  cambiata anche qui → si tiene la cache (conflitti veri: fase 3); senza rete → cache con avviso, o messaggio se manca.
  Blocco nel cestino / 404 → `DriveGoneException` (via da recenti e cache; con modifiche non caricate si offre una
  copia sul computer). **Rinomina** (scelta dell'utente 20/10/2026): il nome nuovo va su Drive col caricamento.
  **Eliminare** (scelta dell'utente 20/10/2026, già in fase 2): cestino di Drive, cartelle mai; la domanda sul PDF
  arriva con la fase 4.
- Interfaccia: `NewDriveNotebookDialog` (classe e materia scritte a mano, quelle già usate come pulsanti, nome proposto
  modificabile, percorso su Drive mostrato sotto, «Crea invece sul computer…»), `DriveBrowserWindow` («Apri da
  Drive…»: cerca, Apri, Elimina…, Nuovo…, con la frase sui blocchi di `G:`), `Controls/BusyDialog` (attesa con
  «Annulla», compare dopo 350 ms). `StartWindow`: da collegati «Nuovo» va su Drive, sezione «Su Google Drive» con
  gli ultimi 5 blocchi e «Tutti i blocchi su Drive…», «Nuovo blocco appunti sul computer…»; da scollegati «Accedi con
  Google»; recenti con la nuvola. Menu File: «Nuovo blocco appunti su Drive…», «…sul computer…», «Apri da Drive…»,
  «Apri dal computer…», recenti con nome e cartella, «Salva una copia sul computer…», «Apri la cartella su Google
  Drive», «Elimina il blocco appunti…». Barra: nuvola + «Su Drive · salvato alle 10:32» / «Salvato su questo PC · da
  caricare su Drive» / «Caricamento su Drive…» / «Senza connessione…» / «Accedi a Google per caricare le modifiche».
  Titolo «Nome – Google Drive (e-mail) – Ardesia». Impostazioni: «Svuota la cache» (tiene quelle da caricare e quella
  aperta); nella scheda del blocco, dove si trova su Drive. All'avvio si riapre l'ultimo blocco su Drive (scaricandolo
  se serve); se non si è collegati la schermata iniziale dice di accedere.
- Il codice di `MainWindow` per Drive è in `MainWindow.Drive.cs`; in `MainWindow.xaml.cs` solo agganci piccoli
  (`_path` resta la copia in cache, quindi salvataggio automatico, di emergenza e `NotebookStore` non cambiano).
- **PDF per la classe**: per i blocchi su Drive non si crea fino alla fase 4 (scelta dell'utente 20/10/2026); le
  impostazioni del blocco lo dicono.
- «Esci da Google» (barra e Impostazioni): salva, carica la coda con l'attesa e «Esci senza aspettare» (se resta
  qualcosa chiede conferma: resta su questo PC e si carica quando si rientra), revoca, chiude il blocco su Drive
  aperto, cancella la cache dell'account se non restano modifiche.
- Prova `Ardesia.exe --prova-drive [registro.txt] [--finto]` (`DriveTestWindow` + `Services/Google/DriveSelfTest.cs`):
  prima 22 prove con un Drive finto in memoria (con `--finto` solo queste, senza finestra, codice d'uscita 1 se
  qualcosa non va), poi con l'account vero: crea un blocco in `Ardesia/PROVA/Prova`, coda, rinomina, download,
  riprendibile (1,3 MB in pezzi da 256 KB), elenco, cestino. La cache della prova è in una cartella temporanea.
  Se non si è collegati apre il browser.
- **Provata il 20/10/2026** con `giovanni.dalbo@liceofoscarini.it`: `--prova-drive` tutta OK; nell'app vera nuovo
  blocco su Drive dalla schermata iniziale (classi e materie lette da Drive), tratto → «da caricare» → caricato dopo
  ~30 s, rinomina, «Apri da Drive…», riapertura all'avvio con la cache cancellata (riscaricato), «Elimina il blocco
  appunti…», «Esci da Google» con una modifica appena fatta (caricata prima, poi cache tolta). Sul Drive dell'utente
  restano le cartelle `Ardesia/PROVA/Prova` e `Ardesia/PROVA/Prova-2` con un blocco «prova Prova-2 26-27»: da
  cancellare dal sito di Drive.
- Non fatto in fase 2 (fase 3): conflitti «tieni entrambi» (ora vince la copia di questo PC; Drive tiene le
  revisioni), lucchetto morbido, finestrella di chiusura con la coda (ora alla chiusura il caricamento parte ma non si
  aspetta: resta in `sync.json` e riparte al prossimo avvio **su questo PC**), avviso nella barra quando la rete torna.
  Creare un blocco con una cartella nuova richiede 5–6 chiamate (~10 s sulla rete di casa): accettabile, da rivedere
  se a scuola è lento.

**Fase 3 (robustezza) scritta il 20/10/2026** (ramo `claude/google-drive-fase-3-9df131`, parte dalla fase 2):
- `DriveApi`: `DriveFile.AppProperties` (in `Fields`), `UpdateMetadataAsync` (PATCH `files/{id}` dei soli metadati, un
  valore null toglie la chiave; niente revisione nuova), `UpdateContentAsync` con appProperties che possono togliere
  chiavi e con l'avanzamento del caricamento riprendibile.
- **Lucchetto morbido** (§4.4) in `DriveSync`: `SetOpen(id)` (chiamato da `MainWindow.SetNotebook` tramite
  `DriveNotebookChanged`) scrive subito `apertoSu=<nome PC>` e `apertoAlle=<ora UTC ISO>`; si rinnova con ogni
  caricamento o ogni 5 minuti (solo metadati); si toglie al cambio di blocco, alla chiusura e prima di «Esci da
  Google», **solo se è ancora di questo PC** (un altro PC può averlo preso nel frattempo). Con modifiche in coda lo
  toglie il caricamento stesso. All'apertura, se il blocco risulta aperto su un altro PC da meno di 15 minuti:
  «Questo blocco appunti risulta aperto anche sul PC X (ultima attività alle hh:mm)…» Sì = aprilo lo stesso,
  No = annulla (predefinito). Il lucchetto di questo stesso PC si ignora. «Apri da Drive…» e la schermata iniziale
  mostrano «aperto su X».
- **Conflitti «tieni entrambi»** (§4.4, senza domande): prima di ogni caricamento `GetAsync` e confronto di
  `headRevisionId` con `BaseRevision`. Se diversi, la nostra copia va in un file nuovo «{titolo} (modifiche da {PC},
  dd-MM HH.mm).ardesia» nella stessa cartella (`ForkAsync`), la cache passa al nuovo ID, il lucchetto pure; la cache
  del file originale si cancella (si riscarica all'apertura). Evento `DriveSync.Forked` → `MainWindow` cambia
  `_path`, `_driveId` e titolo, mostra il messaggio una volta; nella barra «Conflitto: salvata una copia» (clic →
  spiegazione) finché il blocco resta aperto. Lo stesso all'apertura, se su Drive è cambiato e la cache ha modifiche
  non caricate: si apre la copia (resta il vecchio avviso solo se la revisione di partenza è sconosciuta).
- **Chiusura con coda** (§4.5): `FinishDriveOnExit` dopo `ConfirmClose` toglie il lucchetto e, se c'è lavoro
  (`HasWork`), `FlushAsync` con «Carico su Google Drive… (2,1 MB)», avanzamento ed «Esci senza aspettare» (compare dopo
  350 ms, quindi quasi mai per il solo lucchetto). Se restano modifiche: «Le ultime modifiche sono salvate su questo PC
  ma non ancora su Google Drive…». Se va tutto bene, nessuna domanda. I messaggi dei conflitti nati durante la chiusura
  si mostrano alla fine.
- **Senza rete**: `NetworkChange.NetworkAvailabilityChanged` (riportato sul thread della UI) → `NetworkRestored`
  azzera attese e tentativi, il caricamento riparte subito. `FlushAsync` senza rete non aspetta il secondo tentativo.
  Creare ed eliminare restano impossibili senza rete, con un messaggio che lo dice (per creare: «oppure crealo sul
  computer»).
- Prove: `--prova-drive --finto` ora 37 prove (conflitto all'apertura e al caricamento, lucchetto scritto/rinnovato/
  tolto, lucchetto di un altro PC recente o vecchio, del proprio PC, chiusura con coda, chiusura senza rete e ripresa);
  la parte vera prova lucchetto scritto e tolto e un conflitto con un «ALTRO-PC-PROVA» simulato caricando con
  `DriveApi`, poi cestina blocco e copia.
- Per le prove dal vivo con un «secondo PC» sullo stesso computer: variabili d'ambiente `ARDESIA_PROVA_PC` (nome del PC
  nel lucchetto e nelle copie) e `ARDESIA_PROVA_CACHE` (cartella della cache di Drive al posto di
  `%LocalAppData%\Ardesia\Drive`). `settings.json` resta in comune: prima di avviare la seconda Ardesia togliere
  `LastFile`.
- **Provata il 20/10/2026** con `giovanni.dalbo@liceofoscarini.it`: `--prova-drive` tutta OK (37 prove finte + parte
  vera con lucchetto e conflitto). Nell'app vera, con due Ardesia («PC-STUDIO» e «LIM-AULA12» finta): nuovo blocco in
  PROVA/Prova; sulla seconda la schermata iniziale mostra «aperto su PC-STUDIO» e all'apertura compare la domanda (No
  predefinito); tratto caricato dalla seconda, poi tratto sulla prima → copia «PROVA Fase3 dal vivo (modifiche da
  PC-STUDIO, 20-10 20.14)», titolo cambiato, messaggio, «Conflitto: salvata una copia» con spiegazione al clic (il
  clic non arriva se la finestra ingrandita mette la barra sotto la finestra trasparente di Claude); chiusura con una
  modifica appena fatta → «Carico su Google Drive… (1 kB)», poi uscita senza domande, niente in coda, lucchetto tolto
  (la copia non risultava più «aperta» nell'elenco). Non provata dal vivo la chiusura senza rete (c'è nelle prove
  finte). Blocchi di prova cestinati; restano le cartelle `Ardesia/PROVA/…` e il blocco «prova Prova-2 26-27».
- Visto nelle prove (difetto della fase 2): eliminando il blocco aperto da «Apri da Drive…», la schermata iniziale
  compare sopra l'elenco ancora aperto.

### Scelte dell'utente (17/10/2026)

| # | Domanda | Scelta |
|---|---|---|
| 1 | Chi crea il progetto Google Cloud | **L'utente**; dominio **`liceofoscarini.it`** |
| 2 | Solo questa scuola? | **Per ora sì** (in futuro forse altre: si tiene l'override `google-client.json`, che costa poco) |
| 3 | Accesso sulle LIM | **Browser** (il codice sul telefono esce dal piano) |
| 4 | Utente Windows sulle LIM | **Personale** per ogni docente → DPAPI separa già i docenti; **niente** uscita per inattività |
| 5 | Conflitti | **«Tieni entrambi»**, senza domande |
| 6 | Quando caricare | ~30 s dopo l'ultima modifica, al massimo ogni ~3 min, alla chiusura: **sì** |
| 7 | Nuovi blocchi su Drive di default, cartella «Ardesia» | **Sì** |
| 8 | PDF per la classe su Drive con link fisso | **Sì**, e la condivisione la fa **Ardesia** |
| 9 | Spostare i blocchi già su `G:\` | **No**: restano dove sono, invariati |
| 10 | Aprire `.ardesia` di colleghi / caricati a mano | **No** (niente selettore di Google) |
| 11 | Dove va un blocco nuovo su Drive (19/10/2026) | `Ardesia/{classe}/{materia}/`: Ardesia chiede **classe** (scritta a mano, niente elenco fisso) e **materia**, crea lei le cartelle e propone il nome «2BO Fisica 26-27», modificabile (§2.1) |
| 12 | Materie (e classi) già usate | Ardesia le **ricorda per l'utente**, non per il PC (§2.1) |
| 13 | Eliminare un blocco da Ardesia | Chiede se eliminare anche il **PDF per la classe**. Le **cartelle** Ardesia non le elimina mai (per sicurezza): il docente le cancella dal sito di Drive, dove può sempre cancellare tutto (§2.2) |
| 14 | PDF per la classe di un blocco su Drive | **Accanto al blocco**, nella stessa cartella; si rinomina col blocco (§6). Serve agli alunni **a casa**, dai loro dispositivi (Windows, Mac, Linux, Android): in aula si usa Ardesia. Quindi un blocco su Drive senza PDF su Drive non serve agli alunni → la fase 4 esce **insieme** alle fasi 2–3 (§9) |
| 15 | Prove a mano | Con la cartella **«Ardesia» vera** nel Drive dell'utente (blocchi di prova, es. classe «PROVA», cancellati poi da lui dal sito di Drive) |

Conseguenze principali: la modalità «PC condiviso» (§5) non serve più come requisito, diventa al massimo
un'opzione futura; niente «Sposta su Drive» né selettore di Google; il PDF si condivide da sé col dominio (§6).

Oggi Ardesia lavora solo con file locali: `NotebookStore.Save` scrive un `.tmp` e lo sostituisce
(`File.Replace`), il salvataggio automatico parte ~1,5 s dopo l'ultima modifica (`MainWindow.AutoSave`),
`ConfirmClose` salva e rifà il PDF per la classe alla chiusura. Google Drive funziona solo se sul PC c'è
**Drive per desktop** (unità `G:`, trovata da `DriveLocator`). Sui PC delle LIM, condivisi e spesso senza
Drive per desktop configurato per ogni docente, questo non basta.

L'idea: Ardesia accede a Google **da sé** (login nel browser, permesso limitato) e usa le **API di Drive**
via HTTPS. Il file su cui si lavora resta una **copia locale in cache**, così il salvataggio automatico
resta istantaneo e la LIM non si blocca mai; il caricamento su Drive avviene **in background**.

---

## 1. Accesso a Google (OAuth 2.0 per app desktop)

Niente librerie esterne (regola del progetto): solo `HttpClient`, `System.Text.Json`, `TcpListener`.

**Flusso «browser di sistema + loopback» con PKCE** (quello raccomandato da Google per le app installate):

1. Ardesia genera `code_verifier` (32 byte casuali, Base64URL) e `code_challenge = S256(verifier)`, più uno
   `state` casuale contro le falsificazioni.
2. Apre un piccolo server solo su `127.0.0.1`, porta libera scelta dal sistema. Uso `TcpListener` e leggo a mano
   l'unica richiesta `GET /?code=…&state=…` invece di `HttpListener`: `HttpListener` passa da http.sys e senza
   amministratore a volte chiede la prenotazione dell'indirizzo (l'installer è per utente, senza amministratore).
3. Apre il browser su `https://accounts.google.com/o/oauth2/v2/auth` con `client_id`,
   `redirect_uri=http://127.0.0.1:{porta}`, `response_type=code`, `scope=…drive.file openid email profile`,
   `code_challenge`, `access_type=offline`, `prompt=select_account` (sui PC condivisi si sceglie sempre l'account),
   `hd=liceofoscarini.it` (suggerisce gli account della scuola; si ricontrolla comunque il dominio dell'e-mail
   ricevuta).
4. Il browser torna su `127.0.0.1`; Ardesia risponde con una paginetta «Accesso riuscito, puoi tornare ad
   Ardesia», controlla `state`, scambia il codice su `https://oauth2.googleapis.com/token` (con `code_verifier`)
   e riceve *access token* (1 ora) e *refresh token*.
5. Nome ed e-mail del docente dall'`id_token` (o da `https://openidconnect.googleapis.com/v1/userinfo`),
   da mostrare nella barra e nelle impostazioni.

Note:
- **Non si può usare WebView2 per il login**: Google blocca l'accesso dai browser incorporati
  (`disallowed_useragent`). Serve il browser vero.
- Si usa il **browser predefinito** (scelta dell'utente): ogni docente ha il suo utente Windows, quindi il
  browser ricorda solo il proprio account. (Il codice sul telefono, `google.com/device`, è stato scartato.)

**Conservazione del refresh token**: cifrato con **DPAPI** (`ProtectedData.Protect`, ambito `CurrentUser`, più
un'«entropia» fissa di Ardesia) in `%LocalAppData%\Ardesia\google-account.bin` (Local, non Roaming: non deve
viaggiare con il profilo). Solo lo stesso utente Windows sullo stesso PC può decifrarlo. L'access token resta
solo in memoria e si rinnova da sé quando scade (o alla prima risposta 401).

**«Esci»** (impostazioni e menu dell'account nella barra): carica prima le modifiche in coda (§4), poi revoca il
token (`POST https://oauth2.googleapis.com/revoke`), cancella il file cifrato e la cache di Drive dell'account.

`ProtectedData` in .NET 10 è nel pacchetto `System.Security.Cryptography.ProtectedData` (di Microsoft, non una
libreria di terzi). Se si vuole evitare anche quello, si chiama direttamente `CryptProtectData` di `crypt32.dll`
con P/Invoke (una trentina di righe). **Proposta: P/Invoke**, coerente con «niente pacchetti».

## 2. Permesso `drive.file`

Ardesia chiede solo `https://www.googleapis.com/auth/drive.file`: il permesso «non sensibile» consigliato da
Google, che **non richiede revisione di sicurezza** né verifica dell'app.

Conseguenze (vanno capite bene):
- Ardesia vede e modifica **solo i file che ha creato lei** (o che il docente le ha esplicitamente «dato» con il
  selettore di Google, vedi sotto). Non vede il resto del Drive del docente: è anche una garanzia di riservatezza.
- Il permesso vale per **l'app (l'ID client), non per il PC**: un blocco creato da Ardesia sulla LIM dell'aula 12
  si ritrova da Ardesia sul PC di casa o sulla LIM dell'aula 5, con lo stesso account.
- **Non** si vedono i `.ardesia` caricati su Drive in altro modo (Drive per desktop, trascinati nel browser,
  condivisi da un collega). Per scelta dell'utente non serve: i blocchi già su `G:\` restano lì e si aprono come
  oggi dal computer (Drive per desktop, `DriveLocator`); niente selettore di Google.

**Cartella «Ardesia»** in «Il mio Drive»: creata da Ardesia al primo uso (`files.create` con
`mimeType=application/vnd.google-apps.folder`); il suo ID si ricorda nelle impostazioni dell'account e, se
sparisce (cestinata), si ricrea. I blocchi nuovi finiscono in sottocartelle per classe e materia (§2.1). Il docente
può spostarli dal sito di Drive: Ardesia continua a vederli, perché li riconosce dall'ID, non dalla posizione.

### 2.1 Nuovo blocco: classe, materia, nome (scelta dell'utente, 19/10/2026)

Struttura su Drive: `Il mio Drive/Ardesia/{classe}/{materia}/{nome}.ardesia`, per esempio
`Ardesia/2BO/Fisica/2BO Fisica 26-27.ardesia`. Le cartelle le crea **Ardesia**, al momento della creazione del file.

**Primo avvio su un PC nuovo**: Ardesia installata → «Accedi con Google» → nessun blocco su Drive → finestra
«Nuovo blocco appunti»:
1. **Classe** (es. «2BO», «1BE-CE»): scritta a mano dal docente (un elenco fisso sarebbe troppo lungo); sotto, le
   classi già usate come pulsanti da toccare.
2. **Materia** (es. «Fisica»): scritta a mano, con le materie già usate proposte allo stesso modo.
3. **Nome**: proposto «{classe} {materia} {anno}» → «2BO Fisica 26-27», modificabile. L'anno scolastico si calcola
   dalla data e cambia a **settembre** (settembre 2026 – agosto 2027 = «26-27»).
4. Ardesia crea `Ardesia/2BO/Fisica/` se manca (riusa le cartelle esistenti, senza distinguere maiuscole) e il blocco.

**Su un PC già usato e collegato**: File › Nuovo → classe → materia → nome proposto, come sopra.

**Ricordare classi e materie per l'utente, non per il PC**: non serve salvare nulla di più. Con `drive.file`
Ardesia vede le cartelle che ha creato lei, quindi classi e materie già usate si **leggono da Drive**
(`files.list` con `mimeType='application/vnd.google-apps.folder'`: figlie di «Ardesia» = classi, figlie di una
classe = materie), su qualunque PC e con qualunque utente Windows. Ordine: prima le usate più di recente.
Senza rete si usa l'ultimo elenco letto, tenuto in cache nella cartella dell'account (§4.1).

**Caratteri non ammessi** nei nomi di file e cartelle (`\ / : * ? " < > |`, scelta dell'utente 19/10/2026):
diventano `-` (es. «1BE/CE» → «1BE-CE»). Da decidere più avanti: se proporre nella classe solo le cartelle
dell'anno in corso.

### 2.2 Eliminare un blocco (scelta dell'utente, 19/10/2026)

Il docente può sempre cancellare file e cartelle **dal sito di Drive**: Ardesia se ne accorge (404 o file nel
cestino), toglie il blocco dai recenti e dall'elenco e, se c'erano modifiche non caricate, avvisa e offre di
salvarne una copia sul computer.

**Da Ardesia** («Elimina il blocco appunti…» nell'elenco dei blocchi su Drive e nel menu File):
1. «Eliminare il blocco appunti «2BO Fisica 26-27»? Va nel cestino di Google Drive: lo puoi recuperare da lì per
   30 giorni.»  [Elimina] [Annulla]
2. Se il blocco ha un **PDF per la classe** su Drive: «Eliminare anche il PDF per la classe? Gli alunni che hanno il
   link non potranno più aprirlo.»  [Elimina anche il PDF] [Tieni il PDF]
- **Le cartelle non si eliminano mai da Ardesia** (scelta dell'utente: per andare sul sicuro). Motivo: con
  `drive.file` Ardesia vede solo i file creati da lei, non quelli che il docente ha messo nella cartella dal sito di
  Drive, e cestinando una cartella «vuota» finirebbero nel cestino anche loro. Le cartelle rimaste vuote le cancella
  il docente dal sito di Drive, se vuole; Ardesia le riusa se si crea di nuovo un blocco per quella classe/materia.
- Il blocco e il PDF vanno nel **cestino** di Drive (`files.update` con `trashed=true`), mai cancellati per sempre:
  un errore si rimedia dal cestino. Ardesia può cestinare solo ciò che ha creato lei, cioè proprio questi file.
- Il pulsante predefinito di ogni domanda è quello **che non cancella** (Annulla / Tieni), contro i tocchi per
  sbaglio sulla LIM. Il blocco aperto si chiude prima dell'eliminazione; si cancella anche la sua copia in cache.
- Senza rete non si elimina (messaggio chiaro): niente eliminazioni «in coda».

### 2.3 Elencare i propri blocchi

Con `files.list`:
```
q = mimeType='application/vnd.ardesia.notebook' and trashed=false
fields = files(id,name,modifiedTime,version,headRevisionId,size,parents,appProperties,lastModifyingUser)
orderBy = modifiedTime desc
```
Ogni file caricato porta `mimeType=application/vnd.ardesia.notebook` (lo stesso che c'è già dentro lo ZIP) e
`appProperties` con dati di Ardesia (versione del formato, PC che lo sta usando: §4.4).

## 3. Progetto Google Cloud (una volta sola)

App **«interna»** del dominio della scuola: la usano solo gli account `@<dominio scuola>`, e per questo Google
**non chiede nessuna verifica**. Il refresh token di un'app interna non scade dopo 7 giorni (quello vale per le
app esterne «in prova»).

### Passi per l'utente (con l'account della scuola, non quello personale)

1. `console.cloud.google.com` → crea il progetto «Ardesia» **dentro l'organizzazione della scuola**.
   Se non si può (molte scuole bloccano la creazione di progetti), lo crea l'amministratore e dà all'utente il
   ruolo di *Proprietario* del progetto.
2. «API e servizi» → «Libreria» → attiva **Google Drive API**.
3. «Schermata di consenso OAuth» (Google Auth Platform → Branding / Pubblico / Accesso ai dati):
   tipo utente **Interno**; nome «Ardesia», logo (opzionale), e-mail di assistenza; ambiti
   `drive.file`, `openid`, `email`, `profile`.
4. «Client» → «Crea client» → tipo **App desktop**, nome «Ardesia per Windows».
   Si ottengono *ID client* e *client secret*. Per le app desktop Google considera il secret **non segreto**
   (sta comunque dentro l'eseguibile): va bene metterlo nel codice. Il repository è privato.
5. Mandare a me ID client e secret: li metto in `Services/GoogleConfig.cs` (con la possibilità di sostituirli con
   un file `google-client.json` accanto all'eseguibile, utile se un'altra scuola volesse usare Ardesia).

### Cosa deve fare l'amministratore di Google Workspace

- Console di amministrazione → **Sicurezza → Accesso e controllo dei dati → Controlli API → Controllo dell'accesso
  alle app**: se l'accesso delle app di terze parti è limitato, aggiungere l'app con l'**ID client** di Ardesia e
  impostarla come **«Attendibile»** (oppure verificare che la casella «Considera attendibili le app interne di
  proprietà del dominio» sia attiva).
- Verificare che **Drive** sia attivo per i docenti (lo è, se usano Drive) e che la condivisione «chiunque in
  liceofoscarini.it con il link» sia permessa: serve per il PDF per la classe (§6).
- Eventuale «durata della sessione» per le app Google Cloud: se impostata (es. 16 ore), il docente dovrà
  rifare l'accesso periodicamente. Da chiedere.

## 4. Sincronizzazione

### 4.1 La copia locale è il file di lavoro

Un blocco su Drive ha in locale:
```
%LocalAppData%\Ardesia\Drive\{account}\{fileId}.ardesia     ← il file che Ardesia apre e salva (come oggi)
%LocalAppData%\Ardesia\Drive\{account}\sync.json            ← per ogni fileId: nome, revisione di base,
                                                              «da caricare», ultimo caricamento, errori
```
`{account}` = hash dell'e-mail, così due docenti sullo stesso utente Windows non si mescolano.

**Impatto sul codice esistente minimo**: `MainWindow._path` resta il percorso del file in cache, quindi
`Save`, `AutoSave`, `NotebookStore`, il salvataggio di emergenza funzionano come adesso. In più c'è un
riferimento `_drive` (fileId + account) e, dopo ogni `Save` riuscito, una riga `DriveSync.Changed(fileId)`.

### 4.2 Caricamento in background, con coda

- Il salvataggio automatico locale resta a ~1,5 s. Il **caricamento su Drive no**: parte dopo **~30 s senza
  modifiche**, e comunque al massimo ogni **~3 minuti** mentre si scrive (approvato), e sempre
  alla chiusura / al cambio blocco / a «Esci».
- Un solo caricamento alla volta per file, su un thread a parte; la UI non aspetta mai. Si carica una
  **istantanea** (copia del file in cache fatta in un attimo sul thread della UI), perché il salvataggio
  successivo deve poter sostituire il file mentre l'upload è in corso (`File.Replace` fallisce su un file aperto).
- API: `PATCH https://www.googleapis.com/upload/drive/v3/files/{id}?uploadType=multipart` per file piccoli,
  `uploadType=resumable` sopra ~5 MB (blocchi con immagini, connessione lenta: riprende da dove si era fermato).
- Errori di rete / 5xx / 429: nuovo tentativo con attesa crescente (5 s, 15 s, 1 min, 5 min…). 401: rinnovo del
  token e riprova. 403/404 (file cancellato o non più accessibile): avviso nella barra, il lavoro resta in cache.
- Lo stato «da caricare» è scritto in `sync.json`, quindi **sopravvive a un arresto improvviso**: al riavvio
  Ardesia riprende i caricamenti in sospeso.
- Drive conserva da sé le **revisioni** precedenti (circa 30 giorni / 100 versioni): una cronologia gratuita,
  in futuro anche «Torna a una versione precedente».

### 4.3 Apertura e download

All'apertura di un blocco su Drive (anche all'avvio, che riapre l'ultimo):
1. si leggono i metadati del file (`files.get`, una frazione di secondo);
2. se `headRevisionId` è uguale a quello della cache → si apre subito la cache;
3. se è cambiato (modificato su un altro PC) e la cache non ha modifiche da caricare → si scarica
   (`alt=media`, con barra di avanzamento) e poi si apre;
4. se è cambiato **e** la cache ha modifiche non caricate → conflitto (§4.4);
5. **senza rete**: si apre la cache, con un avviso nella barra «Senza connessione: potrebbe non essere l'ultima
   versione». Se il blocco non è mai stato scaricato su questo PC, non si può aprire (messaggio chiaro).

### 4.4 Conflitti (stesso blocco su due PC)

Il caso tipico: il docente lascia Ardesia aperta sulla LIM dell'aula 12 e apre lo stesso blocco in aula 5.

**Prevenzione – «lucchetto morbido»**: mentre un blocco è aperto, Ardesia scrive nelle `appProperties` del file
`apertoSu=<nome PC>` e `apertoAlle=<ora>`, aggiornati a ogni caricamento (o ogni ~5 minuti), e li toglie alla
chiusura. Aprendo il blocco altrove, se risulta aperto su un altro PC da meno di ~15 minuti:
> «Questo blocco appunti risulta aperto anche sul PC LIM-AULA12 (ultima attività alle 10:42).
> Se lo modifichi qui e là, le modifiche non si uniscono.»  [Aprilo lo stesso]  [Annulla]

**Rilevamento**: prima di ogni caricamento si confronta `headRevisionId` su Drive con la revisione di base della
cache. (Drive v3 non offre un «aggiorna solo se non cambiato» affidabile; la finestra tra controllo e scrittura
è di un secondo, accettabile per un utente solo.)

**Risoluzione – proposta: non perdere mai niente, tenere entrambi**:
- la versione dell'altro PC resta su Drive com'è;
- la nostra si carica come **nuovo file** «Matematica 3B (modifiche da LIM-AULA5, 17-10 11.02).ardesia» nella
  stessa cartella, e Ardesia continua a lavorare su questa;
- avviso nella barra e messaggio una volta sola.

In una fase successiva si può fare di meglio: le pagine hanno già un ID, quindi è possibile un'**unione per
pagina** (pagine nuove da entrambe le parti si tengono, una pagina modificata da tutte e due si duplica).

### 4.5 Chiusura

`ConfirmClose` oggi salva e rifà il PDF. In più:
- se ci sono caricamenti in coda, una finestrella «Carico su Google Drive… (2,1 MB)» con avanzamento e pulsante
  «Esci senza aspettare»;
- se il caricamento non riesce (niente rete): «Le ultime modifiche sono salvate su questo PC ma non ancora su
  Google Drive. Verranno caricate la prossima volta che apri Ardesia **su questo PC**.»
- L'uscita resta **senza domande** quando tutto va bene (comportamento richiesto).

## 5. PC delle LIM condivisi tra docenti

Ogni docente entra in Windows **con il proprio utente** (scelta dell'utente): il refresh token cifrato con DPAPI e
la cache in `%LocalAppData%` sono già separati per docente, e il browser ricorda solo il proprio account Google.
Quindi:
- l'accesso **si ricorda** (come sul PC personale) e all'avvio si riapre l'ultimo blocco, come oggi;
- **nessuna** uscita automatica per inattività;
- nella barra resta visibile **chi è collegato** («Giovanni Dal Bo ▾»), con «Esci» a un clic.

Una modalità «PC condiviso» (InPrivate, niente token su disco, cache cancellata alla chiusura) resta solo un'idea
per il futuro, se un giorno Ardesia girasse su PC con un utente Windows comune.

## 6. PDF per la classe su Drive

Oggi il PDF va accanto al file; se il file è su Drive per desktop, Drive lo carica. Per i blocchi su Drive
(scelta dell'utente: link fisso, condivisione fatta da Ardesia):
- il PDF si crea in locale come ora (`PdfExporter`, alla chiusura, solo se ci sono modifiche) e si **carica su
  Drive** nella stessa coda, **aggiornando sempre lo stesso file** (stesso ID): così il **link per gli alunni
  non cambia mai** e la condivisione resta.
- Posizione (scelta dell'utente, 19/10/2026): **accanto al blocco**, nella stessa cartella
  (`Ardesia/2BO/Fisica/2BO Fisica 26-27.pdf`), come oggi per i file sul computer. Nome del PDF = nome del blocco.
- **Rinomina** (scelta dell'utente): se il docente cambia il nome del blocco nella barra in alto, Ardesia rinomina
  su Drive sia il blocco sia il PDF (`files.update` con `name`); ID, cartella e link per gli alunni restano.
- **Condivisione automatica** al primo caricamento: `permissions.create` con `type=domain`,
  `domain=liceofoscarini.it`, `role=reader`, `allowFileDiscovery=false` → lo apre chiunque della scuola **abbia il
  link** (il PDF non compare nelle ricerche degli altri). Gli alunni devono avere account `@liceofoscarini.it`
  (da confermare in prova).
- Il PDF per la classe resta **attivo di default** per ogni blocco (scelta già fatta); si spegne per singolo blocco
  in Impostazioni › Questo blocco appunti, come oggi. Spegnendolo, il PDF su Drive resta (non si cancella nulla).
- In **Impostazioni › Questo blocco appunti › PDF per la classe**, per un blocco su Drive: «**Copia il link per
  gli alunni**» (da incollare su Classroom), «Apri il PDF su Drive», stato dell'ultimo aggiornamento.
- Nel blocco si salva l'ID del PDF su Drive (`ClassPdfDriveId`), come oggi `ClassPdfPath`.
- Per i blocchi locali non cambia nulla.

## 7. Interfaccia

**Schermata iniziale (`StartWindow`)**: accanto a «Nuovo» e «Apri» una sezione **Google Drive**:
- non collegati: pulsante «Accedi con Google»;
- collegati: elenco dei propri blocchi su Drive (nome, data di modifica, «aperto su LIM-AULA12» se è il caso);
- «Nuovo» da collegati crea il blocco **su Drive**, in `Ardesia/{classe}/{materia}` (scelta dell'utente): si
  chiedono classe, materia e nome proposto (§2.1). Per un blocco sul computer resta «Nuovo blocco sul computer…» (finestra di salvataggio di oggi);
- i recenti mostrano un'icona a nuvola per quelli su Drive.

**Menu File**: «Nuovo» (su Drive se collegati), «Nuovo sul computer…», «Apri da Drive…» (stessa lista in una
finestra), «Apri dal computer…», «Salva una copia sul computer…». Niente «Sposta su Drive»: i blocchi esistenti
restano dove sono (scelta dell'utente).

**Barra di stato** (dove oggi c'è «Salvato alle 10:32»): icona a nuvola + testo, con tooltip:
- «Su Drive · salvato alle 10:32»
- «Caricamento su Drive…»
- «Senza connessione · modifiche salvate su questo PC, da caricare»
- «Conflitto: salvata una copia» (clic → spiegazione)
- per i file locali resta come adesso.

**Titolo della finestra**: oggi mostra il percorso; per i file su Drive «Matematica 3B – Google Drive
(giovanni.dalbo@…) – Ardesia», non il percorso della cache.

**Impostazioni › Questo PC**: riquadro «Google Drive»: account collegato, «Accedi / Esci», «Svuota la cache di
Drive su questo PC».

**Impostazioni › Questo blocco appunti**: dove si trova (Drive / questo PC), «Apri su drive.google.com»,
PDF per la classe come in §6.

**Messaggi di avvio**: `App.xaml.cs` oggi dice «controlla che Google Drive per desktop sia avviato»; per i
blocchi su Drive diventerà «Accedi a Google per riaprire …». `DriveLocator` resta per i blocchi su `G:\`.

## 8. Struttura del codice (indicativa)

| File nuovo | Contenuto |
|---|---|
| `Services/Google/GoogleConfig.cs` | ID client, secret, dominio `liceofoscarini.it`, eventuale `google-client.json` |
| `Services/Google/GoogleAuth.cs` | PKCE, server loopback (`TcpListener`), scambio e rinnovo dei token, revoca |
| `Services/Google/TokenStore.cs` | DPAPI via P/Invoke, `%LocalAppData%\Ardesia\google-account.bin` |
| `Services/Google/DriveClient.cs` | `HttpClient`: list, get, create, update multipart/resumable, download, permissions, cartelle |
| `Services/Google/DriveSync.cs` | cache, `sync.json`, coda di caricamento con ritardo e tentativi, conflitti, lucchetto morbido |
| `DriveBrowserWindow.xaml(.cs)` | elenco dei blocchi su Drive (anche dentro `StartWindow`) |

`DriveSync` lavora su un'interfaccia `IDriveClient`, così le prove automatiche usano un **Drive finto in memoria**:
`Ardesia.exe --prova-drive [registro.txt]` simula caricamenti, rete assente, conflitti, PDF per la classe
(codice d'uscita 1 se qualcosa non va), come `--prova-grafico`. La prova con un account vero si fa a mano, nella cartella «Ardesia» vera (scelta 15).
(Fatto nella fase 2: il Drive finto è in `DriveSelfTest.cs`; `--prova-drive` fa anche la parte vera, `--finto` solo
quella finta. `DriveBrowserWindow` e `NewDriveNotebookDialog` sono scritte in codice, senza XAML; il codice di
`MainWindow` per Drive è in `MainWindow.Drive.cs`.)

## 9. Fasi

0. **Preparazione (utente)**: progetto Google Cloud, client desktop, app attendibile se serve (§3); mandarmi ID
   client e secret.
1. **Accesso** (*scritta il 19/10/2026*): login col browser, token cifrato, «Esci», account nelle impostazioni;
   prova `--prova-accesso` (apre il browser, stampa e-mail e scadenza).
2. **Drive di base** (*scritta e provata il 20/10/2026*, vedi in cima): cartella «Ardesia» con sottocartelle classe/materia, nuovo blocco su Drive con classe,
   materia e nome proposto (§2.1, default da collegati), elenco e apertura, cache,
   coda di caricamento, download all'apertura, recenti, stato nella barra, `--prova-drive`.
3. **Robustezza** (*scritta il 20/10/2026*, vedi in cima): senza rete, conflitti («tieni entrambi»), lucchetto morbido, chiusura con coda.
   *Prima di usarlo con le classi servono le fasi 1–4 insieme* (il PDF su Drive è ciò che leggono gli alunni a casa).
4. **PDF per la classe su Drive**, accanto al blocco, condivisione automatica col dominio, «Copia il link»,
   rinomina insieme al blocco.
5. **In seguito, se servirà**: unione per pagina nei conflitti, «versioni precedenti» dalle revisioni di Drive,
   modalità «PC condiviso», configurazione per altre scuole.

## 10. Rischi

- **Permessi nella console della scuola**: se l'utente non può creare progetti nell'organizzazione o rendere
  l'app attendibile, serve comunque l'amministratore. Ripiego: app «esterna» in prova (token che scade ogni
  7 giorni: scomodo). Va chiarito per primo (fase 0).
- **Password sulla LIM**: il primo accesso su ogni LIM si fa col browser e la tastiera a schermo; poi Ardesia lo
  ricorda (utente Windows personale), quindi capita una volta per LIM.
- **Modifiche non caricate** su un PC dove il docente non torna (rete caduta all'uscita): restano in cache su
  quel PC, solo per quel docente. Mitigazione: avviso all'uscita, ripresa automatica al prossimo avvio di Ardesia
  su quel PC.
- **File grandi e rete lenta della scuola**: blocchi con molte immagini possono pesare decine di MB;
  upload riprendibile e caricamento solo dopo una pausa nella scrittura.
- **`drive.file` e file «estranei»**: il docente potrebbe non capire perché non vede un `.ardesia` caricato a
  mano su Drive. Serve una frase nell'elenco («Qui ci sono i blocchi creati da Ardesia su Drive; quelli su `G:\`
  si aprono da Apri dal computer»).
- **Condivisione col dominio** disattivata dall'amministratore: il PDF si carica ma la condivisione non riesce →
  avviso chiaro, e il docente condivide a mano.
- **Cambiamenti di Google** (pannello Cloud, regole OAuth): i passi del §3 vanno ricontrollati al momento.
- **Mantenere due mondi** (file locali e Drive): si tengono entrambi, ma ogni funzione nuova va provata con tutti
  e due.

## 11. Domande

Tutte risolte il 17/10/2026: vedi la tabella in cima. Da verificare durante le prove: che gli alunni abbiano
account `@liceofoscarini.it` (per la condivisione col dominio) e le voci esatte della console di Google.
