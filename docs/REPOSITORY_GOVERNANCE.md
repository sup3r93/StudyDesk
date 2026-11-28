# Repository governance / Governo della repository

[English](#english) · [Italiano](#italiano)

## English

This page describes the configuration in the local checkout. GitHub workflow execution and live
repository enforcement must be verified separately; local definitions are not evidence of a
successful GitHub application build.

### Branches and intended protection

`main` is the stable branch and `dev` is the integration branch. Short-lived branches normally merge
into `dev`, followed by a release pull request into `main`.

The [ruleset definitions](../.github/rulesets/) for both `main` and `dev` require pull requests,
resolved conversations and all five exact GitHub Actions check names: **Repository quality**,
**Secret scan**, **Dependency review**, **Server build and tests** and **Windows desktop build and
tests**. Each required check is bound to the GitHub Actions integration ID `15368`.

Both rulesets block force pushes and branch deletion. No independent approval is required for the
single maintainer; additional reviewers can be required when independent maintainers join. `main`
also requires an up-to-date pull request branch.

The JSON definitions must be applied through GitHub settings or its API and inspected afterward.
Both application jobs are explicitly required in each ruleset; their presence in the workflow alone
would not make them mandatory. The candidate pull request must obtain successful results for all
five checks before merging. Configuring these requirements does not establish that an application
job has passed; results must be checked on the new run after any workflow correction. Merge commits
preserve `dev` ancestry during release promotion; mandatory commit signatures remain deferred until
a signing workflow is deliberately configured.

### Configured checks

The local [CI workflow](../.github/workflows/ci.yml) targets pushes and pull requests for `main` and
`dev`, and supports manual dispatch. It defines:

| Job name                        | Runner  | Configured work                                                                                                                                                                                                    |
| ------------------------------- | ------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Repository quality              | Ubuntu  | Node.js 24, locked root npm tooling installed without lifecycle scripts, repository Prettier checks.                                                                                                               |
| Secret scan                     | Ubuntu  | Full Git-history Gitleaks scan with redacted output and the custom StudyDesk-token rule.                                                                                                                           |
| Dependency review               | Ubuntu  | Pull requests only; dependency review fails for high or critical known vulnerabilities.                                                                                                                            |
| Server build and tests          | Ubuntu  | .NET 10 locked restore, C# formatting/analyzers, Release build with warnings as errors, server and offline synchronization tests.                                                                                  |
| Windows desktop build and tests | Windows | Node.js 24 editor install/type-check/build, .NET 10 locked restore, C# formatting/analyzers, Release build, offline persistence tests, native transport/editor-boundary tests and the real CEF runtime executable. |

The projects target .NET 10 and the workflow requests SDK `10.0.x`. However,
[`global.json`](../global.json) uses `rollForward: latestMajor`, allowing a later stable SDK when
available. Therefore the configured setup version does not by itself establish the SDK selected on a
runner; the actual version and results must be read from that execution.

The Windows job explicitly launches
`desktop/tests/StudyDesk.Desktop.Runtime.Tests/StudyDesk.Desktop.Runtime.Tests.csproj`, an
executable, after the build. Its positive and negative commands, from the repository root, are:

```text
dotnet run --project desktop/tests/StudyDesk.Desktop.Runtime.Tests/StudyDesk.Desktop.Runtime.Tests.csproj --configuration Release --no-build
dotnet run --project desktop/tests/StudyDesk.Desktop.Runtime.Tests/StudyDesk.Desktop.Runtime.Tests.csproj --configuration Release --no-build -- --reject-editor-factory
```

The positive run checks the real packaged editor and autosave. The negative run injects a registrar
that returns `false` when invoked by the real CEF initialization callback, then verifies StudyDesk
rejects the registration and keeps note commands disabled. This exercises StudyDesk’s response to
the injected result; it does not demonstrate an internal registration failure inside CEF itself.
Both executable runs are separate from `dotnet test` discovery. Their presence in YAML does not
establish that GitHub has executed them successfully.

Actions are pinned to full commit SHAs and the Gitleaks image to a digest. Workflow permissions are
read-only, checkout does not persist credentials, and no runtime secrets are supplied to untrusted
pull requests. Superseded runs for the same branch or pull request are canceled. No CodeQL job is
configured in this workflow.

### Dependency maintenance

[Dependabot](../.github/dependabot.yml) is configured locally for real manifests already present.
All times below use Europe/Rome:

- npm at `/` and `/desktop/editor`, weekly on Monday at 08:00, Europe/Rome.
- NuGet at `/`, weekly on Monday at 08:15, covering the .NET projects and
  [central package versions](../Directory.Packages.props).
- GitHub Actions at `/`, weekly on Monday at 08:30.

All version updates target `dev`. npm and NuGet minor/patch updates are grouped, and each ecosystem
has a five-open-pull-request limit. No automatic merge is configured by these files. Security
alerts, security updates and the dependency graph additionally depend on live GitHub repository
settings.

### Secrets and verification

Runtime tokens, pairing bundles and personal data stay out of source control. GitHub secret scanning
and push protection complement local review; arbitrary random tokens may not match built-in
detectors. The custom Gitleaks rule adds a focused check, not a guarantee of complete detection.

Follow [the security policy](../SECURITY.md) for vulnerability reporting. Repository administrators
must verify actual workflow runs, required-check names and integration IDs, ruleset enforcement,
dependency settings and private reporting settings before treating the remote protections as active.

## Italiano

Questa pagina descrive la configurazione nella copia locale. Esecuzione dei workflow GitHub e
protezioni effettive della repository richiedono una verifica separata: le definizioni locali non
dimostrano una compilazione riuscita dell’applicazione su GitHub.

### Branch e protezioni previste

`main` è il branch stabile e `dev` quello di integrazione. I branch temporanei confluiscono
normalmente in `dev`, seguito da una pull request di rilascio verso `main`.

Le [definizioni dei ruleset](../.github/rulesets/) per entrambi i branch `main` e `dev` richiedono
pull request, conversazioni risolte e tutti e cinque i nomi esatti dei controlli GitHub Actions:
**Repository quality**, **Secret scan**, **Dependency review**, **Server build and tests** e
**Windows desktop build and tests**. Ogni check obbligatorio è associato all’integrazione GitHub
Actions con ID `15368`.

Entrambi i ruleset bloccano force push ed eliminazione dei branch. Per l’unico manutentore non è
richiesta un’approvazione indipendente; sarà possibile richiederla quando contribuiranno altri
manutentori. `main` richiede inoltre che il branch della pull request sia aggiornato.

Le definizioni JSON devono essere applicate tramite impostazioni o API GitHub e controllate dopo
l’applicazione. Entrambi i job dell’applicazione sono esplicitamente obbligatori in ciascun ruleset;
la loro sola presenza nel workflow non li renderebbe tali. Prima del merge, la pull request deve
ottenere un risultato positivo per tutti e cinque i controlli. Configurare questi requisiti non
dimostra che un job dell’applicazione sia riuscito: dopo una correzione del workflow occorre
verificare la nuova esecuzione. I merge commit conservano la continuità di `dev` durante il
rilascio; le firme obbligatorie dei commit restano rinviate finché non viene configurato un flusso
di firma.

### Controlli configurati

Il [workflow CI locale](../.github/workflows/ci.yml) riguarda push e pull request verso `main` e
`dev` e può essere avviato manualmente. Definisce:

| Nome del job                    | Runner  | Attività configurate                                                                                                                                                                                                                     |
| ------------------------------- | ------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Repository quality              | Ubuntu  | Node.js 24, installazione bloccata degli strumenti npm principali senza script di lifecycle, verifica Prettier della repository.                                                                                                         |
| Secret scan                     | Ubuntu  | Scansione Gitleaks dell’intera cronologia Git, risultati oscurati e regola specifica per i token StudyDesk.                                                                                                                              |
| Dependency review               | Ubuntu  | Solo pull request; rifiuto delle modifiche con vulnerabilità note di gravità alta o critica.                                                                                                                                             |
| Server build and tests          | Ubuntu  | Ripristino .NET 10 con lock file, formattazione/analizzatori C#, build Release con avvisi trattati come errori e test del server/sincronizzazione offline.                                                                               |
| Windows desktop build and tests | Windows | Installazione, controllo TypeScript e build dell’editor con Node.js 24; ripristino .NET 10 bloccato, formattazione/analizzatori C#, build Release, test di persistenza offline, trasporto nativo/confini editor ed eseguibile CEF reale. |

I progetti usano il target .NET 10 e il workflow richiede l’SDK `10.0.x`. Tuttavia,
[`global.json`](../global.json) imposta `rollForward: latestMajor`, consentendo un SDK stabile
successivo quando disponibile. La versione richiesta nel setup non dimostra quindi quale SDK sia
stato selezionato sul runner: versione effettiva e risultati vanno verificati nell’esecuzione.

Il job Windows avvia esplicitamente
`desktop/tests/StudyDesk.Desktop.Runtime.Tests/StudyDesk.Desktop.Runtime.Tests.csproj`, che è un
eseguibile, dopo la compilazione. I comandi positivo e negativo, dalla cartella principale della
repository, sono:

```text
dotnet run --project desktop/tests/StudyDesk.Desktop.Runtime.Tests/StudyDesk.Desktop.Runtime.Tests.csproj --configuration Release --no-build
dotnet run --project desktop/tests/StudyDesk.Desktop.Runtime.Tests/StudyDesk.Desktop.Runtime.Tests.csproj --configuration Release --no-build -- --reject-editor-factory
```

Il caso positivo verifica editor incluso e salvataggio automatico reali. Il caso negativo inserisce
un registrar che restituisce `false` quando viene invocato dal vero callback di inizializzazione
CEF, poi verifica che StudyDesk rifiuti la registrazione e mantenga disabilitati i comandi degli
appunti. Si verifica la reazione di StudyDesk al risultato inserito, senza dimostrare un guasto
interno della registrazione CEF. Entrambe le esecuzioni sono avviate separatamente dalla ricerca dei
test di `dotnet test`. La loro presenza nello YAML non dimostra che GitHub le abbia completate con
successo.

Le Action sono fissate a SHA di commit completi e l’immagine Gitleaks a un digest. I permessi del
workflow sono di sola lettura, il checkout non conserva credenziali e le pull request non
attendibili non ricevono segreti di esecuzione. Le esecuzioni superate da una nuova per lo stesso
branch o pull request vengono annullate. Questo workflow non configura alcun job CodeQL.

### Manutenzione delle dipendenze

[Dependabot](../.github/dependabot.yml) è configurato localmente per manifest già presenti. Tutti
gli orari seguenti usano Europe/Rome:

- npm in `/` e `/desktop/editor`, ogni lunedì alle 08:00, Europe/Rome.
- NuGet in `/`, ogni lunedì alle 08:15, per i progetti .NET e le
  [versioni centralizzate dei pacchetti](../Directory.Packages.props).
- GitHub Actions in `/`, ogni lunedì alle 08:30.

Tutti gli aggiornamenti di versione puntano a `dev`. Le modifiche npm e NuGet minor/patch sono
raggruppate e ogni ecosistema ha un limite di cinque pull request aperte. Questi file non
configurano merge automatici. Avvisi di sicurezza, aggiornamenti di sicurezza e grafo delle
dipendenze dipendono anche dalle impostazioni effettive della repository GitHub.

### Segreti e verifica

Token generati durante l’uso, file di associazione e dati personali restano fuori dal codice
versionato. Secret scanning e push protection GitHub affiancano la revisione locale; token casuali
arbitrari potrebbero non essere riconosciuti dai rilevatori predefiniti. La regola Gitleaks
specifica aggiunge un controllo mirato, senza garantire il rilevamento completo.

Per le vulnerabilità seguire la [politica di sicurezza](../SECURITY.md). Prima di considerare attive
le protezioni remote, gli amministratori devono verificare esecuzioni reali dei workflow, nomi dei
check obbligatori e ID delle integrazioni, applicazione dei ruleset, impostazioni delle dipendenze e
segnalazioni private.
