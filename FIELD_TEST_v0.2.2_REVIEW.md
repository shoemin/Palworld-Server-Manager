# v0.2.2 Field-Test Review

Evidence reviewed from the first real Windows test session on 2026-08-17.

## Confirmed PASS from supplied logs

- Solution Release build completed successfully.
- Self-test executable completed successfully.
- Portable package manifest and SHA-256 payload verification succeeded.
- Portable import succeeded on the second attempt after SteamCMD had initialized itself.
- Managed PalServer launch succeeded and the manager detected the server process.
- Process inspection detected both `PalServer.exe` and the Unreal `PalServer-Win64-Shipping-Cmd.exe` child during shutdown.
- Settings loaded and saved successfully.
- Portable `.palserver` export completed successfully.
- After REST API and an admin password were configured, `/save` returned HTTP 200 and `/shutdown` returned HTTP 200; the server then exited gracefully.
- Diagnostic export completed and redacted `AdminPassword` and `ServerPassword`; the supplied diagnostic bundle contained no `.sav` files.
- Update/validate created a pre-update backup first, SteamCMD returned exit code 0, and `PalServer.exe` was verified afterward.

## Defects found and addressed in v0.2.3

### First-run SteamCMD bootstrap failure

The first portable import downloaded a fresh SteamCMD bootstrap. That SteamCMD invocation updated itself but exited with code 7 (`Missing configuration`), causing the manager to report the import as failed. Repeating the same import immediately succeeded.

v0.2.3 automatically retries provisioning once only when SteamCMD did not exist before the operation. Normal failures from an established SteamCMD installation are not automatically retried indefinitely.

### JSON Palworld logs omitted from diagnostics

The selected server was configured with `LogFormatType=Json`. v0.2.2 only treated `.log`, `.txt`, and `.out` files under `Pal\\Saved\\Logs` as shareable text logs, so the diagnostic bundle contained no actual Palworld server log.

v0.2.3 adds `.json` to the bounded server-log extensions and changes the diagnostic self-test to verify JSON log inclusion.

## Expected behavior observed, not a defect

The first safe-stop attempt refused to automate shutdown because REST was not yet enabled/configured. Force Stop then worked. After REST and the admin password were configured, safe shutdown worked through REST as intended.

## Still requiring acceptance testing

The supplied logs do not yet show these operations:

- expected-location legacy server scan;
- safe import of a pre-manager legacy server through the discovery workflow;
- independent PRE/POST source-tree hash comparison for legacy import;
- creation of a new Server B;
- A/B server switching and world isolation;
- manual backup creation and restore;
- portable import on a clean manager installation or second PC;
- persistence verification after restore/import and subsequent restart.
