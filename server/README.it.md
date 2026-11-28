# StudyDesk Server

[English](README.md)

StudyDesk Server è un servizio facoltativo di sincronizzazione, ospitato dal proprietario, per le
sue installazioni Windows. Gli appunti restano utilizzabili offline su ogni desktop. Questa
implementazione è stata scritta con l'assistenza dell'AI ed è verificata progressivamente; non è una
versione definitiva.

## Fondazione implementata

- Server HTTP C#/.NET 10 con spazi, pagine JSON, revisioni e ricevute delle operazioni salvati in
  SQLite.
- Token indipendenti di 64 caratteri generati durante l'associazione; il server conserva
  verificatori SHA-256 anziché credenziali riutilizzabili come bearer.
- Associazione locale del proprietario, snapshot autenticati e gruppi di operazioni condizionali e
  idempotenti.
- Elenco e revoca dei dispositivi. Un caricamento revocato viene ricontrollato prima di applicare
  dati.
- Indirizzi di ascolto espliciti, cartelle private, caricamenti limitati e una sola istanza per
  cartella dati.

Non sono previsti registrazione, account centrale, accesso con password, email, SMTP o Keycloak.
Ogni proprietario sceglie il proprio server; la repository non incorpora indirizzi personali o
credenziali.

## Compilazione e configurazione

Il server usa .NET 10 e richiede il relativo SDK per compilare oppure il runtime ASP.NET Core per
eseguire una pubblicazione che dipende dal runtime installato. Le verifiche locali attuali sono
state eseguite su Windows. Distribuzione Linux, certificati di produzione e pacchetti di
distribuzione non sono stati collaudati in questa fase.

Eseguire dalla cartella principale della repository:

```text
dotnet build server/src/StudyDesk.Server/StudyDesk.Server.csproj
```

Creare un **file di ambiente privato, dedicato al server e fuori dalla repository**, usando le voci
pertinenti di [`.env.example`](../.env.example). Prima dei comandi seguenti, impostare nell'ambiente
del processo `STUDYDESK_SERVER_ENV_FILE` con il percorso assoluto del file. La scelta è esplicita:
il server non cerca file `.env` nella cartella corrente. Le variabili già impostate nel processo
prevalgono sui valori del file.

| Variabile                                             | Scopo                                                                             |
| ----------------------------------------------------- | --------------------------------------------------------------------------------- |
| `STUDYDESK_SERVER_ENV_FILE`                           | Percorso assoluto del file di ambiente privato selezionato.                       |
| `STUDYDESK_SERVER_DATA_DIR`                           | Cartella privata assoluta per SQLite e blocco dell'istanza.                       |
| `STUDYDESK_SERVER_ORIGIN`                             | Origine HTTPS vista dai client, senza credenziali, percorso, query o frammento.   |
| `STUDYDESK_SERVER_ALLOW_INSECURE_LOOPBACK`            | Predefinito `false`; eccezione HTTP esplicita per sviluppo locale.                |
| `ASPNETCORE_URLS`                                     | Indirizzi di ascolto separati da punto e virgola, con IP espliciti o `localhost`. |
| `ASPNETCORE_Kestrel__Certificates__Default__Path`     | Percorso assoluto del certificato PFX privato per HTTPS diretto.                  |
| `ASPNETCORE_Kestrel__Certificates__Default__Password` | Password PFX fornita privatamente, quando richiesta.                              |

Esempio di ascolto HTTPS: `https://127.0.0.1:7443`, da sostituire con l'interfaccia esatta da
esporre. L'origine pubblica può contenere un nome DNS, come `https://sync.example.invalid`;
l'indirizzo di ascolto non può contenere nomi DNS. `0.0.0.0` e `::` sono ammessi solo come scelta
esplicita di ascolto su tutte le interfacce. Configurazioni aggiuntive `Kestrel:Endpoints` vengono
rifiutate per impedire l'apertura di ulteriori indirizzi.

