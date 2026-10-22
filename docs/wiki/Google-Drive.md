# Google Drive

Dalla versione 0.7.0 Ardesia salva i blocchi appunti **direttamente su Google Drive**, con l'account della scuola: non serve installare *Google Drive per desktop*. Il blocco si ritrova su **qualunque PC o LIM** dove entri con lo stesso account, e il [PDF per la classe](PDF.md#il-pdf-per-la-classe) va su Drive con un **link per gli alunni che non cambia mai**.

I blocchi salvati sul computer (o su `G:` con Drive per desktop) **restano dove sono** e si aprono come prima: Drive è una possibilità in più, non un obbligo.

## Accedere con Google

1. Premi **Accedi a Google** in alto a destra (oppure **Accedi con Google** nella schermata iniziale, *File → Accedi a Google Drive…*, o *File → Impostazioni… → Questo PC*, riquadro **Google Drive**).
2. Si apre il **browser** sulla pagina di Google: scegli il tuo account **@liceofoscarini.it** e consenti l'accesso. Sono accettati solo gli account della scuola.
3. Torna in Ardesia: in alto a destra, al posto di «Accedi a Google», compare il tuo **nome**. Un clic sul nome apre **Nuovo blocco appunti su Drive…**, **Apri da Drive…**, **Esci da Google** e **Impostazioni…**.

Che cosa vede Ardesia: **solo i file che crea lei** sul tuo Drive. Il resto del Drive non lo vede e non lo tocca.

L'accesso si fa **una volta per PC**: Ardesia lo ricorda, cifrato e leggibile solo dal tuo utente di Windows, anche dopo aver chiuso Ardesia o riavviato il PC. Sui PC delle LIM ognuno entra in Windows con il proprio utente, quindi ognuno ha il suo accesso.

**Uscire:** clic sul tuo nome in alto → **Esci da Google** (o il pulsante *Esci* nelle Impostazioni). Ardesia carica prima le modifiche in sospeso, chiude il blocco su Drive, toglie il permesso anche dal lato di Google e cancella l'account da quel PC. Fallo se hai usato un PC che non è tuo e su cui sei entrato con un utente di Windows condiviso.

## Creare un blocco su Drive

Da collegati, **Crea un nuovo blocco appunti** (schermata iniziale), *File → Nuovo blocco appunti su Drive…* o il menu del tuo nome in alto a destra apre la finestra **«Nuovo blocco appunti su Google Drive»**:

- **Classe** (per esempio `2BO`), **Materia** (per esempio `Fisica`) e **Nome del blocco appunti**. Ardesia propone, come pulsanti da toccare, le classi e le materie dove hai già dei blocchi (le cartelle rimaste vuote non compaiono);
- sotto si vede dove andrà: «Su Drive: Il mio Drive › Ardesia › 2BO › Fisica › 2BO Fisica 26-27»;
- **Crea**. Le cartelle che mancano le crea Ardesia.

Per creare invece un file sul PC c'è **Crea invece sul computer…** nella stessa finestra (oppure *File → Nuovo blocco appunti sul computer…*).

Per creare un blocco su Drive serve la rete. I caratteri che Drive o Windows non accettano nei nomi (per esempio `/` o `:`) diventano «-».

## Aprire un blocco da Drive

- Nella schermata iniziale, sotto **Su Google Drive**, ci sono i tuoi ultimi blocchi su Drive; **Tutti i blocchi su Drive…** li mostra tutti.
- Da Ardesia aperta: *File → Apri da Drive…*, o il menu del tuo nome in alto a destra.

La finestra **«I tuoi blocchi appunti su Google Drive»** ha una casella per **cercare** per nome, classe o materia; per ogni blocco dice la cartella, quando è stato modificato e se risulta aperto su un altro PC. **Apri**, **Nuovo blocco appunti…**, **Elimina…**.

Ardesia elenca solo i blocchi **creati da lei** sul tuo Drive: un `.ardesia` caricato a mano su Drive o condiviso da un collega non compare (aprilo da `G:` con Drive per desktop, oppure scaricalo e usa *File → Apri…*).

Anche i blocchi su Drive compaiono in *File → Recenti*, con accanto «(Google Drive · classe / materia)».

## Il salvataggio

Ardesia lavora su una **copia sul PC**, quindi il salvataggio resta istantaneo e la LIM non rallenta mai. Le modifiche vanno su Drive **da sole**: dopo una breve pausa nella scrittura, comunque **al massimo ogni 3 minuti**, e alla **chiusura** di Ardesia.

In alto a destra, accanto a una **nuvola**, si vede a che punto è:

