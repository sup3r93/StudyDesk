# StudyDesk

[English](README.md) · [Desktop](desktop/README.md) · [Server](server/README.md)

Uno spazio di studio per Windows utilizzabile offline, con un server dati e di sincronizzazione
facoltativo e gestito dall'utilizzatore.

> **Dichiarazione sullo sviluppo con AI:** il progetto viene sviluppato principalmente tramite AI,
> in particolare OpenAI Codex, sotto la direzione del proprietario della repository. Il codice
> generato richiede revisione e verifiche. Vedi [AI_DISCLOSURE.md](AI_DISCLOSURE.md).

## Stato del progetto

**Fondazione eseguibile in verifica locale; nessuna versione applicativa pubblicata.** La bozza
contiene un host Windows WPF/CEF, l'editor Tiptap incluso, archivi SQLite, un server ASP.NET Core
protetto tramite token e test di comportamento. Il collaudo di modifica offline, interfaccia
bilingue e riavvio è passato; restano in corso i controlli finali di sicurezza e del pacchetto. La
presenza del codice non significa che tutte le funzionalità previste siano già disponibili.

Consultare [lo stato di integrazione](PROJECT_STATUS.md), la [guida desktop](desktop/README.it.md) e
la [guida server](server/README.it.md) per comportamento e limiti attuali. Permessi di condivisione,
collaborazione simultanea, allegati, Windows Hello, esportazioni complete e migrazione di Studiario
restano fasi successive. Nessun servizio esistente è stato migrato o eliminato.

## Comportamento previsto

- Scrivere subito offline, senza registrazione, account o connessione Internet.
- Ritrovare gli stessi appunti su fisso e portatile tramite il proprio server dati.
- Configurare il proprio VPS o PC principale; il progetto non offre un servizio ospitato comune.
- Condividere spazi privati con dispositivi invitati, in lettura o modifica. Lo spazio resta sul
  server del proprietario.
- Modificare contemporaneamente online e riallineare le modifiche eseguite offline.
- Scaricare gli allegati a scelta oppure usare **Scarica tutto lo spazio**.
- Attivare facoltativamente Windows Hello sul singolo dispositivo, con possibilità di recupero.
- Conservare le pagine cestinate per 30 giorni e le versioni precedenti per 90 giorni, con durate
  configurabili.
- Esportare in PDF, Word/DOCX e altri formati documentati, mantenendo un formato nativo completo.

## Struttura della repository

```text
desktop/    Applicazione Windows, archivio locale, editor e test desktop
server/     Server dati, sincronizzazione, accessi dei dispositivi e test server
docs/       Architettura, requisiti, roadmap e regole del codice
.github/    Controlli automatici, Dependabot, modelli e definizioni dei ruleset
```

Il codice applicativo usa **C#, TypeScript e, quando utile, Python**. Commenti, identificativi e
documentazione tecnica restano in inglese. L'interfaccia è disponibile in **italiano e inglese**,
con selettore e preferenza locale conservata; gli appunti scritti dall'utilizzatore non vengono
tradotti automaticamente. Il README è mantenuto in entrambe le lingue. Sono ammesse soltanto
librerie applicative gratuite e open source.

## Accesso dei dispositivi e segreti

Il modello previsto usa un **token esadecimale casuale di 64 caratteri distinto per ciascuna
associazione client/server**. I token vengono generati durante la configurazione: mai inseriti nel
codice, nei commit, negli installer o nei log. Il server conserva un verificatore; il client Windows
conserva il token in un archivio locale protetto.

Il client verifica il certificato HTTPS del server; il server verifica token e permessi del client.
Il token dimostra il possesso di un segreto, non l'identità fisica del computer. Non sono richiesti
Keycloak, login nel browser, nome utente o password. Windows Hello locale rimane facoltativo.

Vedi [architettura e decisioni concordate](docs/ARCHITECTURE.md) e
[segnalazioni di sicurezza](SECURITY.md).

## Flusso di sviluppo

- `main`: stato stabile e verificato della repository.
- `dev`: ramo di integrazione dello sviluppo.
- `feature/*`, `fix/*`, `docs/*`: rami temporanei, normalmente diretti a `dev`.
- Le modifiche verificate passano da `dev` a `main` mediante pull request.

Per entrambi i rami principali sono previsti pull request, controlli riusciti, discussioni risolte e
blocco di cancellazione e push forzati. Per questo progetto individuale non è obbligatoria
l'approvazione di un secondo revisore. Le definizioni sono in `.github/rulesets/`; la
[guida alla gestione](docs/REPOSITORY_GOVERNANCE.md) distingue i file dalle impostazioni effettive
di GitHub.

## Compilazione e verifiche

Usare Node.js 24 LTS e l'SDK .NET 10. Compilare l'editor locale prima dell'host Windows:

```text
npm ci --ignore-scripts
npm run check
npm --prefix desktop/editor ci --ignore-scripts
npm --prefix desktop/editor run build
dotnet test server/StudyDesk.Server.slnx
dotnet test desktop/StudyDesk.Desktop.slnx
dotnet run --project desktop/tests/StudyDesk.Desktop.Runtime.Tests/StudyDesk.Desktop.Runtime.Tests.csproj
```

La soluzione desktop richiede Windows x64. Il server è progettato per Windows e Linux; i test locali
su Windows non dimostrano il funzionamento su Linux. La definizione CI comprende controlli server su
Linux e desktop su Windows, ma deve riuscire su GitHub prima di dichiararla verificata lì. Le guide
dei componenti descrivono configurazione privata e avvio. L'eseguibile di collaudo dell'editor reale
viene eseguito separatamente dai test unitari. Il
[rapporto sull'interfaccia](docs/DESKTOP_UI_VERIFICATION.md) documenta risultati e limiti.

I controlli automatici affiancano il collaudo reale di modifica offline, riavvio e revisione della
sicurezza. Vedi [regole del codice](docs/CODING_STANDARDS.md).

## Migrazione

StudyDesk sostituirà Studiario dopo il trasferimento e la verifica di appunti, file, versioni e dati
personali. Prima della dismissione sarà necessaria una prova di recupero. I servizi attivi sulla VPS
rimangono invariati durante la preparazione della repository.

## Licenza

[MIT](LICENSE). I componenti di terzi conservano licenze e attribuzioni proprie. Nella repository
non devono entrare appunti personali, configurazioni private del server, token reali o dati clinici
dei pazienti.
