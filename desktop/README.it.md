# StudyDesk Desktop

[English](README.md)

StudyDesk è un'applicazione Windows per appunti personali utilizzabile offline. Questa
implementazione è stata scritta con l'assistenza dell'AI ed è in fase di verifica progressiva; non è
una versione definitiva.

## Interfaccia e lingua

Il desktop usa una struttura scura compatta, una raccolta di spazi e appunti, un foglio A4 bianco e
schede distinte per documento e azioni secondarie. Il selettore nella barra del titolo cambia
interfaccia nativa, finestre, messaggi, nomi accessibili ed editor tra italiano e inglese. La scelta
viene conservata localmente; **Lingua di sistema** sceglie italiano quando la cultura
dell'interfaccia Windows è italiana, altrimenti inglese. Testi e titoli degli appunti non vengono
tradotti.

La finestra iniziale è centrata e limitata all'area di lavoro del monitor primario nelle unità WPF;
anche i minimi si riducono quando serve per uno schermo portatile con ridimensionamento Windows.
Restano disponibili ridimensionamento della finestra e scorrimento del documento. I passaggi tra
monitor con densità diverse richiedono ancora verifica manuale.

Il gestore di accessibilità CefSharp incluso è sperimentale e di sola lettura. Sono implementati
focus da tastiera, etichette e controlli nativi. Le prove manuali a 1320×860, 1280×800 e 1024×768
sono state completate in entrambe le lingue: vedi il
[registro di verifica UI](../docs/DESKTOP_UI_VERIFICATION.md). Restano da verificare modifica con
lettore di schermo, schermi ad alta densità e finestre native per i file. I soli test automatici non
costituiscono una dichiarazione di conformità accessibile.

## Base implementata

- Creazione immediata di spazi locali e appunti, senza registrazione né server.
- Editor Tiptap open source incluso nell'applicazione: formattazione di base, titoli, elenchi,
  tabelle, annulla/ripeti e immagini PNG/JPEG incorporate.
- Presentazione in layout di stampa A4. L'impaginazione riguarda la visualizzazione e non inserisce
  elementi artificiali nel documento salvato.
- Salvataggio durevole di appunti e operazioni di sincronizzazione in SQLite. Il salvataggio
  automatico viene confermato dopo la transazione locale. La chiusura acquisisce prima l'ultima
  versione del testo dall'editor.
- Apertura degli appunti esistenti in lettura, con attivazione esplicita della modifica.
- Schede Editor, Dettagli e Azioni secondarie. Avanzamento e Fonti sono visibilmente disabilitate
  perché questi flussi non sono implementati nella milestone corrente.
- Cestino reversibile, ripristino, esportazione JSON strutturata ed esportazione PDF mediante il
  motore di stampa Chromium locale.
- Associazione facoltativa a un proprio server e sincronizzazione manuale. In caso di errore di rete
  gli appunti locali restano disponibili. Identità e generazione impediscono la sincronizzazione
  silenziosa verso un server sostituito.
- **Segnala un problema** apre il modulo GitHub nel browser di sistema. L'app non invia
  automaticamente appunti, registri, credenziali o altri dati diagnostici.

## Compilazione e avvio

Questa è una compilazione di sviluppo dipendente dal runtime, non un installer o una distribuzione
autosufficiente. Per compilarla servono Windows x64, un SDK .NET capace di usare .NET 10/C# 14 e
Node.js/npm compatibili con gli strumenti editor fissati nel progetto. La prima compilazione scarica
le dipendenze; l'editor completo è locale e l'uso offline ordinario non richiede Node.js o npm.

