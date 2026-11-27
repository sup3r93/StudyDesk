# Architecture and accepted decisions

Status: agreed requirements and proposed design; not implemented. Updated 2026-11-28.

## Product boundary

StudyDesk is a personal Windows application plus an optional self-hosted data server. A user can
work offline from the first launch. Each owner selects their own VPS or main PC. The public
repository distributes software, not access to the maintainer's server or data.

The repository has two application boundaries: `desktop/` and `server/`. C#, TypeScript and Python
are the permitted implementation languages. Use free and open-source libraries and preserve their
notices. English is used for code, comments, UI and technical documentation; maintain English and
Italian root READMEs. Disclose AI-assisted development.

## Local data and synchronization

The desktop saves locally before reporting success. A persistent queue survives crashes and
reconnection. Distinguish **Saved on this PC**, **Pending synchronization**, **Synchronized** and
**Error/conflict**. A server acknowledgment means the relevant state has been persisted, not just
received by another client.

Use stable identifiers, durable acknowledgments, duplicate-operation detection, integrity checks and
resumable attachments. Never synchronize a live SQLite database file as though it were an ordinary
document. Reconciliation must preserve recoverable work, including clients returning after the
retention period or a server restore.

Support real-time online editing and offline edits that merge later. Yjs/Hocuspocus are candidates
for text collaboration; application rules still govern titles, hierarchy, deletion, attachments and
permissions. Automatic text convergence does not guarantee a sensible merged result.

Text and metadata of selected spaces remain available offline. Attachments are downloaded
selectively, with a **Download entire space** command. A client has no local backup of an attachment
it has not downloaded.

## Spaces and permissions

- **Owner:** controls invitations, access changes and deletion of the entire space.
- **Editor:** creates, edits, moves and trashes pages within authorized destinations.
- **Reader:** reads permitted material; personal annotations remain private.

All spaces are private unless deliberately shared. An invited client connects to the owner's server
for that space; it does not automatically replicate the space to the recipient's separate server. An
offline copy cannot be remotely withdrawn immediately.

## Device association instead of account login

No Keycloak, username/password login, email registration or browser sign-in is required by the new
design. Optional Windows Hello protects local access separately from server pairing.

Generate a distinct 32-byte cryptographically random secret for every client/server association;
encode it as exactly 64 hexadecimal characters. Do not derive it from a device name, password or
timestamp. A single shared installation-wide token would prevent independent client revocation and
scoped sharing.

The client stores its secret in protected Windows storage, outside source and build output. The
server stores a verifier and association metadata: an opaque device identifier, display name,
allowed spaces, capabilities, creation/revocation information and, if configured, expiry. Validate
permissions on HTTP and collaboration requests.

The desktop verifies the server's HTTPS identity before sending a secret. The server verifies the
client token. This supplies both directions of verification without sending a global server secret
to every client. Any certificate pinning must support deliberate certificate rotation; never bypass
certificate validation.

Issue the first owner association through trusted local server administration. Pairing invitations
are deliberately issued, scoped and revocable. Do not let the first unauthenticated Internet request
become the owner. A copied bearer token can be used by whoever possesses it; it is not proof of a
physical device.

Never place real tokens in code, examples, commits, release artifacts, URLs, logs, screenshots or
issue reports. Setup generates them at runtime. Cleartext must not be recoverable from a committed
server verifier. Do not invent a cryptographic challenge protocol.

## Recovery and retention

Trash defaults to 30 days from deletion; version history defaults to 90 days. Retention is
configurable and storage usage is visible. Never discard unsynchronized drafts to satisfy cleanup
rules. Persist deletion metadata sufficiently to prevent an old client from silently resurrecting
removed content.

VPS and client replicas are the existing storage resources. Replication is not an independent
historical backup because erroneous changes and deletions can propagate. Coherent snapshots and
local recovery exports are proposed protections; automated backups outside the VPS are not part of
the agreed scope. Test actual restore behavior and key recovery.

Protect local data, attachments, temporary files and secrets consistently. Windows Hello is optional
per device. Encryption key recovery and any end-to-end encryption protocol require a separate,
documented design before implementation.

## Editor and exports

Reuse an existing editor library. Tiptap/ProseMirror, CEF/CefSharp, Yjs/Hocuspocus and Open XML SDK
are candidates. Verify licenses, supported runtimes, accessibility, media support and print layout
before adopting them. Do not assume paid extensions are included in an open-source core.

The page sections are **Editor**, **Details**, **Progress**, **Sources** and **Secondary actions**.
Keep print layout as the text view. PDF and DOCX are exports, not the canonical storage format:
interactive study blocks need an explicit static representation and a conversion summary. Preserve a
complete native format for round-trip transfer.

## Migration and retirement

Migrate only authorized data from Studiario, preserving hierarchy, identifiers, versions, files and
personal progress. Compare transferred data and file hashes and prove recovery. Studiario is retired
only after transition validation. Existing VPS services and unrelated projects are outside
repository preparation.

Clinical study material retains qualified Italian source versions and existing availability
restrictions. Platform migration is not clinical validation. No patient-identifying fields or AI
content generation are introduced.
