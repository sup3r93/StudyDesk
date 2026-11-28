# StudyDesk

[Italiano](README.it.md) · [Desktop](desktop/README.md) · [Server](server/README.md)

An offline-first Windows study workspace with an optional, self-hosted data and synchronization
server.

> **AI development disclosure:** This project is being developed primarily using AI, specifically
> OpenAI Codex, under the repository owner's direction. AI-generated code requires review and
> testing. See [AI_DISCLOSURE.md](AI_DISCLOSURE.md).

## Project status

**Executable foundation under local validation; no application release yet.** The working draft
contains a WPF/CEF Windows host, bundled Tiptap editor, SQLite stores, a token-protected ASP.NET
Core server and behavioral tests. The offline editing, bilingual interface and restart walkthrough
has passed; final security and packaging checks are still being completed. Source availability is
not a claim that every planned feature works.

See [the current integration status](PROJECT_STATUS.md), the [desktop guide](desktop/README.md) and
the [server guide](server/README.md) for implemented behavior and limitations. Sharing permissions,
real-time collaboration, attachments, Windows Hello, complete exports and Studiario migration remain
later milestones. Existing services have not been migrated or removed.

## Intended experience

- Start writing offline immediately, without registration, an account or an Internet connection.
- Keep the same notes on a desktop PC and laptop through your own data server.
- Configure your own VPS or main PC; this project does not provide a shared hosted service.
- Share private spaces with invited devices as readers or editors. Shared spaces stay on their
  owner's server.
- Support simultaneous editing online and reconciliation after offline work.
- Download attachments selectively, or choose **Download entire space**.
- Offer optional Windows Hello protection per device, with a recovery path.
- Keep deleted pages in the trash for 30 days and previous versions for 90 days, with configurable
  retention.
- Export to PDF, Word/DOCX and other documented formats while retaining a complete native format.

## Repository layout

```text
desktop/    Windows application, local persistence, editor and desktop tests
server/     Self-hosted data service, synchronization, device access and server tests
docs/       Architecture, requirements, roadmap and coding standards
.github/    Automated checks, Dependabot, templates and ruleset definitions
```

Application code uses **C#, TypeScript and, where useful, Python**. Source comments, identifiers and
technical documentation use English. The interface supports **Italian and English**, with a
persisted local language selector; user-written notes are never translated automatically. The main
README is maintained in both languages. Only free and open-source application libraries are
accepted.

## Device access and secrets

The intended access model uses a separate, cryptographically random **64-character hexadecimal token
per client/server association**. Tokens are created during setup, never hard-coded, committed,
included in installers or written to logs. The server keeps a verifier rather than the original
token; the Windows client stores its token using protected local storage.

The client verifies the server's HTTPS certificate; the server checks the client's token and
permissions. A token proves possession of a secret, not the physical identity of a computer. The
design does not require Keycloak, browser login, usernames or passwords. Local Windows Hello remains
optional.

See [architecture and accepted decisions](docs/ARCHITECTURE.md) and
[security reporting](SECURITY.md).

## Development workflow

- `main`: stable, reviewed repository state.
- `dev`: integration branch for development.
- `feature/*`, `fix/*`, `docs/*`: short-lived branches, normally targeting `dev`.
- Promote tested changes from `dev` to `main` through a pull request.

Both long-lived branches are intended to require pull requests, successful checks and resolved
review discussions, and to reject force pushes and deletion. A second reviewer's approval is not
required for this individual-maintainer project. Ruleset definitions live in `.github/rulesets/`;
the [governance guide](docs/REPOSITORY_GOVERNANCE.md) distinguishes tracked configuration from
GitHub settings.

## Build and verification

Use Node.js 24 LTS and the .NET 10 SDK. Build the local editor before the Windows host:

```text
npm ci --ignore-scripts
npm run check
npm --prefix desktop/editor ci --ignore-scripts
npm --prefix desktop/editor run build
dotnet test server/StudyDesk.Server.slnx
dotnet test desktop/StudyDesk.Desktop.slnx
dotnet run --project desktop/tests/StudyDesk.Desktop.Runtime.Tests/StudyDesk.Desktop.Runtime.Tests.csproj
```

The desktop solution requires Windows x64. The server is designed for Windows and Linux; local
Windows tests do not establish Linux runtime behavior. The application CI definition includes Linux
server checks and Windows desktop checks, but it must pass on GitHub before being reported as
verified there. The real packaged-editor smoke executable is run separately from the unit tests. See
the component guides for private configuration and launch commands and the
[interface verification report](docs/DESKTOP_UI_VERIFICATION.md) for measured results and limits.

Automated checks complement a real offline editing/restart walkthrough and security review. See
[coding standards](docs/CODING_STANDARDS.md).

## Migration

StudyDesk is intended to replace Studiario after notes, files, versions and personal data have been
transferred and verified. A tested recovery path is required before the old application is retired.
Existing VPS services remain unchanged during repository preparation.

## License

[MIT](LICENSE). Third-party components retain their own licenses and attribution requirements. This
repository must never contain personal notes, private server configuration, real tokens or
clinical/patient records.
