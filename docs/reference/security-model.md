# Security & safety model

## Guiding principle

Your Palworld world is worth more than anything the Manager does to manage it. Destructive-shaped operations follow **validate → back up → perform → verify**, not delete-then-hope. Concretely:

- Importing an existing server never modifies the source — it's re-hashed at the end to prove it wasn't touched. See [Importing an existing server](../getting-started/existing-server-import.md).
- Restoring a [backup](../guide/backups.md) automatically creates a pre-restore backup first.
- Importing a [`.palserver` package](../guide/portable-packages.md) verifies every file's SHA-256 against its manifest before installing anything.
- A [LAN transfer](../guide/lan.md#sending-a-server-to-another-pc) never finalizes a received file until both its byte count and whole-file SHA-256 match what the sender declared; a failed/corrupted transfer never leaves a usable package on disk.

## Secrets are never sent or logged

- Palworld's `AdminPassword` and `ServerPassword` never leave the machine that has them. A [paired LAN Manager](../guide/lan.md#pairing) only ever receives sanitized Dashboard data, never raw REST credentials.
- The [Dashboard's Settings view](../guide/dashboard.md) redacts any setting whose key looks like a password, token, secret, credential, or API key before it's displayed or transmitted anywhere — this redaction happens at the data layer, not just hidden in the UI.
- Diagnostic bundles exclude `.sav` save files entirely and redact AdminPassword/ServerPassword before anything is written to the bundle.
- Manager logs never contain AdminPassword, ServerPassword, or LAN pairing/bearer tokens.
- The runtime-handoff file written before [installing a Manager update](../manager-updates/update-while-server-running.md) (`runtime\update-handoff.json`) records only process identity (profile ID/name, install path, PID, executable path, start time) — never a password, token, or any other secret. It's one-shot (deleted after being read once) and rejected outright if it's stale (older than 5 minutes) or malformed, rather than partially trusted.

## LAN trust model

- LAN networking is **disabled by default**.
- Discovering a peer grants no access — only an explicit, one-use, 5-minute pairing code (locked out after 10 wrong attempts) does. See [Pairing](../guide/lan.md#pairing).
- Inbound bearer tokens are stored **hashed**, not in plaintext, and compared with a fixed-time comparison.
- The remote Dashboard is strictly read-only in this version — there is no remote settings editing, shutdown, kick, or ban.
- A received transfer's destination filename is always sanitized and confined to the Manager's own `incoming` directory; a sender cannot cause a path-traversal write anywhere else on disk.

## Distribution / code signing

The current build is not code-signed, so Windows SmartScreen may show an "unrecognized publisher" warning. This is a known limitation, not evidence of tampering — verify a release by checking its published `SHA256SUMS.txt` against the file you downloaded if you want extra assurance. Code signing is a deferred item, not solved yet.