Per avviare una copia su un altro PC Windows x64 serve
[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), comprensivo di
WPF: il solo runtime console non basta. Il pacchetto .NETCore di CefSharp richiede anche un runtime
Visual C++ x64 compatibile, come indicato dalla
[documentazione di ridistribuzione](https://github.com/cefsharp/CefSharp/wiki/Output-files-description-table-%28Redistribution%29).
Microsoft distribuisce il
[Visual C++ v14 Redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist/)
compatibile attuale. Questi prerequisiti non vengono inclusi o installati automaticamente.

Windows 10/11 fornisce il componente facoltativo `d3dcompiler_47.dll`: StudyDesk esclude la copia
locale dagli output di compilazione, pubblicazione e test, senza disattivare la GPU. I controlli di
compilazione e pubblicazione rifiutano output precedenti che contengono ancora quella DLL. Il
distinto componente open source `dxcompiler.dll` resta incluso con gli altri file Chromium. L'avvio
esegue lo stesso controllo completo delle dipendenze ufficiale e ammette soltanto quel percorso
locale mancante dopo aver verificato il modulo caricato dal percorso assoluto di sistema Windows. Il
modulo resta caricato fino alla chiusura di Chromium. La prova reale di editor e salvataggio passa
sul PC Windows attuale senza FXC locale; altre versioni Windows e installazione su PC pulito restano
non qualificate.

L'intera cartella di output, le dipendenze Chromium native e gli avvisi `ThirdParty/` devono restare
insieme. Installazione e rilevamento dei prerequisiti su PC pulito non sono qualificati. Non vengono
dichiarati installer firmato, configurazione automatica, distribuzione autosufficiente o
compatibilità verificata su macchina pulita.

Dalla radice della repository:

```powershell
npm --prefix desktop/editor ci --ignore-scripts --no-audit --no-fund
npm --prefix desktop/editor run build
dotnet build desktop/src/StudyDesk.Desktop/StudyDesk.Desktop.csproj
dotnet run --project desktop/src/StudyDesk.Desktop/StudyDesk.Desktop.csproj --no-build
```

L'eseguibile attuale viene creato in
`desktop/src/StudyDesk.Desktop/bin/Debug/net10.0-windows/win-x64/`. La compilazione e la
pubblicazione includono l'editor e gli avvisi di licenza in `ThirdParty/`. Per distribuire l'app
serve l'intera cartella di output, comprensiva dei file Chromium.

Test mirati sui confini di sicurezza nativi:

```powershell
dotnet test desktop/tests/StudyDesk.Desktop.Ui.Tests/StudyDesk.Desktop.Ui.Tests.csproj
```

La [prova reale Chromium](tests/StudyDesk.Desktop.Runtime.Tests/README.md) separata avvia la
finestra nativa e l'editor su dati sintetici, verificando IPC, salvataggio, chiusura e rilettura. È
un comando eseguibile: il solo `dotnet test` non esegue questa prova.

## Configurazione privata e primo avvio offline

Al primo avvio StudyDesk crea `%LOCALAPPDATA%/StudyDesk/.env` con percorsi assoluti per lo spazio
locale e l'associazione protetta al server. Per l'uso locale non servono nome utente, password o
token. Crea uno spazio, crea un appunto e inizia a scrivere. Puoi collegare un server in seguito.

La configurazione viene letta dal file `.env` privato; le variabili del processo hanno precedenza.
Le variabili supportate sono:

| Variabile                                   | Scopo                                                                                             |
| ------------------------------------------- | ------------------------------------------------------------------------------------------------- |
| `STUDYDESK_DESKTOP_ENV_FILE`                | Percorso assoluto facoltativo di un altro `.env` privato, utile per installazioni o test isolati. |
| `STUDYDESK_DESKTOP_DATA_DIR`                | Cartella assoluta con `workspace.sqlite3` e i file locali del browser.                            |
| `STUDYDESK_DESKTOP_TOKEN_FILE`              | Percorso assoluto dell'associazione al server protetta per l'utente Windows.                      |
| `STUDYDESK_DESKTOP_CONNECTION_FILE`         | Percorso assoluto della copia JSON consultiva e non segreta dell'identità del server.             |
| `STUDYDESK_DESKTOP_LANGUAGE`                | `auto`, `it` oppure `en`; il selettore conserva la preferenza locale.                             |
| `STUDYDESK_DESKTOP_ALLOW_INSECURE_LOOPBACK` | `false` predefinito; eccezione esplicita solo per HTTP di sviluppo su IP loopback letterale.      |

Le nuove cartelle private ricevono permessi per l'utente Windows corrente, SYSTEM e gli
amministratori locali. Le cartelle esistenti accessibili ad altri utenti vengono rifiutate, compresi
i permessi solo ereditabili che esporrebbero i nuovi file. File di configurazione, database e
associazione già esistenti vengono verificati per permessi estranei e reparse point prima
dell'utilizzo. Scegli un percorso locale privato, evitando cartelle condivise e giunzioni del
filesystem. Questa milestone non cifra gli appunti SQLite; protegge invece le credenziali del server
tramite Windows DPAPI per l'utente Windows corrente.

## Server dati facoltativo

Scegli **Connetti un server dati**, inserisci l'origine HTTPS e il token del dispositivo di 64
caratteri, quindi conferma che lo spazio locale debba essere sincronizzato con quel server. La
finestra verifica l'identità con una richiesta autenticata leggera prima di salvare l'associazione;
l'associazione non deve scaricare tutti gli appunti. Destinazione e token sono protetti insieme in
un unico file sostituito atomicamente. Il JSON consultivo non viene mai usato per scegliere dove
inviare la credenziale. I token restano nel trasporto C# nativo e non entrano nell'editor
incorporato.

**Sincronizza** invia le operazioni in ordine e scarica uno snapshot aggiornato. Il trasferimento
avviene a blocchi con identici server, generazione e cursore. Un download interrotto o una
cronologia cambiata non vengono applicati né usati per confermare operazioni locali. Ogni risposta è
limitata a 32 MiB; questa versione supporta snapshot fino a 256 MiB di JSON e 100.000 elementi per
sincronizzazione. Raccolte superiori richiedono un futuro trasferimento incrementale; gli appunti
locali restano disponibili quando viene raggiunto un limite. Questa milestone usa versioni server e
aggiornamenti condizionati; non offre ancora collaborazione CRDT o fusione automatica dei conflitti.
Le modifiche locali in conflitto vengono conservate invece di essere sovrascritte silenziosamente.
Azioni secondarie elenca i problemi durevoli e permette una scelta esplicita tra riprovare la
propria versione e usare quella del server; in questo secondo caso crea prima un JSON locale di
recupero. La sincronizzazione può essere annullata senza eliminare le operazioni locali non
confermate. L'associazione non crea un servizio centrale di account condivisi.

In produzione è richiesto HTTPS con verifica ordinaria del certificato. Il trasporto nativo non
segue reindirizzamenti e non usa proxy. HTTP di sviluppo richiede contemporaneamente
`STUDYDESK_DESKTOP_ALLOW_INSECURE_LOOPBACK=true` e un'origine su IP loopback letterale, per esempio
`http://127.0.0.1:5091`; l'eccezione non accetta nomi DNS, incluso `localhost`.

## Limiti attuali

L'editor nativo accetta documenti fino a 1 MiB e immagini PNG/JPEG incorporate fino a 256 KiB
ciascuna; gli stessi controlli precedono il caricamento dei documenti salvati o sincronizzati. In
questa base non sono accettate immagini remote, SVG o incorporamenti attivi. Le risorse dell'editor
provengono esclusivamente dal pacchetto installato e non accedono alla rete esterna. La
registrazione nativa deve riuscire prima del caricamento dell'indirizzo locale; ogni richiesta
locale disabilita inoltre il caricatore di rete ordinario e riceve sempre una risposta file/errore.
Il messaggio del bridge è limitato a 2 MiB UTF-8 prima della creazione dell'evento e ricontrollato
dal subscriber; resta più restrittivo il limite dei singoli documenti.

PDF e JSON sono implementati. Importazione/esportazione DOCX, schede allegati e media, impostazioni
di stampa personalizzate, blocchi di studio, scale cliniche, ricerca, spazi condivisi, Windows
Hello, installer firmato e migrazione dei dati Studiario restano attività distinte. Questa milestone
non elimina né modifica il servizio Studiario esistente. Il livello di archiviazione conserva
revisioni SQLite; non è ancora presente un'interfaccia per esplorarle e recuperarle. Copie locali e
sincronizzazione non sostituiscono backup indipendenti verificati.

## Componenti open source

L'host usa CefSharp 156.0.300 (licenza in stile BSD), Chromium Embedded Framework 156.0.30 (licenza
CEF in stile BSD e avvisi Chromium) e Tiptap 3.35.1 (MIT), basato su ProseMirror. Gli avvisi di
ridistribuzione dei pacchetti installati sono conservati in `src/StudyDesk.Desktop/ThirdParty/`;
`Editor.NOTICES.txt` include le licenze delle dipendenze di produzione dell'editor. Non sono
richieste estensioni Tiptap proprietarie né servizi ospitati.

Consulta [architettura](../docs/ARCHITECTURE.md), [roadmap](../docs/ROADMAP.md) e
[standard di sviluppo](../docs/CODING_STANDARDS.md) per il piano complessivo.
