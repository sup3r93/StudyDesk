import { Editor } from '@tiptap/core';
import type { JSONContent } from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';
import { TableKit } from '@tiptap/extension-table';
import Image from '@tiptap/extension-image';
import './style.css';

interface EditorState {
  pageId: string;
  sessionId: string;
  sequence: number;
  content: JSONContent;
}

interface NativeDocument {
  pageId: string;
  sessionId: string;
  contentJson: string;
}

declare global {
  interface Window {
    CefSharp?: { PostMessage(message: string): void };
    studyDesk: {
      connect(challenge: string): boolean;
      clear(): void;
      setLanguage(language: string, dictionary: Readonly<Record<string, string>>): void;
      load(document: NativeDocument): void;
      snapshot(): string | null;
      insertImage(source: string): void;
      setReadOnly(value: boolean): void;
    };
  }
}

let current: Omit<EditorState, 'content'> | null = null;
let nativeChannel: string | null = null;
let measuring = false;
let paperZoom = 1;
let translations: Readonly<Record<string, string>> = {};
let language = 'en';
const stage = required<HTMLElement>('paper-stage');
const sheets = required<HTMLElement>('sheets');
const notice = required<HTMLElement>('notice');
const paperStatus = required<HTMLElement>('paper-status');
const nativeColumns = CSS.supports('column-wrap', 'wrap') && CSS.supports('column-height', '100px');

/** Resolve embedded UI text without changing any user-authored document content. */
function translate(key: string, ...arguments_: unknown[]): string {
  const template = translations[key] ?? `[${key}]`;
  return template.replace(/\{(\d+)\}/g, (_match, index: string) =>
    String(arguments_[Number(index)] ?? ''),
  );
}

/** Fit the physical sheet to the available viewport while keeping print dimensions and saved content intact. */
function fitPaper(): void {
  const viewport = required<HTMLElement>('paper-scroll');
  paperZoom = Math.max(0.5, Math.min(1, (viewport.clientWidth - 48) / (210 * (96 / 25.4))));
  stage.style.zoom = String(paperZoom);
  measurePages();
}

/** Start a newly loaded or cleared note at its beginning without affecting edits, snapshots or UI language. */
function resetDocumentScroll(): void {
  const viewport = required<HTMLElement>('paper-scroll');
  viewport.scrollTop = 0;
  viewport.scrollLeft = 0;
}

/** Resolve required packaged elements while failing clearly on an invalid editor bundle. */
function required<T extends HTMLElement>(id: string): T {
  const element = document.getElementById(id);
  if (!element) throw new Error('The packaged editor layout is incomplete.');
  return element as T;
}

/** Send a narrowly typed document event; the renderer never receives a server credential. */
function notifyNative(type: string, state?: EditorState): boolean {
  if (!nativeChannel || typeof window.CefSharp?.PostMessage !== 'function') return false;
  window.CefSharp.PostMessage(JSON.stringify({ type, channel: nativeChannel, ...state }));
  return true;
}

/** Return the current document for an explicit native flush before switching or closing. */
function snapshot(): string | null {
  return current
    ? JSON.stringify({ ...current, channel: nativeChannel, content: editor.getJSON() })
    : null;
}

/** Check loaded content before it can initiate image requests or replace the current note. */
function validateDocument(content: JSONContent): void {
  const serialized = JSON.stringify(content);
  if (content.type !== 'doc' || new TextEncoder().encode(serialized).length > 1_048_576) {
    throw new Error(translate('editor.error.documentFormat'));
  }
  /** Inspect only schema nodes; inline images must be inert bounded raster data. */
  function visit(node: JSONContent, depth: number): void {
    if (depth > 48) throw new Error(translate('editor.error.documentDepth'));
    if (node.type === 'image') {
      const source: unknown = node.attrs?.src;
      if (typeof source !== 'string' || !isAllowedImage(source)) {
        throw new Error(translate('editor.error.imageUnsupported'));
      }
    }
    for (const child of node.content ?? []) visit(child, depth + 1);
  }
  visit(content, 0);
}

