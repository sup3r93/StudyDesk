# StudyDesk

[Italiano](README.it.md) · [Desktop](desktop/README.md) · [Server](server/README.md)

An offline-first Windows study workspace with an optional, self-hosted data and synchronization
server.

> **AI development disclosure:** This project is being developed primarily using AI, specifically
> OpenAI Codex, under the repository owner's direction. AI-generated code requires review and
> testing. See [AI_DISCLOSURE.md](AI_DISCLOSURE.md).

## Project status

**Repository foundation and design stage.** The application, installer and synchronization protocol
are not implemented or released yet. The features below are agreed requirements, not a list of
working capabilities. No existing service has been migrated or removed.

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

Application code will use **C#, TypeScript and, where useful, Python**. Source comments,
identifiers, interface text and technical documentation use English. The main README is maintained
in both English and Italian. Only free and open-source application libraries are accepted.

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

## Check repository formatting

Use Node.js 24 LTS for repository tooling:

```text
npm ci --ignore-scripts
npm run check
```

These commands currently check repository formatting. Application build, analyzer and behavioral
tests will be added with their projects; formatting checks do not establish application correctness.
See [coding standards](docs/CODING_STANDARDS.md).

## Migration

StudyDesk is intended to replace Studiario after notes, files, versions and personal data have been
transferred and verified. A tested recovery path is required before the old application is retired.
Existing VPS services remain unchanged during repository preparation.

## License

[MIT](LICENSE). Third-party components retain their own licenses and attribution requirements. This
repository must never contain personal notes, private server configuration, real tokens or
clinical/patient records.
