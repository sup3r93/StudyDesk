# StudyDesk Desktop

This directory is reserved for the Windows application and its tests. No desktop application has
been implemented yet.

## Responsibilities

- Immediate offline use without registration or a network connection.
- Local notes, files, full-text search, autosave and crash recovery.
- A documented editor library, editable print layout and study-specific blocks.
- Server pairing, protected token storage and optional Windows Hello.
- A persistent synchronization queue, selective downloads and collaboration UI.
- PDF, DOCX and native-format export, with conversion limitations disclosed.

The current candidate stack is C#/.NET with WPF, a TypeScript editor based on Tiptap, and an
open-source Chromium integration such as CEF/CefSharp. These are design candidates, not installed or
validated dependencies.

Application projects and their tests belong here. Local databases, runtime configuration, tokens,
downloaded files and build outputs never belong in source control.

See [architecture](../docs/ARCHITECTURE.md), [roadmap](../docs/ROADMAP.md) and
[coding standards](../docs/CODING_STANDARDS.md).
