# StudyDesk Desktop

[Italiano](README.it.md)

StudyDesk is a Windows desktop application for personal, offline notes. This implementation was
written with AI assistance and is being validated incrementally; it is not a finished release.

## Interface and language

The desktop uses a compact dark shell, a space-and-note library, a white A4 writing surface and
separate tabs for the document and secondary actions. The caption's language selector switches the
native interface, dialogs, status messages, accessibility labels and editor between Italian and
English. The local choice is preserved; **System language** selects Italian for an Italian Windows
UI culture and English otherwise. User-written notes and titles are never translated.

The first window is centered and limited to the primary Windows work area in WPF units; its minimum
size also shrinks when necessary to fit a scaled laptop desktop. Native resizing and document
scrolling remain available. Mixed-DPI monitor transitions still require manual qualification.

The bundled CefSharp accessibility handler is experimental and read-only. Keyboard focus, labels and
native controls are implemented. Manual checks at 1320×860, 1280×800 and 1024×768 were completed in
both languages; see the [UI verification record](../docs/DESKTOP_UI_VERIFICATION.md). Screen-reader
editing, high-DPI behavior and native file dialogs remain release-validation work. No accessibility
compliance is claimed from automated tests alone.

## Implemented foundation

- Create local spaces and rich-text notes immediately, without registration or a server.
- Edit with the locally bundled, open-source Tiptap editor: basic formatting, headings, lists,
  tables, undo/redo and embedded PNG/JPEG images.
- Use A4 print layout as the editor presentation. Pagination is display-only and does not add page
  artifacts to the saved document.
- Save notes and pending synchronization operations durably in SQLite. Autosave acknowledges
  completion only after a local transaction. Closing requests a final editor snapshot first.
- Open existing notes in viewing mode, then explicitly enable editing.
- Use the Editor, Details and Secondary actions tabs. Progress and Sources are visibly disabled
  because those workflows have not been implemented in this milestone.
- Move notes to reversible trash, restore them, export structured note JSON or export PDF through
  the local Chromium print engine.
- Optionally pair a chosen data server and synchronize manually. A failed network request leaves
  local notes available. Generation and server identity checks stop a replaced-server sync.
- Open **Report an issue** in the system browser at the project's GitHub issue form. The application
  does not upload notes, logs, credentials or any other diagnostic data automatically.

## Build and run

This is a framework-dependent development build, not an installer or a self-contained release.
Building requires Windows x64, a .NET SDK capable of targeting .NET 10/C# 14, and Node.js/npm
compatible with the pinned editor tools. The first build downloads packages; the finished editor
bundle is local and normal offline use does not need Node.js or npm.

