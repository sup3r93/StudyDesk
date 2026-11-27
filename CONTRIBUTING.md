# Contributing to StudyDesk

StudyDesk is in its repository-foundation stage. Check [the roadmap](docs/ROADMAP.md) before
assuming a feature exists.

1. Branch from `dev`, using `feature/`, `fix/` or `docs/` and a descriptive English name.
2. Keep Windows code in `desktop/` and service code in `server/`.
3. Follow the [coding standards](docs/CODING_STANDARDS.md), including English documentation for
   classes and functions.
4. Update architecture/behavior documentation and both root READMEs when relevant.
5. Run formatting and the meaningful checks applicable to the change.
6. Open a focused pull request to `dev`; explain behavior, validation, risks and material AI
   assistance.

```text
npm ci --ignore-scripts
npm run format
npm run check
```

Use Node.js 24 LTS for these repository tools. These commands currently verify formatting;
application tests are added with real projects.

Never commit notes, databases, personal deployment details, runtime tokens, private keys, recovery
material or screenshots containing secrets. Generate test secrets at runtime. Run a Gitleaks history
scan before publishing work. Do not attach sensitive data to an issue or pull request.

Use only free and open-source application libraries. Record upstream licenses and attribution
requirements. Code generation does not remove third-party licensing obligations. See
[the AI disclosure](AI_DISCLOSURE.md).

Behavioral changes involving synchronization, permissions, deletion, key storage or recovery require
focused failure-mode tests and an explanation of data preservation. Do not deploy to an existing
server as part of an unrelated repository change.