/** Match the native PNG/JPEG restrictions without allowing SVG, remote images or oversized data. */
function isAllowedImage(source: string): boolean {
  if (source.length > 350_000 || !/^data:image\/(png|jpeg);base64,[A-Za-z0-9+/=]+$/.test(source))
    return false;
  try {
    const bytes = atob(source.slice(source.indexOf(',') + 1));
    if (bytes.length < 8 || bytes.length > 262_144) return false;
    return source.startsWith('data:image/png;')
      ? [137, 80, 78, 71, 13, 10, 26, 10].every((value, index) => bytes.charCodeAt(index) === value)
      : bytes.charCodeAt(0) === 255 && bytes.charCodeAt(1) === 216 && bytes.charCodeAt(2) === 255;
  } catch {
    return false;
  }
}

/** Repaint sheet backgrounds without inserting pagination artifacts into saved content. */
function measurePages(): void {
  if (measuring) return;
  measuring = true;
  requestAnimationFrame(() => {
    measuring = false;
    const body = editor.view.dom;
    const step = 309 * (96 / 25.4);
    const bodyHeight = 257 * (96 / 25.4);
    const gap = 52 * (96 / 25.4);
    const count = Math.max(
      1,
      Math.ceil(
        (Math.max(bodyHeight, body.getBoundingClientRect().height / paperZoom) + gap - 1) / step,
      ),
    );
    if (count > 200) {
      notice.textContent = translate('editor.layoutLimit');
      notice.hidden = false;
    }
    sheets.replaceChildren(
      ...Array.from({ length: Math.min(200, count) }, (_, index) => {
        const sheet = document.createElement('div');
        sheet.className = 'sheet';
        sheet.style.top = `${index * 309}mm`;
        return sheet;
      }),
    );
    stage.style.minHeight = `${Math.min(200, count) * 297 + Math.max(0, Math.min(200, count) - 1) * 12}mm`;
    paperStatus.textContent = `${translate(count === 1 ? 'editor.layoutSingle' : 'editor.layoutPlural', count)} · ${translate('editor.zoom', Math.round(paperZoom * 100))}`;
  });
}

const editor = new Editor({
  element: required('editor'),
  extensions: [
    StarterKit.configure({ link: { openOnClick: false }, trailingNode: false }),
    TableKit,
    Image.configure({ allowBase64: true }),
  ],
  editable: false,
  content: { type: 'doc', content: [{ type: 'paragraph' }] },
  editorProps: {
    attributes: {
      'aria-label': '',
      role: 'textbox',
      'aria-multiline': 'true',
      spellcheck: 'true',
    },
    handleDOMEvents: {
      /** Keep external links and drag navigation outside the native bridge origin. */
      drop(_view, event): boolean {
        if (event.dataTransfer?.files.length || event.dataTransfer?.getData('text/uri-list')) {
          event.preventDefault();
          return true;
        }
        return false;
      },
    },
  },
  /** Mark changes as unsaved until the native host commits its local transaction. */
  onUpdate: () => {
    if (!current) return;
    current.sequence += 1;
    notifyNative('documentChanged', { ...current, content: editor.getJSON() });
    measurePages();
  },
});

type Tool = { labelKey: string; action: () => void; active?: () => boolean };
const tools: Tool[] = [
  {
    labelKey: 'editor.bold',
    action: () => {
      editor.chain().focus().toggleBold().run();
    },
    active: () => editor.isActive('bold'),
  },
  {
    labelKey: 'editor.italic',
    action: () => {
      editor.chain().focus().toggleItalic().run();
    },
    active: () => editor.isActive('italic'),
  },
  {
    labelKey: 'editor.underline',
    action: () => {
      editor.chain().focus().toggleUnderline().run();
    },
    active: () => editor.isActive('underline'),
  },
  {
    labelKey: 'editor.heading',
    action: () => {
      editor.chain().focus().toggleHeading({ level: 2 }).run();
    },
    active: () => editor.isActive('heading'),
  },
  {
    labelKey: 'editor.bullets',
    action: () => {
      editor.chain().focus().toggleBulletList().run();
    },
    active: () => editor.isActive('bulletList'),
  },
  {
    labelKey: 'editor.numbering',
    action: () => {
      editor.chain().focus().toggleOrderedList().run();
    },
    active: () => editor.isActive('orderedList'),
  },
  {
    labelKey: 'editor.table',
    action: () => {
      editor.chain().focus().insertTable({ rows: 3, cols: 3, withHeaderRow: true }).run();
    },
  },
  {
    labelKey: 'editor.undo',
    action: () => {
      editor.chain().focus().undo().run();
    },
  },
  {
    labelKey: 'editor.redo',
    action: () => {
      editor.chain().focus().redo().run();
    },
  },
];

