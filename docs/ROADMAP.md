# Roadmap

Unchecked items are not implemented. Repository preparation does not imply a working application.

## Repository foundation

- [x] Separate `desktop/` and `server/` boundaries.
- [x] English and Italian root READMEs and explicit AI disclosure.
- [x] Architecture, coding standards, contribution and security guidance.
- [x] Pinned formatter, CI definitions, Dependabot configuration and ruleset definitions.
- [x] Confirm effective GitHub branch/security settings after bootstrap checks pass.

## Windows application

- [ ] Validate the editor/runtime prototype with print layout, accessibility and media.
- [ ] Implement local spaces, notes, attachments, search and crash-safe persistence.
- [ ] Support immediate offline use and optional Windows Hello with recovery.
- [ ] Add the five page sections and study tools without losing existing data.
- [ ] Implement a complete native format and verified PDF/DOCX exports.

## Data server and synchronization

- [ ] Implement runtime token generation, protected storage, scoped device association and
      revocation.
- [ ] Implement durable operations, acknowledgments, versioning and resumable attachments.
- [ ] Integrate real-time text collaboration and offline reconciliation.
- [ ] Enforce owner/editor/reader capabilities on all transport paths.
- [ ] Implement selective downloads, full-space downloads and a synchronization center UI.
- [ ] Implement 30-day trash, 90-day version history and tested recovery.

## Distribution and transition

- [ ] Package and test the Windows installer and self-hosted server.
- [ ] Verify secure upgrades and recovery of keys and unsynchronized work.
- [ ] Migrate and compare authorized Studiario data.
- [ ] Prove restore, then retire the previous application and its scoped resources.

No deployment to the existing VPS is authorized by this repository bootstrap. AI functionality and
automated off-VPS backups are outside the agreed application scope.
