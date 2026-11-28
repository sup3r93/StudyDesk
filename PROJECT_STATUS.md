# StudyDesk project status / Stato del progetto

Updated: 2026-11-28. Local verification complete on feat/offline-sync-foundation; consolidated dev
review pending. No application release or VPS deployment. The existing Studiario service and
unrelated projects remain untouched.

## English

### Verified local foundation

- The coordinator ran 81 Release server/security/synchronization tests: 11 core, 41 HTTP API and 29
  storage integration tests, with zero failures. Five independent desktop storage/restart tests also
  passed.
- The coordinator independently ran 75 Release native-boundary/localization/binding tests, all
  passing. Real packaged Chromium tests verify readiness, oversized-message rejection before the
  subscriber, editor changes, durable autosave, native close, archive reopening, empty-state actions
  and accessible item names.
- The actual UI was inspected in Italian and English at 1320 by 860, 1280 by 800 and 1024 by 768. A
  real keyboard edit survived language changes and reopening. Both language preferences survived
  separate process restarts without an environment override. User titles and text were unchanged.
- The new window, themed dialogs, application icon, compact toolbar, note library and section tabs
  are integrated. The editor uses A4 print layout; a 32-paragraph synthetic note was visually
  checked across all three sheets, including both page gaps. Details and Secondary actions work;
  Progress and Sources remain disabled with localized explanations, pending their functional
  milestones.
- The catalogs contain 198 matching Italian/English keys. Visible strings, validations, native
  accessible names and the embedded-editor context menu use those resources.
- Server Release build passed with zero warnings/errors. Local checks used SDK 11.0.100 targeting
  .NET 10/C# 14; execution with SDK 10 is not claimed. Locked restores passed for both solutions;
  the shared desktop storage core is explicitly included in the server solution's Release mapping.
- Repository text formatting and server C# formatting passed. Desktop C# import ordering was
  corrected and the coordinator independently verified the complete formatter check. No runtime
  behavior changed in that step.
- Local secret scanning includes tracked and untracked publishable source; it passed with redacted
  output and without staging. AI_RULES.md and private runtime data remain ignored.

See [the desktop UI verification report](docs/DESKTOP_UI_VERIFICATION.md) for observed behavior,
contrast measurements, exact testing limits and pending assistive-technology checks. Automated
checks and screenshots do not establish legal or complete accessibility conformance.

### Security integration gate

The missing editor bundle, read-only resource binding crash and disposed-frame bridge failure were
reproduced and corrected. The bridge now freezes actual main-frame/origin facts synchronously,
limits messages before subscriber dispatch and checks limits again before the UI queue. Only
installed local files and bounded embedded raster images can load; failed native factory
registration blocks editor navigation, and ordinary network fallback is disabled.

Daybreak's read-only follow-ups closed the CEF findings, the exact failed-registrar callback test
and the Windows bootstrap implementation. The coordinator ran both real Chromium executables in
Release: normal readiness/edit/autosave/reopen and a failed native registration with one registrar
call, zero bridge events and no editor navigation. Both exited successfully. The bootstrap preserves
the full CefSharp dependency check; only the exact optional app-local FXC absence is allowed. It
refuses local copies and verifies a Windows SystemDirectory module handle, held until after
Cef.Shutdown. Other missing dependencies remain fatal. CefSharp's internal dispatch dependency is
pinned and retains the real-runtime upgrade gate.

Preexisting server databases and SQLite sidecars are checked for foreign permissions/reparse points;
tests cover rejection without changing their ACLs. The former full HTTP snapshot route returns a
small authenticated 410 response and never materializes a complete workspace.

### Ownership and interfaces

| Area                                                                        | Exclusive writer                               |
| --------------------------------------------------------------------------- | ---------------------------------------------- |
| Native window, dialogs, editor and desktop boundary/runtime tests           | desktop_app                                    |
| Language service/catalogs and server host/API tests                         | server_host                                    |
| Theme, assets, core stores/integration tests and dedicated upstream notices | storage_engine                                 |
| Contracts, root configuration, CI, integration and project status           | Coordinator                                    |
| Independent contract tests and protocol review                              | Chat #2 and Chat #3; integrated by coordinator |
| Security review                                                             | Daybreak; read-only Audit Report before fixes  |

`SqliteLocalWorkspaceStore` exposes InitializeAsync, GetSpacesAsync, GetPagesAsync, GetPageAsync,
SaveSpaceAsync, SavePageAsync, SetPageTrashAsync, GetPendingOperationsAsync, ApplySyncAsync,
GetSyncIssuesAsync and ResolveSyncIssueAsync. `SqliteServerWorkspaceStore` exposes InitializeAsync,
GetSnapshotAsync, GetSnapshotChunkAsync and ApplyOperationsAsync. Shared DTOs live in
server/src/StudyDesk.Contracts; the wire contract is documented in docs/SYNC_PROTOCOL.md.

