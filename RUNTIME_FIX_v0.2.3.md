# Runtime Fix v0.2.3

This revision addresses two issues found in the first real-machine runtime logs from v0.2.2.

## 1. First-run SteamCMD provisioning retry

On a machine where the manager had to download SteamCMD itself, the first Palworld provisioning invocation allowed SteamCMD to self-update but then exited with code 7 (`Missing configuration`). Retrying the same import immediately succeeded because SteamCMD was then fully initialized.

v0.2.3 records whether SteamCMD existed before provisioning. If the first provisioning invocation fails only in the freshly-installed case, the manager automatically retries the same SteamCMD command once. Normal failures from an already-existing SteamCMD installation are still surfaced immediately, and a failed retry is still reported as an error.

## 2. JSON Palworld logs in diagnostic bundles

A server configured with `LogFormatType=Json` produces text diagnostics that can use a `.json` extension. v0.2.2 only collected `.log`, `.txt`, and `.out` files from `Pal\\Saved\\Logs`.

v0.2.3 includes `.json` files from that bounded server-log directory as diagnostic text logs. The diagnostic self-test now verifies JSON server-log inclusion while continuing to verify that `.sav` files are excluded and passwords are redacted.

No server profile, save, backup, portable package, or settings file formats changed in this revision.
