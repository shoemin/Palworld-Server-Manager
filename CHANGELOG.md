# Changelog

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
