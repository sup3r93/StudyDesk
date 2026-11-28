# StudyDesk Server

[Italiano](README.it.md)

StudyDesk Server is an optional, self-hosted synchronization service for the owner's Windows
installations. Notes remain usable offline in each desktop installation. This implementation was
written with AI assistance and is being validated incrementally; it is not a finished release.

## Implemented foundation

- C#/.NET 10 HTTP host with durable SQLite spaces, JSON pages, revisions and operation receipts.
- Independent runtime-generated 64-character device tokens; the server persists SHA-256 verifiers,
  rather than reusable bearer credentials.
- Local owner provisioning, authenticated snapshots and conditional, idempotent operation batches.
- Device listing and revocation. A revoked upload is checked again before anything is applied.
- Explicit listeners, private data directories, bounded uploads and one host per data directory.

There is no registration, central account, password login, email, SMTP or Keycloak dependency. Each
owner chooses their own server; this repository embeds no personal endpoint or credential.

## Build and configure

The server targets .NET 10 and requires its SDK to build, or its ASP.NET Core runtime to run a
framework-dependent published build. Current local verification was performed on Windows. Linux
deployment, production certificates and deployment packaging have not been exercised in this
milestone.

Run the following from the repository root:

```text
dotnet build server/src/StudyDesk.Server/StudyDesk.Server.csproj
```

Create a **server-only private environment file outside the repository**, using the relevant entries
from [`.env.example`](../.env.example). Set `STUDYDESK_SERVER_ENV_FILE` in the process environment
to that file's absolute path before running the commands below. The path is selected explicitly; the
server does not search the current directory for `.env` files. Already-set process variables take
precedence over file values.

| Variable                                              | Purpose                                                                   |
| ----------------------------------------------------- | ------------------------------------------------------------------------- |
| `STUDYDESK_SERVER_ENV_FILE`                           | Absolute path to the selected private environment file.                   |
| `STUDYDESK_SERVER_DATA_DIR`                           | Absolute private directory for SQLite data and the host lease.            |
| `STUDYDESK_SERVER_ORIGIN`                             | Client-facing HTTPS origin, with no credentials, path, query or fragment. |
| `STUDYDESK_SERVER_ALLOW_INSECURE_LOOPBACK`            | `false` by default; explicit local-development HTTP exception.            |
| `ASPNETCORE_URLS`                                     | Semicolon-separated listeners with explicit IP addresses or `localhost`.  |
| `ASPNETCORE_Kestrel__Certificates__Default__Path`     | Absolute private PFX certificate path for direct HTTPS.                   |
| `ASPNETCORE_Kestrel__Certificates__Default__Password` | PFX password supplied privately when required.                            |

HTTPS listener example: `https://127.0.0.1:7443`, replaced by the exact interface the owner intends
to expose. A public origin can use a DNS name, such as `https://sync.example.invalid`; listener
bindings cannot use DNS names. `0.0.0.0` and `::` are accepted only as an explicit choice to listen
on every interface. Additional `Kestrel:Endpoints` entries are rejected so they cannot widen the
configured listeners.