GET /v1/server is minimal public identity. GET /v1/access is a small owner-authenticated identity
check. GET /v1/snapshot/chunks reads at most 64 spaces and one page in a coherent transaction;
identity, generation and cursor must match across all chunks before atomic local application. POST
/v1/sync/push accepts at most 100 operations, 16 MiB HTTP and 2 MiB per page. Device list/revocation
requires an owner association. Authorization is rechecked after request-body reading.

Provisioning creates a runtime 64-character token in a private, non-overwritten pairing file. The
server keeps a SHA-256 verifier of decoded random bytes. The desktop atomically protects destination
and token together with Windows DPAPI. Credentials never enter the editor. Validated HTTPS is
required except explicitly enabled literal-loopback development connections.

### Remaining gates and later milestones

- The locally qualified functional block has 161 unique passing tests plus two real Chromium runtime
  scenarios. Desktop/server Release builds have zero warnings/errors; the source scan includes 160
  publishable files, including untracked source.
- All eight upstream notices match their source hashes in the final Release output. The optional
  Windows FXC DLL is not redistributed; Chromium runs using the verified OS module. A clean-machine
  installer/prerequisite walkthrough remains a later release gate.
- Application CI is prepared locally with Linux server and Windows desktop jobs, including explicit
  positive/negative packaged-editor executables. No application GitHub CI run is claimed.
- Prepare one consolidated development pull request for the qualified block. No main merge,
  installer or application release is implied by that review.

Later work includes functional Progress/Sources, attachments/media tabs, print settings, clinical
scales, search, scoped sharing, CRDT collaboration, DOCX, Windows Hello, recovery/retention UI, a
qualified installer and Studiario migration. PDF/JSON export and recoverable trash exist in this
draft but their full native-dialog walkthrough is not yet qualified. Notes are stored locally in
SQLite without database encryption; only the device credential is Windows-protected. Client/server
replicas do not replace tested independent recovery copies. No email, SMTP, Keycloak, user-account
service, FAQ, support service or AI feature. Issues go to GitHub without automatic data uploads.

## Italiano

### Risultati verificati

La nuova grafica e l'interfaccia italiano/inglese sono integrate: finestra, dialoghi, icona,
navigazione, menu editor e nomi accessibili. Le 198 chiavi corrispondono fra le lingue. Il layout di
stampa si adatta alla finestra; editor, Dettagli e Azioni secondarie sono stati osservati anche su
dimensioni da portatile. Avanzamento e Fonti restano disabilitate finché le loro funzioni non
saranno implementate.

Il collaudo reale ha verificato modifica da tastiera, salvataggio offline, cambio lingua senza
alterare appunti e recupero della bozza dopo il riavvio. Sia italiano sia inglese vengono conservati
fra processi diversi. Sono passati 81 test server/sincronizzazione e cinque test indipendenti
dell'archivio desktop. Il coordinatore ha verificato anche i 75 test Release
dell'interfaccia/confini nativi e le due prove separate con Chromium reale: in totale 161 test
distinti, oltre ai due scenari runtime. La compilazione Release server passa senza avvisi.

Daybreak ha confermato corretti i due rilievi di produzione del collegamento editor: limite dei
messaggi prima dell'evento e blocco del caricamento se manca la registrazione locale, senza ripiego
sulla rete. Anche la prova negativa del callback e il bootstrap del componente Windows sono stati
verificati e chiusi da Daybreak in sola lettura. Le due prove reali sono passate in Release; gli
otto avvisi hanno hash identici e la DLL FXC locale non viene distribuita. Sono stati osservati
tutti e tre i fogli di un documento sintetico lungo, con testo continuo e margini corretti. La
finestra iniziale si adatta all'area utile del monitor primario.

I dettagli e i limiti del collaudo sono nel rapporto collegato sopra: restano prove su alta densità,
Assistente vocale, contrasto elevato, dialoghi dei file ed esportazione di tabelle e media su più
fogli. Non viene dichiarata conformità normativa completa. Le prove automatiche non sostituiscono
quelle manuali mancanti.

### Stato della consegna

Il blocco è qualificato localmente e viene preparato per una PR consolidata verso dev; la CI è
predisposta ma non ancora verificata su GitHub. Nessun rilascio, migrazione, modifica VPS o
rimozione di Studiario. Le chat lavorano su file distinti e Daybreak verifica la sicurezza prima
delle correzioni. AI_RULES.md resta escluso da Git. Condivisione, collaborazione, allegati, scale
cliniche, DOCX, Windows Hello, recupero/retention e installer appartengono alle fasi successive; il
rapporto non li dichiara completati.