/** Reflect selection formatting using standard accessible toolbar buttons. */
function updateToolbar(): void {
  document.querySelectorAll<HTMLButtonElement>('#toolbar button').forEach((button, index) => {
    button.disabled = !editor.isEditable;
    const tool = tools[index];
    if (tool) {
      const label = translate(tool.labelKey);
      button.title = label;
      button.setAttribute('aria-label', label);
    }
    if (tool?.active) button.setAttribute('aria-pressed', String(tool.active()));
  });
}

const icons: Readonly<Record<string, string>> = {
  'editor.bold': 'B',
  'editor.italic': 'I',
  'editor.underline': 'U',
  'editor.heading': 'H₂',
  'editor.bullets': '•≡',
  'editor.numbering': '1.',
  'editor.table': '▦',
  'editor.undo': '↶',
  'editor.redo': '↷',
};

for (const tool of tools) {
  const button = document.createElement('button');
  button.type = 'button';
  button.textContent = icons[tool.labelKey] ?? '';
  button.dataset.tool = tool.labelKey;
  button.setAttribute('aria-label', translate(tool.labelKey));
  button.addEventListener('click', tool.action);
  required('toolbar').append(button);
}

editor.on('selectionUpdate', updateToolbar);

window.studyDesk = {
  /** Acknowledge a repeated native challenge only after the real post-message API is available. */
  connect(challenge: string): boolean {
    if (!/^[a-f0-9]{32}$/i.test(challenge)) return false;
    nativeChannel = challenge;
    return notifyNative('editorReady');
  },
  /** Clear the previous selection after native code has saved it, without exposing hidden note content. */
  clear(): void {
    current = null;
    editor.commands.setContent(
      { type: 'doc', content: [{ type: 'paragraph' }] },
      { emitUpdate: false },
    );
    editor.setEditable(false);
    notice.hidden = true;
    updateToolbar();
    measurePages();
    resetDocumentScroll();
  },
  /** Refresh only packaged UI strings and accessibility names; the editor document is not rewritten. */
  setLanguage(selected: string, dictionary: Readonly<Record<string, string>>): void {
    if (selected !== 'it' && selected !== 'en') throw new Error('Unsupported editor UI language.');
    language = selected;
    translations = { ...dictionary };
    document.documentElement.lang = language;
    document.title = translate('editor.title');
    required('toolbar').setAttribute('aria-label', translate('editor.toolbarLabel'));
    required('paper-scroll').setAttribute('aria-label', translate('editor.documentSheets'));
    editor.view.dom.setAttribute('aria-label', translate('editor.label'));
    if (!current) {
      notice.textContent = translate('editor.selectNote');
      notice.hidden = true;
    } else if (!nativeColumns) {
      notice.textContent = translate('editor.layoutUnsupported');
      notice.hidden = false;
    }
    updateToolbar();
    fitPaper();
  },
  /** Load one native-selected note; pagination remains a presentation-only concern. */
  load(document: NativeDocument): void {
    const parsed: JSONContent = JSON.parse(document.contentJson);
    validateDocument(parsed);
    current = { pageId: document.pageId, sessionId: document.sessionId, sequence: 0 };
    editor.commands.setContent(parsed, { emitUpdate: false, errorOnInvalidContent: true });
    editor.setEditable(false);
    updateToolbar();
    notice.textContent = nativeColumns ? '' : translate('editor.layoutUnsupported');
    notice.hidden = nativeColumns;
    measurePages();
    resetDocumentScroll();
  },
  snapshot,
  /** Insert only a bounded image selected and validated by the native file picker. */
  insertImage(source: string): void {
    if (current && editor.isEditable && isAllowedImage(source)) {
      editor.chain().focus().setImage({ src: source }).run();
    }
  },
  /** Disable editing while a native save or shutdown operation requires a stable snapshot. */
  setReadOnly(value: boolean): void {
    editor.setEditable(!value && current !== null && nativeColumns);
    updateToolbar();
  },
};

updateToolbar();

new ResizeObserver(measurePages).observe(editor.view.dom);
new ResizeObserver(fitPaper).observe(required('paper-scroll'));
document.addEventListener('click', (event) => {
  if ((event.target as Element | null)?.closest('a')) event.preventDefault();
});
measurePages();
