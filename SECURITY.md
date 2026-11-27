# Security policy

## Project maturity

StudyDesk has no supported application release yet. Do not treat this repository foundation as a
production-ready data service or store important data in an unqualified build.

## Report privately

Use
[GitHub private vulnerability reporting](https://github.com/sup3r93/StudyDesk/security/advisories/new)
when available. If reporting is unavailable, open an issue asking for a private channel without
disclosing exploit details or secrets.

Include the affected commit/version, reproduction steps, impact and a redacted minimal example.
Never send real notes, patient information, tokens, private keys or an entire personal database. Do
not publish a working exploit before coordinated handling.

## Secret handling

- Device/server tokens are runtime-generated and must never appear in committed code or
  configuration.
- Use a separate token for each association, protected client storage and a server verifier.
- Validate the server's TLS identity and enforce scoped permissions on every endpoint and
  collaboration path.
- Revoking a device must terminate its active server access; keep offline drafts recoverable without
  accepting unauthorized uploads.
- Configure redaction before logging security failures.

GitHub settings, Gitleaks, dependency review and Dependabot provide complementary checks. They do
not guarantee that all secrets or vulnerabilities will be detected. A copied bearer token grants its
configured access until revoked or expired.
