# Real Chromium runtime check

This project is an executable smoke check, not an xUnit test assembly. Run it on Windows x64 after
building the bundled editor:

```powershell
dotnet run --project desktop/tests/StudyDesk.Desktop.Runtime.Tests/StudyDesk.Desktop.Runtime.Tests.csproj
```

A separate rejection scenario must also pass:

```powershell
dotnet run --project desktop/tests/StudyDesk.Desktop.Runtime.Tests/StudyDesk.Desktop.Runtime.Tests.csproj -- --reject-editor-factory
```

The negative scenario injects an internal native registrar that returns false inside the actual CEF
context-initialized callback, never from JavaScript. Exit zero requires exactly one registrar
invocation, an observed failed gate, no packaged navigation, disabled document commands and zero
bridge events. Production continues to use the real native registration method by default. The
ordinary run also sends an oversized envelope through real CEF IPC and requires rejection before any
native subscriber receives it.

The check creates the actual native `MainWindow` outside the visible desktop, without activating it
or sending keyboard/mouse input. It uses a newly generated private synthetic workspace and overrides
all desktop path settings for its own process. Existing configuration, notes and server credentials
are never used. Output contains only verification flags, protocol counters and exception type names.

An exit code of zero requires the native editor-ready acknowledgement, enabled document commands, a
synthetic editor change observed through native IPC, the actual autosave transaction, the native
close pipeline and successful database rereading. It does not replace a visual, keyboard or
screen-reader review of the application.

The production bridge checks CEF's actual originating main frame and URL synchronously before its
wrapper expires. Only immutable sender facts and the bounded message then cross to WPF's dispatcher;
the document channel, page identifier, session identifier and sequence checks remain mandatory. The
runtime regression protects this boundary when the pinned CefSharp version changes.

Both scenarios refuse an app-local `d3dcompiler_47.dll` and use the production bootstrap's full
upstream dependency check plus verified, retained Windows system module. No GPU-disabling flag is
added. Separate native tests reject another missing dependency, an unavailable system compiler, an
app-local compiler and an incorrectly resolved native module. This is a check of the current test
machine, not qualification of installation prerequisites on a clean PC.

## Italiano

Questo progetto è una verifica eseguibile, non un assembly xUnit. Eseguilo su Windows x64 dopo aver
compilato il pacchetto editor, usando il comando sopra.

Anche lo scenario separato di rifiuto, eseguito con `--reject-editor-factory` come sopra, deve
passare. La prova negativa inietta un registrar nativo interno che restituisce false nel vero
callback di inizializzazione CEF, mai da JavaScript. L'uscita zero richiede una sola invocazione, il
gate effettivamente fallito, nessuna navigazione editor, comandi disabilitati e zero eventi del
bridge. In produzione il percorso predefinito continua a usare la vera registrazione nativa. La
prova ordinaria invia inoltre un messaggio troppo grande attraverso l'IPC CEF reale e verifica che
venga respinto prima di raggiungere il subscriber nativo.

La prova crea la vera `MainWindow` fuori dall'area visibile, senza attivarla e senza inviare input
da tastiera o mouse. Usa un nuovo archivio sintetico privato e sostituisce tutti i percorsi desktop
solo nel proprio processo. Non usa configurazioni, appunti o credenziali server esistenti. L'output
contiene soltanto esiti, contatori del protocollo e nomi dei tipi di eccezione.

Il codice di uscita zero richiede conferma effettiva dell'editor, comandi abilitati, modifica
sintetica ricevuta tramite IPC, salvataggio automatico reale, chiusura nativa e rilettura del
database. Non sostituisce le verifiche visive, da tastiera o con lettore di schermo.

Il collegamento di produzione verifica sincronicamente frame principale e URL effettivi di CEF prima
che il relativo wrapper scada. Solo dati immutabili e il messaggio limitato passano poi al
dispatcher WPF; restano obbligatori i controlli di canale, pagina, sessione e sequenza. La
regressione protegge questo confine quando viene aggiornata la versione CefSharp fissata nel
progetto.

Entrambi gli scenari rifiutano `d3dcompiler_47.dll` locale e usano il controllo completo delle
dipendenze e il modulo di sistema Windows verificato e mantenuto in memoria fino alla chiusura CEF.
Non vengono aggiunte opzioni per disattivare la GPU. Test nativi separati rifiutano altre dipendenze
mancanti, compilatore di sistema non caricabile, copia locale e modulo risolto nel percorso errato.
La verifica riguarda il PC di test attuale, non qualifica i prerequisiti su un PC pulito.
