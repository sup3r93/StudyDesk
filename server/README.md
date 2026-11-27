# StudyDesk Server

This directory is reserved for the self-hosted data and synchronization service and its tests. No
runnable server has been implemented or deployed yet.

## Responsibilities

- Store notes and attachments received from authorized clients.
- Pair devices using runtime-generated tokens and enforce access to each space.
- Coordinate durable synchronization and real-time document collaboration.
- Support resumable file transfers, integrity checks, trash and version retention.
- Expose revocation and recovery controls without requiring Keycloak or account login.

The intended deployment is an owner's VPS or main PC. Each installation configures its own server;
no personal server endpoint is embedded in the distributed application. Clients accessing an invited
space connect to that space owner's server only.

The candidate stack uses C#/.NET for the data service and, if adopted, a TypeScript service based on
Yjs/Hocuspocus for collaborative text. The protocol, persistence model and packaging still require
implementation and tests.

No Docker, NuGet or server runtime manifest is present yet. Dependabot entries for those ecosystems
must be added when their actual manifests are introduced.

See [architecture](../docs/ARCHITECTURE.md), [security policy](../SECURITY.md) and
[coding standards](../docs/CODING_STANDARDS.md).