Direct HTTPS uses Kestrel's default certificate configuration. The certificate must cover the
client-facing hostname and have a chain trusted by the clients. Keep certificate material and its
password in private operating-system storage; do not put the password on a command line or in source
control. See
[Microsoft's .NET 10 Kestrel certificate documentation](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0#configure-https-in-appsettingsjson).

For a local HTTP development test only, choose a literal loopback origin/listener, such as
`http://127.0.0.1:5091`, and explicitly enable the loopback exception. The server also checks the
real local and remote socket addresses. A forged `Host` or forwarded header does not grant this
exception. Desktop HTTP development access requires its own explicit flag; see
[desktop configuration](../desktop/README.md).

New server data directories are created privately. Existing Windows grants outside the operator,
SYSTEM and local Administrators are rejected; existing Unix group/other permissions are rejected.
Symbolic links and junctions in the selected directory chain are rejected. Choose a dedicated local
directory, not a shared folder. Environment files and certificate files must also remain private.
Notes in SQLite are not encrypted by this milestone.

Existing `devices.sqlite3` and `workspace.sqlite3` files, together with each `-wal` and `-shm`
sidecar, are checked before host startup or CLI provisioning opens SQLite. Device-store opens and
authorized workspace operations recheck the applicable files. Broadened permissions, symbolic links
and reparse points are rejected without rewriting files or changing existing permissions. Only
missing databases are created privately; existing data requires the operator to correct its storage
policy explicitly.

## Associate devices and start the host

Provision each PC through a **trusted local command on the server**. From the repository root, with
the server environment selected:

```text
dotnet run --project server/src/StudyDesk.Server/StudyDesk.Server.csproj -- device create --name Desktop --output <ABSOLUTE_PRIVATE_PATH.studydesk-pairing.json>
```

Replace the output placeholder with a real absolute file path. The command creates a new private
JSON bundle containing `serverUrl`, `serverId`, `deviceId` and `token`. It refuses to overwrite an
existing file and never prints the token. Failed bundle delivery revokes the newly created
association. The suffix `.studydesk-pairing.json` is required and ignored by Git. Create a separate
bundle for the laptop rather than reusing the desktop association.

Transfer each bundle privately to its intended PC. In the current desktop's **Connect a server**
dialog, enter its server origin and token from the bundle. The desktop validates the server and
protects the association for the current Windows user. Bundle-file import is not yet implemented;
the server credential must never be pasted into a document or issue. Remove unnecessary plaintext
bundle copies after successful setup.

Start the host with the same environment:

```text
dotnet run --project server/src/StudyDesk.Server/StudyDesk.Server.csproj -- serve
```

The host can provision another device while it is running. Only one HTTP host can own a data
directory; an exclusive private lease remains open until host disposal. A second host using that
directory fails instead of creating an independent revocation boundary.

## API and limits

| Method and route                   | Access and result                                                         |
| ---------------------------------- | ------------------------------------------------------------------------- |
| `GET /v1/server`                   | Public cached server identity, generation and protocol version only.      |
| `GET /v1/access`                   | Owner token; cached identity only, for pairing without downloading notes. |
| `GET /v1/snapshot/chunks?offset=0` | Owner token; bounded snapshot chunks with cursor-checked continuation.    |
| `GET /v1/snapshot`                 | Owner token; HTTP 410 with short guidance to the bounded chunk route.     |
| `POST /v1/sync/push`               | Owner token; conditional operation receipts and cursor.                   |
| `GET /v1/devices`                  | Owner token; association metadata without tokens or verifiers.            |
| `POST /v1/devices/{id}/revoke`     | Owner token; idempotent revocation of an existing association.            |

Credentials belong in a single `Authorization: Bearer` header, never a URL or request body. Device
identity comes from that authenticated association. This first slice provisions **Owner only**: each
associated device can access the installation's entire workspace. Reader/editor sharing scopes and
invitations have not been implemented.

Uploads allow at most **100 operations**, **16 MiB of HTTP JSON** and **15 seconds to read the
body**. The decoded page JSON limit remains **2 MiB in UTF-8**. The larger transport allowance
covers JSON escaping; it does not increase the document limit. Oversized requests return HTTP 413,
expired body reads HTTP 408, malformed batches HTTP 400 and unsupported content types HTTP 415.
Upload reading occurs outside the revocation gate; authorization is checked again inside the gate
immediately before apply.

Successful push responses can still contain `conflict` or `invalid` receipts. Clients must inspect
each receipt rather than treating HTTP 200 as acceptance of every operation. Accepted retries return
`duplicate`. A generation mismatch returns HTTP 409 with the current generation: keep local drafts
and stop upload until the installation has been reviewed and a new snapshot obtained.

The default download protocol starts with `/v1/snapshot/chunks?offset=0`. Each response contains at
most 64 spaces and one page, the server identity, generation, cursor and nullable `nextOffset`.
Continue with `offset=nextOffset&cursor=<first-response-cursor>` until `nextOffset` is null. All
chunks must keep the same identity, generation and cursor; assemble and validate the complete
snapshot before applying it to a desktop store. If the archive changes during transfer, HTTP 409
contains only a generic restart message. Discard partial transfer data and restart the download
without replacing local notes. Invalid or missing continuation positions return HTTP 400. The full
snapshot endpoint no longer returns documents: an authorized owner receives a small HTTP 410
response pointing to `/v1/snapshot/chunks?offset=0`. Unauthenticated requests remain HTTP 401, and
non-owner roles are denied with HTTP 403. Pairing and synchronization use only the bounded routes.

## Verification and current boundaries

Focused tests, from the repository root:

```text
dotnet test server/tests/StudyDesk.Server.Tests/StudyDesk.Server.Tests.csproj
dotnet test server/tests/StudyDesk.Server.Api.Tests/StudyDesk.Server.Api.Tests.csproj
```

The API suite uses isolated private temporary data and real loopback Kestrel connections. It covers
authorization, verifier rejection, revocation during an already-started slow upload, an exclusive
host lease, local provisioning while serving, conditional writes, retries, generation mismatch,
private non-overwriting bundles, Windows directory permissions and upload limits. A Unicode
regression sends a valid 1,800,085-byte document using a request over 5.4 MB and verifies exact
content after reloading the snapshot. These tests do not certify production TLS or Linux deployment.

An end-to-end test uses two actual desktop SQLite stores and real HTTP: offline save, initial
upload, the other client's edit, conflict preservation across restart, explicit version-conditional
resolution and clean outbox/status after reopening. It downloads through the chunk protocol. A
separate large-archive regression reconstructs 17 pages whose full JSON exceeds 32 MiB while each
observed chunk is around 2 MB; the desktop response limit is not increased.

Additional security tests reject pre-existing Windows database/WAL/SHM files with broad grants and
verify unchanged bytes and ACLs. Symbolic-link rejection was exercised in the local Windows test
session, including preservation of the linked target. The retired full route is tested as a bounded
HTTP 410 response for owners, with HTTP 401/403 for callers without the required access.

Real-time Yjs/CRDT collaboration, attachment transfer, shared-space scopes, invitations, automatic
backup/restore, history browsing and a packaged service installer remain separate work. SQLite
revision history exists, but there is no managed restore command in this milestone. Do not edit or
replace active server databases; an older database cannot be restored safely in place without a
coordinated generation/recovery workflow. Synchronization and a second replica do not replace
verified backups. This work does not change the existing Studiario deployment.

See [architecture](../docs/ARCHITECTURE.md), [security policy](../SECURITY.md) and
[coding standards](../docs/CODING_STANDARDS.md).