HTTPS diretto usa il certificato predefinito di Kestrel. Il certificato deve comprendere il nome
usato dai client e avere una catena considerata attendibile dai client. Conservare certificato e
password in archivi privati del sistema operativo; non inserire la password nella riga di comando o
nel codice. Consultare la
[documentazione Microsoft dei certificati Kestrel per .NET 10](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0#configure-https-in-appsettingsjson).

Solo per prove locali HTTP, scegliere origine e ascolto su un IP loopback letterale, per esempio
`http://127.0.0.1:5091`, e abilitare esplicitamente l'eccezione. Il server controlla anche gli
indirizzi locali e remoti reali della connessione. Header `Host` o forwarded falsificati non
concedono questa eccezione. Il desktop richiede il proprio consenso esplicito all'HTTP locale:
vedere la [configurazione desktop](../desktop/README.it.md).

Le nuove cartelle dati sono create private. Su Windows vengono rifiutati permessi esistenti concessi
a soggetti diversi dall'operatore, SYSTEM e amministratori locali; su Unix vengono rifiutati
permessi per gruppo o altri utenti. Collegamenti simbolici e junction nella catena della cartella
vengono rifiutati. Scegliere una cartella locale dedicata, non condivisa. Anche file di ambiente e
certificati devono restare privati. Gli appunti SQLite non sono cifrati in questa fase.

I file esistenti `devices.sqlite3` e `workspace.sqlite3`, insieme ai rispettivi file `-wal` e
`-shm`, vengono controllati prima che avvio e associazione locale aprano SQLite. Anche ogni apertura
dell’archivio dispositivi e ogni operazione autorizzata sugli appunti ricontrollano i file
pertinenti. Permessi ampliati, collegamenti simbolici e reparse point vengono rifiutati senza
riscrivere file o modificare permessi esistenti. Soltanto i database mancanti vengono creati
privati; per dati già presenti è richiesta una correzione esplicita della configurazione da parte
dell’operatore.

## Associazione dei dispositivi e avvio

Associare ciascun PC con un **comando locale attendibile sul server**. Dalla cartella principale
della repository, dopo aver selezionato l'ambiente del server:

```text
dotnet run --project server/src/StudyDesk.Server/StudyDesk.Server.csproj -- device create --name Desktop --output <ABSOLUTE_PRIVATE_PATH.studydesk-pairing.json>
```

Sostituire il segnaposto con un percorso assoluto reale. Il comando crea un nuovo JSON privato con
`serverUrl`, `serverId`, `deviceId` e `token`. Rifiuta la sovrascrittura e non stampa il token. Se
la consegna del file fallisce, revoca l'associazione appena creata. Il suffisso
`.studydesk-pairing.json` è obbligatorio ed è escluso da Git. Creare un file separato per il
portatile, senza riutilizzare l'associazione del fisso.

Trasferire privatamente ciascun file al PC previsto. Nella finestra desktop attuale **Collega un
server**, inserire origine e token contenuti nel file. Il desktop verifica il server e protegge
l'associazione per l'utente Windows corrente. L'importazione automatica del file non è ancora
implementata; la credenziale non deve essere incollata in documenti o issue. Eliminare le copie in
chiaro non necessarie dopo la configurazione riuscita.

Avviare il server con lo stesso ambiente:

```text
dotnet run --project server/src/StudyDesk.Server/StudyDesk.Server.csproj -- serve
```

È possibile associare un altro dispositivo mentre il server è acceso. Una sola istanza HTTP può
utilizzare una cartella dati; un file privato resta aperto in esclusiva fino alla chiusura
dell'istanza. Un secondo server sulla stessa cartella viene rifiutato, evitando controlli di revoca
indipendenti.

## API e limiti

| Metodo e indirizzo                 | Accesso e risultato                                                               |
| ---------------------------------- | --------------------------------------------------------------------------------- |
| `GET /v1/server`                   | Identità, generazione e versione del protocollo pubbliche, conservate in memoria. |
| `GET /v1/access`                   | Token Owner; sola identità in memoria, per associare senza scaricare appunti.     |
| `GET /v1/snapshot/chunks?offset=0` | Token Owner; blocchi limitati con cursore verificato per continuare.              |
| `GET /v1/snapshot`                 | Token Owner; HTTP 410 con breve indicazione del percorso a blocchi.               |
| `POST /v1/sync/push`               | Token Owner; ricevute condizionali delle operazioni e cursore.                    |
| `GET /v1/devices`                  | Token Owner; metadati delle associazioni senza token o verificatori.              |
| `POST /v1/devices/{id}/revoke`     | Token Owner; revoca idempotente di un'associazione esistente.                     |

La credenziale va in un solo header `Authorization: Bearer`, mai nell'URL o nel corpo. L'identità
del dispositivo deriva dall'associazione autenticata. Questa prima fase crea **solo Owner**: ogni
dispositivo associato può accedere all'intero archivio dell'installazione. Permessi di lettura o
modifica limitati a singoli spazi e inviti non sono ancora implementati.

Ogni invio ammette al massimo **100 operazioni**, **16 MiB di JSON HTTP** e **15 secondi per leggere
il corpo**. Il limite della pagina JSON decodificata resta **2 MiB UTF-8**. Lo spazio aggiuntivo
durante il trasferimento serve all'escaping JSON e non aumenta il limite del documento. Richieste
troppo grandi restituiscono HTTP 413, letture scadute HTTP 408, gruppi malformati HTTP 400 e tipi di
contenuto non ammessi HTTP 415. La lettura avviene fuori dal blocco delle revoche; l'autorizzazione
viene ricontrollata nel blocco immediatamente prima di applicare le operazioni.

Le risposte di invio riuscite possono contenere ricevute `conflict` o `invalid`. Il client deve
controllare ogni ricevuta: HTTP 200 non implica che tutte le operazioni siano accettate. Ripetizioni
di operazioni già accettate restituiscono `duplicate`. Una generazione diversa restituisce HTTP 409
con la generazione attuale: conservare le bozze locali e fermare gli invii finché l'installazione
non è stata controllata e non è stato acquisito un nuovo snapshot.

Il download predefinito parte da `/v1/snapshot/chunks?offset=0`. Ogni risposta contiene al massimo
64 spazi e una pagina, identità del server, generazione, cursore e `nextOffset` facoltativo.
Continuare con `offset=nextOffset&cursor=<cursore-della-prima-risposta>` finché `nextOffset` diventa
null. Tutti i blocchi devono mantenere identità, generazione e cursore: assemblare e verificare lo
snapshot intero prima di applicarlo all'archivio desktop. Se i dati cambiano durante il
trasferimento, HTTP 409 contiene solo un messaggio generico di riavvio. Scartare i dati parziali del
trasferimento e ricominciare il download senza sostituire gli appunti locali. Posizioni di
continuazione mancanti o errate danno HTTP 400. Il vecchio percorso completo non restituisce più
documenti: un proprietario autorizzato riceve una piccola risposta HTTP 410 che indica
`/v1/snapshot/chunks?offset=0`. Senza autenticazione resta HTTP 401; ruoli diversi da Owner ricevono
HTTP 403. Associazione e sincronizzazione usano soltanto gli indirizzi limitati.

## Verifiche e limiti attuali

Test mirati, dalla cartella principale della repository:

```text
dotnet test server/tests/StudyDesk.Server.Tests/StudyDesk.Server.Tests.csproj
dotnet test server/tests/StudyDesk.Server.Api.Tests/StudyDesk.Server.Api.Tests.csproj
```

I test API usano dati temporanei privati e connessioni Kestrel reali su loopback. Verificano
autorizzazioni, rifiuto dei verificatori, revoca durante un invio lento già iniziato, istanza
esclusiva, associazione locale a server acceso, scritture condizionali, ripetizioni, generazione
diversa, file privati senza sovrascrittura, permessi Windows e limiti degli invii. Una regressione
Unicode invia un documento valido di 1.800.085 byte con una richiesta superiore a 5,4 MB e controlla
il contenuto esatto ricaricando lo snapshot. Questi test non certificano TLS di produzione o
distribuzione Linux.

Un test completo usa due veri archivi desktop SQLite e HTTP reale: salvataggio offline, primo invio,
modifica dell'altro client, conservazione del conflitto dopo riapertura, risoluzione esplicita
contro la versione osservata e successiva riapertura con coda e problemi vuoti. Il download usa i
blocchi. Un'altra regressione ricostruisce 17 pagine il cui JSON completo supera 32 MiB, con singoli
blocchi osservati intorno a 2 MB; il limite delle risposte del desktop non viene aumentato.

Ulteriori prove di sicurezza rifiutano database e file WAL/SHM Windows preesistenti con permessi
ampliati e verificano che byte e ACL restino invariati. Il rifiuto dei collegamenti simbolici è
stato provato nella sessione Windows locale, controllando anche l’integrità del file di
destinazione. Il percorso completo dismesso è verificato come risposta HTTP 410 limitata per Owner,
con HTTP 401/403 per chi non ha l’accesso richiesto.

Collaborazione Yjs/CRDT in tempo reale, trasferimento allegati, permessi condivisi per spazio,
inviti, backup/ripristino automatici, navigazione delle revisioni e installazione come servizio
restano attività separate. SQLite conserva revisioni, ma in questa fase non esiste un comando di
ripristino gestito. Non modificare o sostituire database attivi; un vecchio database non può essere
ripristinato in sicurezza sul posto senza un flusso coordinato di generazione e recupero.
Sincronizzazione e una seconda replica non sostituiscono backup verificati. Questo lavoro non
modifica Studiario esistente.

Consultare [architettura](../docs/ARCHITECTURE.md), [sicurezza](../SECURITY.md) e
[standard del codice](../docs/CODING_STANDARDS.md).