Running a copied build on another Windows x64 PC requires the matching major-version
[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), including WPF;
the plain console runtime alone is insufficient. CefSharp's .NETCore package also requires a
compatible x64 Visual C++ runtime, according to its
[redistribution documentation](https://github.com/cefsharp/CefSharp/wiki/Output-files-description-table-%28Redistribution%29).
Microsoft supplies the current compatible
[Visual C++ v14 Redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist/).
These prerequisites are not bundled or installed automatically by the current build. Windows 10/11
provides the optional `d3dcompiler_47.dll` component: StudyDesk deliberately excludes the app-local
copy from build, publish and test outputs, without disabling GPU rendering. Build/publish checks
reject stale output directories containing that DLL. The separate open-source `dxcompiler.dll`
remains included with the other required Chromium assets. Startup runs the same full upstream
dependency check and permits only that exact missing app-local compiler when the module loaded from
the absolute Windows system path has been verified. Its handle is retained until after Chromium
shutdown. The real renderer/autosave test passes on the current Windows test PC with no app-local
FXC; other Windows versions and clean-machine setup remain unqualified.

The complete output folder, native Chromium dependencies and all `ThirdParty/` notices must stay
together. Installation and prerequisite detection on a clean PC have not been qualified. No signed
installer, automatic setup, self-contained deployment or clean-machine compatibility is claimed.

From the repository root:

```powershell
npm --prefix desktop/editor ci --ignore-scripts --no-audit --no-fund
npm --prefix desktop/editor run build
dotnet build desktop/src/StudyDesk.Desktop/StudyDesk.Desktop.csproj
dotnet run --project desktop/src/StudyDesk.Desktop/StudyDesk.Desktop.csproj --no-build
```

The current executable is created under
`desktop/src/StudyDesk.Desktop/bin/Debug/net10.0-windows/win-x64/`. The build and publish output
include `editor/index.html`, its `editor/assets/` files and `ThirdParty/` license notices. Builds
and publishing fail with clear npm instructions when the bundle is absent or incomplete; a
regression test checks every copied script/stylesheet reference. Do not distribute only the
executable: the complete output directory is needed for Chromium.

For the focused native boundary tests:

```powershell
dotnet test desktop/tests/StudyDesk.Desktop.Ui.Tests/StudyDesk.Desktop.Ui.Tests.csproj
```

The separate [real Chromium runtime check](tests/StudyDesk.Desktop.Runtime.Tests/README.md) runs the
native window and packaged editor against synthetic data, covering IPC, autosave, close and
rereading. It is an executable command, so `dotnet test` alone does not run that smoke check.

## Private configuration and first offline start

On first launch StudyDesk creates `%LOCALAPPDATA%/StudyDesk/.env` with absolute paths for the local
workspace and protected association file. No username, password or token is required for local use.
Create a space, create a note and begin writing. A server can be connected later.

Configuration is read from that private `.env` file, with process environment variables taking
precedence. The supported variables are:

| Variable                                    | Purpose                                                                                       |
| ------------------------------------------- | --------------------------------------------------------------------------------------------- |
| `STUDYDESK_DESKTOP_ENV_FILE`                | Optional absolute path to a separate private `.env`, useful for isolated installations/tests. |
| `STUDYDESK_DESKTOP_DATA_DIR`                | Absolute directory containing `workspace.sqlite3` and local browser files.                    |
| `STUDYDESK_DESKTOP_TOKEN_FILE`              | Absolute path to the Windows-user-protected server association.                               |
| `STUDYDESK_DESKTOP_CONNECTION_FILE`         | Absolute path to an advisory, non-secret JSON copy of the server identity.                    |
| `STUDYDESK_DESKTOP_LANGUAGE`                | `auto`, `it` or `en`; the selector persists the chosen local preference.                      |
| `STUDYDESK_DESKTOP_ALLOW_INSECURE_LOOPBACK` | `false` by default; explicit development-only allowance for literal loopback HTTP addresses.  |

New private directories receive permissions for the current Windows user, SYSTEM and local
administrators. Existing directories granting sensitive access to unrelated users, including
inherit-only permissions that expose newly created files, are rejected. Existing environment,
database and association files are checked for foreign access grants and reparse points before use.
Choose a private local path, rather than a shared directory or filesystem junction. The local SQLite
notes are not encrypted by this milestone; the server credential is protected using Windows DPAPI
for the current Windows user.

## Optional data server

Choose **Connect a server**, enter the HTTPS server origin and its 64-character device token, then
confirm that this workspace should synchronize there. The dialog verifies server identity and a
lightweight authenticated access check before saving the association; pairing does not download the
workspace. The origin and token are protected together in a single atomically replaced file.
Advisory JSON metadata is never used to choose a credential destination. Credentials stay in native
C# transport and never enter the embedded editor.

Choose **Synchronize** to send queued operations in order and download a current snapshot. Snapshots
are assembled from bounded chunks with the same server identity, generation and cursor. A changed
history or interrupted download is never applied or used to acknowledge local operations. Each
response is limited to 32 MiB; this version supports at most 256 MiB of snapshot JSON and 100,000
entities per synchronization. Larger workspaces require a later incremental-download implementation;
local notes remain available when a limit is reached. This milestone uses server versions and
conditional updates; it does not yet provide CRDT collaboration or automatic conflict merging.
Conflicting local work is retained instead of silently overwritten. Secondary actions lists durable
issues and offers explicit retry or server-version choices. Choosing the server version writes a
local recovery JSON before replacing anything. Synchronization can be canceled without discarding
unacknowledged local operations. Pairing the first workspace does not create a shared central
account service.

Production connections require ordinary HTTPS certificate validation. Redirects and proxies are
disabled for the native synchronization transport. Development HTTP additionally requires both
`STUDYDESK_DESKTOP_ALLOW_INSECURE_LOOPBACK=true` and a literal loopback origin such as
`http://127.0.0.1:5091`; DNS names including `localhost` are not accepted for that exception.

## Current boundaries

The native editor accepts documents up to 1 MiB and embedded PNG/JPEG images up to 256 KiB each; the
same checks cover saved or synchronized documents before loading. Remote images, SVG and active
document embeds are not accepted in this foundation. Editor resources come exclusively from the
installed bundle and cannot fetch external network resources. Native factory registration must
succeed before the packaged address is loaded; local requests also disable the ordinary network
loader and always receive a file/error response. Bridge envelopes are limited to 2 MiB of UTF-8
before event creation and checked again by the subscriber; document limits remain stricter.

PDF and JSON are currently implemented. DOCX import/export, attachments/media tabs, custom print
settings, study blocks, clinical scales, search, shared spaces, Windows Hello, a signed installer
and the migration of existing Studiario data remain separate work. This milestone does not retire or
alter the existing Studiario deployment. SQLite revision history exists in the storage layer; a
user-facing recovery browser is not yet included. A local replica and synchronization are not
substitutes for independently verified backups.

## Open-source components

The host uses CefSharp 156.0.300 (BSD-style license), Chromium Embedded Framework 156.0.30
(BSD-style CEF license plus upstream Chromium notices) and Tiptap 3.35.1 (MIT), built on
ProseMirror. Redistributable notices from the installed packages are retained under
`src/StudyDesk.Desktop/ThirdParty/`; bundled editor production dependency licenses are included in
`Editor.NOTICES.txt`. No proprietary Tiptap extension or hosted service is required.

See the project [architecture](../docs/ARCHITECTURE.md), [roadmap](../docs/ROADMAP.md) and
[coding standards](../docs/CODING_STANDARDS.md) for the broader plan.
