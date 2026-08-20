# v0.3.0 — Native Dashboard and LAN Server Transfer

This milestone adds a native WPF Dashboard plus paired Manager-to-Manager LAN functionality.

## Dashboard

The main window now contains:
- Servers
- Dashboard
- LAN & Transfers

Dashboard sources can be local managed servers or servers exposed by a paired Palworld Server Manager on the same LAN.

The Dashboard reads the running Palworld server through the host Manager. Palworld REST credentials never leave the host.

Implemented REST reads:
- `/info`
- `/metrics`
- `/players`
- `/settings`

The Dashboard Settings page is intentionally read-only. Configuration changes remain in the existing server Settings editor. Password/token/secret-like values are redacted before they are shown or sent to a paired Manager.

Metrics retain up to 60 minutes in memory at a five-second sample interval.

## LAN security boundary

LAN functionality is disabled by default.

When enabled:
- Manager API TCP default: 8215
- UDP discovery default: 8216
- peers are discovered by a minimal protocol advertisement;
- remote access requires a one-use six-digit pairing code that expires after five minutes and is invalidated after repeated failed attempts;
- successful pairing yields a random bearer token;
- Palworld AdminPassword is never exposed to the remote Manager;
- the Manager LAN API is intended only for trusted LANs and must not be port-forwarded to the Internet.

The first enable may cause Windows Firewall to request permission for private networks.

## `.palserver` transfer

The existing `PortablePackageService` remains the canonical migration path.

Send workflow:
1. export through the normal `.palserver` service;
2. if running, perform the existing safe-stop behavior first;
3. calculate a package SHA-256;
4. send a transfer offer;
5. destination explicitly accepts or rejects;
6. upload into a `.partial` file;
7. verify length and SHA-256;
8. rename to `.palserver` only after verification.

The destination can then import the received file using the existing package importer, which performs its own per-file manifest/hash validation and installs a fresh Palworld runtime through SteamCMD.

## First test plan

Use two Windows PCs on the same private LAN.

1. Run `build.cmd`.
2. Start Manager on both PCs.
3. Enable LAN on both PCs under `LAN & Transfers`.
4. Allow the app on Private networks if Windows Firewall prompts.
5. Confirm each Manager discovers the other.
6. On PC B, generate a pairing code.
7. On PC A, select PC B, Pair Selected, enter the code.
8. On PC A Dashboard, Refresh Sources and select a server hosted by PC B.
9. Verify Overview, Players, Metrics, and read-only Settings.
10. On PC A Servers tab, select a disposable server and click `Send to PC`.
11. Select PC B.
12. On PC B LAN & Transfers, accept the incoming offer.
13. Verify transfer completes and the receiver reports `Received`.
14. Import the received package.
15. Start the imported server and verify world/player/settings integrity.

## Known v0.3.0 boundaries

- LAN only; Internet exposure is unsupported.
- HTTP is used inside the paired LAN channel; do not use hostile/untrusted networks.
- Paired outbound bearer credentials are stored in the Manager LAN state under the current Windows user profile; OS-level credential protection is a later hardening item.
- Unpairing removes both inbound and outbound trust on the local Manager. The remote Manager must also unpair locally to remove its saved trust record; synchronized revocation is a later hardening item.
- Transfer resume is not implemented; failed transfers restart.
- Remote server administration (kick/ban/shutdown/settings editing) is intentionally out of scope.
- `/game-data` visualization is intentionally deferred.
