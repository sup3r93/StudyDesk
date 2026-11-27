# StudyDesk

[English](README.md) · [Desktop](desktop/README.md) · [Server](server/README.md)

Uno spazio di studio per Windows utilizzabile offline, con un server dati e di sincronizzazione
facoltativo e gestito dall'utilizzatore.

> **Dichiarazione sullo sviluppo con AI:** il progetto viene sviluppato principalmente tramite AI,
> in particolare OpenAI Codex, sotto la direzione del proprietario della repository. Il codice
> generato richiede revisione e verifiche. Vedi [AI_DISCLOSURE.md](AI_DISCLOSURE.md).

## Stato del progetto

**Fase di preparazione della repository e progettazione.** Applicazione, installer e protocollo di
sincronizzazione non sono ancora implementati o pubblicati. Le funzionalità elencate sono requisiti
concordati, non funzionalità già disponibili. Nessun servizio esistente è stato migrato o eliminato.

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

Il codice applicativo userà **C#, TypeScript e, quando utile, Python**. Commenti, identificativi,
interfaccia e documentazione tecnica saranno in inglese. Il README principale sarà mantenuto in
inglese e italiano. Sono ammesse soltanto librerie applicative gratuite e open source.

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

## Controllo della formattazione

Per gli strumenti della repository usare Node.js 24 LTS:

```text
npm ci --ignore-scripts
npm run check
```

Attualmente questi comandi verificano la formattazione della repository. Compilazione, analizzatori
e test di comportamento saranno aggiunti insieme ai progetti applicativi. La formattazione non
dimostra la correttezza dell'applicazione. Vedi [regole del codice](docs/CODING_STANDARDS.md).

## Migrazione

StudyDesk sostituirà Studiario dopo il trasferimento e la verifica di appunti, file, versioni e dati
personali. Prima della dismissione sarà necessaria una prova di recupero. I servizi attivi sulla VPS
rimangono invariati durante la preparazione della repository.

## Licenza

[MIT](LICENSE). I componenti di terzi conservano licenze e attribuzioni proprie. Nella repository
non devono entrare appunti personali, configurazioni private del server, token reali o dati clinici
dei pazienti.
