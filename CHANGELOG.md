# Changelog

## 0.4.0 (in progress — Prerelease channel, not yet published as Stable) - Manager installer, self-update, and runtime reattachment

Published so far as prereleases only: `v0.4.0-alpha.1`, `v0.4.0-alpha.2`, `v0.4.0-beta.1` (Velopack channel `win-beta`). There is no Stable/`win` `v0.4.0` release yet — this section documents work completed toward it, not a shipped stable version.

- Adds a per-user Windows installer (`Setup.exe`) built with [Velopack](https://velopack.io), alongside the existing portable ZIP distribution. Installed application files live at `%LocalAppData%\ShoeMin.PalworldServerManager\`, a distinct package identifier chosen so it can never collide with the persistent-data root at `%LocalAppData%\PalworldServerManager\`.
- Adds three execution modes (Installed / Portable / Development) that determine whether the Manager can check for, download, and install its own updates — Portable and Development copies explain why they can't rather than pretending to check.
- Adds Stable (`win`) and Prerelease (`win-beta`) update channels, derived directly from the release tag's SemVer prerelease suffix so the mapping can't drift between what the pipeline builds and what the app expects. A freshly installed package defaults to the channel it was actually built for; any channel the user explicitly picks afterward always wins.
- Adds the full Check for Updates → Download Update → Install and Restart flow in a dedicated Updates window, with live download progress, cancellation, and release notes.
- **Manager self-update is fully independent of Palworld's lifetime.** Checking and downloading an update never touch a running server (no save, no shutdown, no force-stop, no SteamCMD, no backup). Installing writes a short-lived runtime handoff recording exactly which servers are running and their process identity (PID, executable path, start time), stops only the Manager's own LAN services, hands off to Velopack's external updater, and exits — Palworld's own process(es) are never part of that exit.
- Adds startup runtime reconciliation: the restarted Manager reads the handoff, verifies the process identity of anything still running, and reattaches its lifetime monitor — the same safe matching path used for a normal Manager restart (not just after a self-update).
- Adds `CriticalOperationTracker` gating so Install and Restart is blocked while the Manager is mid-operation (server start/stop, SteamCMD provisioning, backup/restore, legacy import, `.palserver` export/import, settings save, an active LAN transfer, or another update already in progress) — enforced at the moment of commit, not just by graying out a button. A running Palworld server, by itself, never blocks an update.
- Hardens the release pipeline's previous-release retrieval: pinned `vpk` 1.2.0 defaults `--pre` to `false`, which silently prevented retrieving a previous prerelease for delta generation until fixed — `--pre` is now passed only for prerelease channels.
- Adds real Velopack delta package generation between consecutive releases on the same channel, verified against a real previous-release download.
- Adds canonical LICENSE byte verification to the release pipeline: raw checked-out bytes are proven identical to the Git blob actually committed in the release tag (via `git hash-object --no-filters`, deterministic `.gitattributes` LF checkout), then traced through the packaging directory, both archive formats, and a real installed copy from a genuine silent install.
- Hardens the public `SHA256SUMS.txt` checksum manifest twice: first to list only files Velopack actually publishes (excluding local-only packaging bookkeeping), then — after `v0.4.0-alpha.2` showed that `vpk upload github` can regenerate a channel feed file's bytes in-memory at publish time rather than uploading the local copy verbatim — to generate and verify the manifest from the actual downloaded GitHub Release bytes, post-publication, with a final independent re-verification pass.
- **Field-proven, not just automated-tested:** a real installed `v0.4.0-alpha.1` checked for, downloaded, and installed the published `v0.4.0-alpha.2` update while a real Palworld dedicated server ran throughout, preserving the exact same Palworld Shipping process ID and start time across the Manager replacement, with correct reattachment, no update-triggered REST/SteamCMD activity, working Dashboard/LAN recovery, and a successful Safe Stop afterward. The `v0.4.0-alpha.2 -> v0.4.0-beta.1` update and a real `v0.4.0-beta.1` LAN `.palserver` transfer were separately field-confirmed as well. One related scenario — a genuinely in-flight Manager-owned operation actively blocking Install and Restart — has automated coverage but is still awaiting its own field pass.
- No existing `.palserver` manifest format, managed-server profile format, or persistent-data layout was changed by any of this work.

## 0.3.0 - Native REST dashboard and LAN server transfer

- Adds a native WPF **Dashboard** tab with Overview, Players, Metrics, and read-only live Settings views.
- Extends the Palworld REST client with typed reads for `/info`, `/metrics`, `/players`, and `/settings`.
- Dashboard REST settings are strictly read-only and password/token/secret-like values are redacted before display or LAN transport.
- Adds rolling in-memory charts for server FPS, player count, and frame time (up to 60 minutes at five-second sampling).
- Adds a **LAN & Transfers** tab with LAN enable/disable controls, UDP peer discovery, one-use six-digit pairing codes, and mutually authenticated Manager pairing.
- Adds a dedicated `PalworldServerManager.Lan` project hosting the paired Manager LAN API with ASP.NET Core/Kestrel.
- Remote Dashboard access reuses the same WPF dashboard UI while keeping Palworld REST credentials on the host PC.
- Adds **Send to PC** on managed servers; transfers reuse the existing `.palserver` export/import format rather than live-copying save directories.
- Incoming transfers require explicit acceptance, write to `.partial`, verify length and SHA-256 before finalizing, and can then be imported through the existing package importer.
- Adds disk-space preflight before accepting a transfer and removes incomplete partial files on transfer failure.
- LAN functionality is disabled by default and is intended only for trusted private networks; Internet exposure is unsupported.
- No existing `.palserver` manifest format or managed-server profile format was changed.
- Fixed a bug where every LAN transfer receive failed to finalize (`.partial` -> `.palserver`) because the verification hash stream was still open when the file was renamed; the receiver now closes the stream before finalizing.
- Bounded LAN dashboard/pairing/server-list HTTP calls to a 15-second timeout so an unreachable peer cannot hang the Dashboard indefinitely; large `.palserver` transfers intentionally remain unbounded.
- Added self-tests covering Palworld REST model parsing/redaction, pairing code lifecycle, trusted-peer token storage, LAN discovery filtering, and an end-to-end loopback LAN API/transfer flow (auth, offer validation, hash verification, corrupted-transfer cleanup).

## 0.2.5 - Owned server lifetime / exit-code monitoring

- PalServer launches are now retained and monitored for their full lifetime instead of being disposed immediately after `Process.Start`.
- Start is locked for a profile until the manager observes that all PalServer processes from that managed install have exited and captures their exit codes.
- The lifetime monitor attaches to both the root `PalServer.exe` process and discovered `PalServer-Win64-Shipping*` child processes, logging each exit code.
- Manual server-window closure is detected automatically; non-zero unexpected exits are surfaced as crashes and direct the tester to export diagnostics.
- Manager-requested safe/force stops are marked as expected so their termination codes are not mislabeled as crashes.
- Server switching cannot launch the next profile until the previous managed lifetime has finalized and released its lock.
- The main window refreshes runtime status every second and disables Start for the currently running/monitored profile.

## 0.2.4 - Steam preflight and code-7 recovery

- Detects whether the Steam desktop client is running before operations that provision/update Palworld through SteamCMD.
- If Steam is not running, offers to launch it, continue with anonymous SteamCMD, or cancel before any provisioning begins.
- Explicitly states that Steam login/account ownership cannot be reliably verified by the manager and is not an official Palworld dedicated-server download requirement.
- Replaces the v0.2.3 silent SteamCMD retry with an interactive recovery path for field-observed exit code 7.
- After code 7, the user can run the Steam preflight and retry the entire operation once.
- Adds logging for Steam-client detection, preflight choices, Steam launch attempts, and recovery retries.
- Adds a self-test for the code-7 recovery classification policy.

## 0.2.3 — First-run SteamCMD retry / JSON diagnostics

- Automatically retries Palworld provisioning once when a freshly downloaded SteamCMD exits non-zero during its first bootstrap/self-update invocation.
- Keeps normal SteamCMD failures visible; the retry is limited to the freshly-installed SteamCMD case and to one retry.
- Diagnostic bundles now include `.json` files from the Palworld `Saved\Logs` directory, supporting servers configured with `LogFormatType=Json`.
- Updated the diagnostic self-test to verify JSON server-log inclusion while preserving password-redaction and `.sav` exclusion checks.
- No server profile, save, backup, settings, or `.palserver` package formats changed.

## 0.2.2 — MainWindow compile fix / SDK pin

- Fixed `MainWindow.xaml.cs` compile errors reported by the external Windows build:
  - added explicit `System.IO` import for `Directory` and `Path`;
  - rewrote the selected-profile check in `OpenFolder_Click` so definite assignment is explicit.
- Added `global.json` targeting the .NET 8 SDK family (`8.0.100` with `latestFeature` roll-forward) so a machine with a current .NET 8 SDK uses that SDK instead of a newer major SDK by default.
- Retains all v0.2.1 and v0.2.0 functionality.
- No server profile, save, backup, import/export, or diagnostic bundle formats were changed.

## 0.2.1 — WPF build fix

- Fixed the WPF application build failure caused by an ambiguous `Application` type when WinForms was enabled.
- Replaced the WinForms `FolderBrowserDialog` with WPF's .NET 8 `Microsoft.Win32.OpenFolderDialog`.
- Removed the unnecessary WinForms project dependency.
- Explicitly derives `App` from `System.Windows.Application`.
- Added an explicit Windows guard inside the Steam registry helper to satisfy CA1416 platform analysis.
- No server profile, save, import/export, or diagnostic bundle formats were changed.

## 0.2.0 — Diagnostic logging build

- Added per-launch structured manager log files with ISO-8601 timestamps, severity levels, session IDs, and operation-correlation IDs.
- Added per-server correlated manager logs under `logs\servers\`.
- Added SteamCMD stdout/stderr, PID, exit-code, provisioning, package, backup, discovery, import-hash, REST, and server-process diagnostics.
- Added global logging for unhandled WPF dispatcher, AppDomain, and unobserved Task exceptions.
- Added automatic cleanup of manager session logs older than 30 days.
- Added **Open Logs Folder** to the main UI.
- Added **Export Diagnostic Bundle** to the main UI.
- Diagnostic bundles include recent manager/server logs, runtime metadata, selected-server process information, a sanitized profile snapshot, and sanitized settings.
- Diagnostic bundles explicitly exclude `.sav` files and redact Palworld admin/server passwords.
- Added large-log tail extraction to keep diagnostic bundles practical to share.
- Added build/self-test transcript logging under `build-logs\`.
- Added self-tests for structured logging, per-server logs, diagnostic secret redaction, and save-file exclusion.

## 0.1.0 — Initial prototype

- Initial managed-server lifecycle, bounded legacy discovery/import, settings editor, backup/restore, and portable `.palserver` export/import prototype.
