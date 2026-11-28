# StudyDesk synchronization protocol

Status: first implementation milestone. This document describes the current single-owner,
multiple-device contract. Scoped sharing, attachments, CRDT text merging, and realtime collaboration
are not implemented by this milestone.

## Authority and local durability

The Windows client saves an edit and its outgoing operation locally before reporting it as saved.
The owner's server assigns entity versions and a monotonic workspace cursor when it accepts
operations. An HTTP response alone does not replace the client's durable local receipt processing.

Each client/server association has its own device identity and secret. The server authenticates
protected endpoints and authorizes operations. The installation has a stable `ServerId` and a
`Generation` identifying its current synchronization history. A client must preserve local drafts
when that generation no longer matches.

## HTTP endpoints

| Endpoint                                      | Access               | Purpose                                                      |
| --------------------------------------------- | -------------------- | ------------------------------------------------------------ |
| `GET /v1/server`                              | Public               | Minimal server and protocol information.                     |
| `GET /v1/access`                              | Owner authentication | Small authenticated server identity response.                |
| `GET /v1/snapshot/chunks?offset=0[&cursor=N]` | Owner authentication | Read a bounded snapshot, one chunk at a time.                |
| `POST /v1/sync/push`                          | Owner authentication | Submit up to 100 operations and receive individual receipts. |
| `GET /v1/devices`                             | Owner authentication | List device associations.                                    |
| `POST /v1/devices/{id}/revoke`                | Owner authentication | Revoke a device association.                                 |

The push endpoint limits the HTTP request to 16 MiB and page content to 2 MiB. These are limits, not
recommended batch sizes. Development HTTP exposure requires an explicit flag and is restricted to
loopback IP addresses. Production access requires validated TLS.

The former `GET /v1/snapshot` route is retired: authorized owners receive a small HTTP `410`
response directing them to the chunk endpoint. It must never materialize the complete snapshot.
Anonymous and non-owner calls still fail authentication or authorization.

## Snapshot chunks

A chunk contains `ServerId`, `Generation`, `Cursor`, `NextOffset` (nullable), `Spaces`, and `Pages`.
The server bounds each chunk to at most 64 spaces and one page. A client follows `NextOffset` until
it is null.

Before applying a downloaded snapshot, the client must verify that **every** chunk has the same
`ServerId`, `Generation`, and `Cursor`. It then applies the complete snapshot atomically. If the
server cursor changes during the download, HTTP `409` requires a restart of the snapshot download;
the client must not acknowledge or replace its local state with the partial result. Local drafts and
outgoing operations remain recoverable.

Large-workspace tests exercise approximately 38 MiB in the store and a 34,007,225-byte HTTP
snapshot. The largest HTTP chunk in that scenario was 2,000,628 bytes. These checks do not establish
unlimited capacity: the desktop caps each response at 32 MiB and an assembled snapshot at 256 MiB
and 100,000 entities.

## Push operations and receipts

An operation has an `OperationId`, kind, entity ID, expected entity version, and payload. The server
identifies a prior submission by `(DeviceId, OperationId)` and compares its payload digest.

| Receipt status | Meaning on first submission                   | Identical retry                                         |
| -------------- | --------------------------------------------- | ------------------------------------------------------- |
| `accepted`     | The operation was committed.                  | `duplicate`, referring to the original accepted result. |
| `conflict`     | Expected version did not match current state. | The original `conflict` receipt.                        |
| `invalid`      | The request or operation was invalid.         | The original `invalid` receipt.                         |
| `forbidden`    | Reserved for a scoped authorization failure.  | Preserve the original failure if issued.                |

This owner-only milestone rejects unauthorized requests with HTTP 401/403 before applying
operations; it does not currently emit per-operation `forbidden` receipts.

`duplicate` is a positive confirmation **only** when the original receipt was `accepted`. Reusing an
operation ID with different content returns `invalid`; it cannot change or replace the stored
receipt. The same operation ID may be used independently by different devices.

A generation mismatch returns HTTP `409`. It is not an acknowledgment of queued operations: the
client keeps its drafts and reconciles against the current server generation.

## Immutable outgoing operations and issue resolution

`GetPendingOperationsAsync` freezes selected outgoing operations for transmission. A later local
edit creates a separate outbox operation; it must not rewrite the ID, payload, or expected version
of an operation already selected for sending. Retries transmit the same operation content.

A failed operation remains a durable sync issue requiring an explicit decision. It must not silently
become `accepted`, and an issue affecting one entity does not prevent independent entities from
progressing.

`ResolveSyncIssueAsync` records an explicit choice within a local transaction. A new operation uses
the version of the current local server shadow as its expected server version. The server's
conditional update rejects an intervening remote edit rather than silently overwriting it. Choosing
`UseServer` retains the rejected local work through the recovery path rather than discarding it
without a recoverable copy. Resolution creates new work with a new operation ID when another server
submission is needed; it never alters the stored receipt of the failed operation.

## What convergence does not guarantee

The current milestone synchronizes versioned entities through the server. It does not provide
simultaneous CRDT editing or realtime collaboration. Future text convergence must remain separate
from authorization, durable receipts, deletion rules, and draft recovery. A merged text state can
still require human review.

## Italiano — riepilogo operativo

Il client salva prima in locale. `accepted` conferma una modifica registrata dal server; `duplicate`
è una conferma positiva solo se la prima ricevuta era `accepted`. I retry di `conflict`, `invalid` e
`forbidden` conservano il loro esito iniziale. La chiave della ricevuta comprende dispositivo e ID
dell'operazione; riusare lo stesso ID con contenuto diverso è `invalid`.

Le operazioni selezionate per l'invio restano immutabili. Le modifiche successive entrano in una
nuova voce della coda. Un errore di sincronizzazione rimane visibile e durevole finché l'utente non
sceglie come risolverlo; nel frattempo le altre entità possono continuare a sincronizzarsi. In caso
di `409` per generation o cursor, il client conserva bozze e operazioni locali e riparte dal
recupero dello stato server, senza applicare snapshot parziali.