| Messaggio | Che cosa vuol dire |
|---|---|
| **Su Drive · salvato alle 10:32** | Tutto è su Drive |
| **Salvato su questo PC · da caricare su Drive** | Le ultime modifiche partono tra poco |
| **Caricamento su Drive…** | Le sta caricando |
| **Senza connessione · modifiche salvate su questo PC, da caricare** | Niente rete: si continua a lavorare, Ardesia riprova da sola |
| **Google Drive non risponde · riprovo tra poco** | Drive ha dato un errore passeggero |
| **Accedi a Google per caricare le modifiche** | Sei uscito da Google: le modifiche restano sul PC fino al prossimo accesso |
| **Conflitto: salvata una copia** | Vedi [sotto](#stesso-blocco-su-due-pc); un clic sul messaggio spiega che cosa è successo |

**Alla chiusura**, se c'è ancora qualcosa da caricare, compare per un attimo «Carico su Google Drive…» con il pulsante **Esci senza aspettare**. Se si esce prima che abbia finito (o senza rete), le modifiche restano sul PC e vengono caricate **la prossima volta che apri Ardesia su quel PC**: meglio quindi aspettare quei pochi secondi, soprattutto sulle LIM.

**Senza rete** si lavora lo stesso, ma si aprono solo i blocchi già aperti almeno una volta su quel PC.

Il **nome del blocco** (in alto al centro) è anche il nome del file su Drive: cambiandolo, Ardesia rinomina il file su Drive (e il suo PDF per la classe).

## Stesso blocco su due PC

Ardesia ricorda su Drive dove è aperto un blocco. Se apri un blocco che risulta aperto da poco **su un altro PC** compare l'avviso «Questo blocco appunti risulta aperto anche sul PC LIM-AULA12…»: puoi aprirlo lo stesso o annullare. Di solito vuol dire che qualcuno (o tu stesso) l'ha lasciato aperto su un'altra LIM.

Se lo stesso blocco viene modificato **in due posti**, Ardesia **non perde niente** e non mescola le modifiche: la versione su Drive resta com'è e quella di questo PC viene salvata come **copia** accanto, con un nome come «Fisica 2BO (modifiche da LIM-AULA5, 21-10 10.32)». Ardesia lo spiega con un messaggio e continua sulla copia. Poi decidi tu quale tenere (l'altra si elimina con *File → Elimina il blocco appunti…*).

Il PDF per la classe resta legato al blocco originale: la copia nasce con il PDF per la classe spento.

## Eliminare un blocco su Drive

*File → Elimina il blocco appunti…* (o **Elimina…** in *Apri da Drive*): il blocco va nel **cestino di Google Drive**, da dove si recupera per **30 giorni**. Le cartelle di classe e materia restano.

Ardesia chiede anche se **eliminare il PDF per la classe**: di solito conviene tenerlo, perché gli alunni con il link non potrebbero più aprirlo.

## Il PDF per la classe su Drive

Per un blocco su Drive il [PDF per la classe](PDF.md#il-pdf-per-la-classe) va **accanto al blocco**, nella stessa cartella e con lo stesso nome, e si aggiorna **sempre nello stesso file**: il **link non cambia mai**, quindi basta incollarlo **una volta** su Classroom.

Ardesia lo **condivide da sé con la scuola**: lo apre chi ha il link e un account @liceofoscarini.it, quindi gli alunni anche da casa, dal telefono o dal tablet. Da *File → PDF per la classe…* (o *Impostazioni → Questo blocco appunti*):

- **Copia il link per gli alunni** – da incollare su Classroom;
- **Apri il PDF su Drive** – lo apre nel browser;
- l'ora dell'ultimo aggiornamento («Su Google Drive, aggiornato oggi alle 12:05»).

Se l'amministratore di Google della scuola non permette ad Ardesia di condividerlo, Ardesia lo dice e spiega come farlo a mano: *Apri il PDF su Drive* → **Condividi** → in *Accesso generale* scegli **liceofoscarini.it** (chiunque abbia il link). Quando l'amministratore lo permette, **Riprova a condividere**.

Come per i file sul PC, il PDF si rifà quando chiudi Ardesia o apri un altro blocco, se ci sono modifiche, e le sezioni escluse (per esempio le verifiche) restano fuori.

## Versioni precedenti

Drive conserva le **versioni precedenti** di ogni file per circa 30 giorni. Per recuperarne una: sul sito di Google Drive, apri il menu del file (⋮ o clic destro) e cerca **Gestisci versioni** (in alcune versioni del sito è sotto *Informazioni sul file*).

## Spazio sul PC

Le copie dei blocchi su Drive occupano spazio nel profilo del tuo utente di Windows. In *Impostazioni → Questo PC*, riquadro **Google Drive**, si vede quanto e c'è **Svuota la cache**: i blocchi si riscaricano quando li riapri (quelli con modifiche ancora da caricare restano).

## E Google Drive per desktop?

Chi lo usa già può continuare: un blocco in una cartella di `G:` si salva come un file qualsiasi e Drive per desktop lo carica in rete, con il PDF per la classe accanto. Il limite è che sui PC delle LIM Drive per desktop spesso non c'è o non è configurato per il proprio account; i blocchi creati su Drive **da Ardesia** invece si aprono ovunque.

Ardesia **non sposta** su Drive i blocchi che hai già: per portarne uno su Drive, crea un blocco nuovo su Drive e copiaci le pagine (seleziona tutto, copia, incolla), oppure continua a usarlo da `G:`.
