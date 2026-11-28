# Desktop interface verification / Verifica dell'interfaccia desktop

Date / Data: 2026-11-28. Local working draft, Windows x64; synthetic notes only.

## English

### Implemented design

The native window uses a compact caption, an application book icon, a language selector, a
workspace/server toolbar and a separate note sidebar. The content area has Editor, Details,
Progress, Sources and Secondary actions tabs. Progress and Sources are deliberately disabled with
localized explanations until their functionality is implemented. The text view is an A4 print layout
with a readable white page and an automatic fit to the available width.

Italian and English resources cover the shell, dialogs, validation, accessible names, editor
commands and its context menu. Language selection is saved locally; the automatic option follows the
operating-system language. Changing the interface language never translates note titles or text.

### Observed walkthrough

- The actual packaged Chromium/Tiptap window opened offline. No server or token was configured.
- A synthetic space was created through the interface. A synthetic note was prepared through the
  production local store, opened through the interface, edited with real keyboard input and saved.
- Switching Italian to English and back retained the open title and appended text.
- Closing and reopening recovered the edited note and the Italian preference. A second restart
  recovered the English preference without a process-level language override.
- The main editor remained usable at 1320 by 860, 1280 by 800 and 1024 by 768 window sizes. Details
  and Secondary actions were inspected at laptop dimensions; essential controls did not overlap.
  Long titles remain available through the note title and accessible item name.
- A 32-paragraph synthetic document displayed three A4 sheets. Both page gaps and the final
  paragraph were inspected: text continued without appearing in the inter-page gaps. The new startup
  bounds also have tests for 1280 by 680 and 1024 by 650 logical work areas; these tests do not
  replace real mixed-DPI checks.
- The Italian editor context menu used localized commands. Mutating commands were disabled in view
  mode. Native accessibility inspection exposed note titles instead of raw document objects.
- The packaged-editor runtime executable independently exercised readiness, a real editor change,
  durable autosave, native close and reopening the SQLite archive. It also checked empty-state
  actions and accessible list-item names. It is invoked explicitly, rather than counted as xUnit.

Findings from this walkthrough were corrected in the draft: missing bundle validation, a read-only
localization binding crash, disposed native frame access, overlong accessible item names, and
misleading empty states. Security review separately requires bounded bridge messages and a
fail-closed local bundle handler; consult PROJECT_STATUS.md for their latest validation status.

### Accessibility evidence and limits

The shared theme uses visible keyboard focus, text labels for icon actions, selection markers in
addition to color and larger scrollbar hit areas. Measured contrast for primary/secondary text on
the native panel backgrounds exceeded 7:1. Interactive borders exceeded 3:1; separators are not
treated as interactive boundaries. These are measured subsets, not a conformance certification.

Engineering targets are [WCAG 2.2 AA](https://www.w3.org/TR/WCAG22/) and the applicable software
requirements in EN 301 549, assessed with the
[AgID accessibility guidance](https://www.agid.gov.it/it/design-servizi/accessibilita.html).
Applicability depends on the distribution and use; a personal desktop application must not be
advertised as legally certified from automated tests.

Remaining manual validation includes Windows scaling at 125/150/200 percent, a complete Narrator
editing walkthrough, high-contrast mode, native file dialogs, extended keyboard navigation and
multi-page print/export fidelity for complex tables and mixed media. Different window sizes do not
establish high-DPI compatibility. The note-creation prompt was inspected but its complete
typing/submission path was not qualified by the automated UI operator; store/runtime tests do not
replace that manual check.

## Italiano

### Grafica realizzata

La finestra ha una barra superiore compatta, l'icona del libro, il selettore della lingua, una barra
per spazio/server e una colonna separata per gli appunti. Il contenuto è diviso nelle schede Editor,
Dettagli, Avanzamento, Fonti e Azioni secondarie. Avanzamento e Fonti restano disabilitate con una
spiegazione localizzata fino alla realizzazione delle rispettive funzioni. Il testo usa il layout di
stampa A4, con foglio bianco e adattamento alla larghezza disponibile.

Le risorse italiano/inglese coprono finestra, dialoghi, validazioni, nomi accessibili, comandi e
menu dell'editor. La lingua viene conservata localmente; l'opzione automatica segue il sistema
operativo. Titoli e contenuti degli appunti non vengono tradotti.

### Collaudo osservato

- Avvio offline della finestra Chromium/Tiptap realmente distribuita, senza server o token.
- Creazione di uno spazio tramite interfaccia; preparazione di un appunto sintetico mediante
  l'archivio applicativo, apertura, modifica da tastiera e salvataggio tramite interfaccia.
- Passaggio italiano/inglese/italiano con conservazione di titolo e testo aggiunto.
- Recupero della bozza e della preferenza italiana dopo la chiusura; secondo riavvio con recupero
  dell'inglese senza una variabile di processo che forzasse la lingua.
- Verifica dell'editor a 1320 per 860, 1280 per 800 e 1024 per 768; controllo di Dettagli e Azioni
  secondarie su dimensioni da portatile, senza sovrapposizioni dei controlli essenziali.
- Documento sintetico di 32 paragrafi su tre fogli A4: controllati entrambi i passaggi di pagina e
  l'ultimo paragrafo, senza testo negli spazi fra i fogli. La logica di avvio copre aree utili
  logiche 1280 per 680 e 1024 per 650; non sostituisce prove reali a densità differenti.
- Menu italiano dell'editor, comandi di modifica disabilitati in visualizzazione e nomi accessibili
  limitati ai titoli degli appunti.
- Prova separata con Chromium reale per avvio, modifica, autosalvataggio durevole, chiusura e
  riapertura dell'archivio, azioni degli stati vuoti e nomi delle voci. L'eseguibile viene avviato
  esplicitamente e non viene conteggiato come test xUnit.

Il collaudo ha corretto errori nel pacchetto editor, nei binding delle traduzioni e nel collegamento
con i frame nativi, oltre a nomi accessibili troppo lunghi e stati vuoti fuorvianti. La revisione di
sicurezza verifica separatamente limiti dei messaggi e assenza di ripiego sulla rete: lo stato
aggiornato dei relativi controlli è in PROJECT_STATUS.md.

### Accessibilità: evidenze e limiti

Il tema offre focus visibile, nomi per le azioni a icona, marcatori di selezione oltre al colore e
aree più ampie per le barre di scorrimento. I contrasti misurati dei testi sui pannelli superano
7:1; i bordi interattivi superano 3:1. Sono verifiche parziali, non una certificazione di
conformità.

Gli obiettivi tecnici sono WCAG 2.2 AA e i requisiti software applicabili di EN 301 549, valutati
con le indicazioni AgID collegate sopra. Restano da verificare ridimensionamento Windows 125/150/200
percento, uso completo con Assistente vocale, contrasto elevato, dialoghi dei file, navigazione
estesa da tastiera e fedeltà di stampa/esportazione su più fogli. Le prove su diverse dimensioni
della finestra non dimostrano compatibilità completa con schermi ad alta densità. Il dialogo di
creazione appunto è stato osservato, ma il suo intero percorso di compilazione e invio non è stato
qualificato dall'operatore automatico: archivio e test runtime non sostituiscono quel controllo
manuale.
