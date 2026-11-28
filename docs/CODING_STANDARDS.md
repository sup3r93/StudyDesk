# Coding standards

## Language, structure and documentation

- Write application code only in C#, TypeScript or Python. XAML, CSS and declarative configuration
  support those projects.
- Keep Windows-specific code in `desktop/` and data-service code in `server/`.
- Write identifiers, comments, internal diagnostics and technical documentation in English. Keep
  user-facing text in Italian and English resource catalogs, with matching keys and format
  arguments. Maintain `README.md` and `README.it.md` together.
- Document every class and method/function with its purpose, inputs, output and meaningful failure
  behavior. Use XML documentation for C#, TSDoc/JSDoc notation in TypeScript and docstrings in
  Python.
- Explain non-obvious algorithms, invariants, security boundaries and recovery decisions inline.
  Comments must stay accurate when code changes; do not replace explanations with a comment on every
  obvious assignment.
- Keep functions focused, names descriptive and data contracts explicit. Avoid hidden side effects
  and duplicated business rules.

## Formatting and analysis

- Follow `.editorconfig`; use LF, UTF-8, final newlines and no trailing whitespace.
- Run the pinned Prettier formatter for supported repository files.
- When C# projects are added, enable nullable reference types, analyzers, deterministic builds, XML
  documentation checks and formatting verification in CI. Handle warnings deliberately rather than
  suppressing them globally.
- When TypeScript projects are added, enable strict checking and linting, and document exported and
  internal functions. Python code must use type hints, a formatter and static checks where
  practical.
- Add real manifests, lock files and matching Dependabot ecosystems together. Do not create checks
  that silently succeed without analyzing the expected projects.

## Reliability and security

- Generate tokens at runtime with cryptographically secure randomness. Protect storage, redact logs
  and check authorization at every boundary.
- Use TLS validation and established cryptographic libraries. Never accept all certificates or
  invent a token exchange protocol.
- Validate external input and imported document structures. Restrict file access and native editor
  bridges to narrowly defined operations.
- Save transactionally; support cancellation, bounded requests, retries, duplicate detection and
  recoverable errors.
- In C#, use asynchronous I/O and cancellation tokens; reserve `async void` for necessary UI event
  handlers.
- Treat CRDT text reconciliation, database transactions and file transfers as separate layers whose
  consistency must be tested.
- Do not log note contents, credentials, tokens or personal server configuration.

## Tests and honest reporting

Test behavior and failure modes: offline launch, crash recovery, simultaneous edits, revoked
devices, interrupted attachments, full disks, old clients and restore. Include unauthorized
read/write attempts and native-bridge boundaries. Avoid tests that merely restate implementation
details.

Report exactly which checks ran and what remains unverified. Automated accessibility checks do not
establish full accessibility compliance; automated tests do not establish clinical accuracy.
AI-generated code and tests require the same review standards as any contribution.
