# Test and Log Collection Guide

This build is intended to make failures reproducible and diagnosable without requiring you to manually inspect log files.

## What to send when something fails

### Build or self-test failure

Run `build.cmd`. Send the newest file from:

```text
build-logs\build-YYYYMMDD-HHMMSS.log
```

Include one sentence describing whether the failure occurred during restore, build, or self-tests if that is obvious from the console.

### Application/runtime failure

After reproducing the issue:

1. Keep the affected managed server selected if possible.
2. Click **Export Diagnostic Bundle**.
3. Send the generated ZIP.
4. State what action you performed and what you expected.

The diagnostic ZIP excludes `.sav` files and redacts `AdminPassword` / `ServerPassword` values.

### Application crashes before you can export diagnostics

After restarting the manager, click **Open Logs Folder** and send the newest:

```text
manager-YYYYMMDD-HHMMSS-<session>.log
```

If the issue involved a specific server, also include the corresponding file under:

```text
logs\servers\server-<uuid>.log
```

## Recommended first test pass

### 1. Build gate

Expected:

- Solution restores.
- Release build succeeds.
- All self-tests pass.
- A build transcript is created.

### 2. Existing-server scan

Expected:

- The manager checks bounded Steam/SteamCMD locations only.
- Your existing server appears if it is in an expected location.
- A custom-location server can be selected manually.
- No files are changed by discovery.

### 3. Existing-server import

Before testing, retain your own independent backup of the source server.

SteamCMD preflight expected behavior:

- If Steam is running, provisioning continues without an extra prompt.
- If Steam is not running, the manager offers to launch Steam, continue with anonymous SteamCMD, or cancel before provisioning.
- If you choose to launch Steam, sign in and wait for Steam to finish connecting before continuing.
- The manager does not inspect credentials or claim to verify account ownership.
- If SteamCMD returns exit code `7`, the manager offers one interactive preflight/retry rather than retrying blindly.

Expected:

- Live source servers are refused.
- Source `Pal\Saved` / `Mods` trees are hashed before import.
- A fresh isolated runtime is installed.
- Save/config/mod data is copied into the managed profile.
- Source trees are hashed again and must match.
- The original source installation remains untouched.

If this fails, export diagnostics immediately after the failure.

### 4. Managed server start

Expected:

- `PalServer.exe` launches from the selected managed profile.
- The manager recognizes both the root launcher and any `PalServer-*Shipping*` child process under that install directory.
- Status reports Running.
- The world can be joined normally.

### 5. Safe stop

Expected when REST is enabled and `AdminPassword` is configured:

- Manager requests `/save` first.
- Manager requests `/shutdown` second.
- Manager waits for all PalServer processes belonging to that managed installation to exit.
- Status returns to Stopped.

Do not send passwords with a bug report; diagnostic export redacts them automatically.

### 6. Server switching

Expected:

- Starting Server B while Server A runs first safely stops Server A.
- B launches only after A is confirmed stopped.
- Returning to A preserves its world independently from B.

### 7. Settings

Expected:

- Active and shipped default settings appear in the editor.
- Unknown settings remain present after editing/saving a known setting.
- Password values are never written to manager logs.

### 8. Backup and restore

Expected:

- Filesystem backup requires the server to be stopped.
- Restore creates a pre-restore backup.
- Restored save/config/mod state matches the selected backup.

### 9. Portable export/import

Expected:

- Export creates `.palserver` with per-file hashes.
- Standard server runtime binaries are not embedded.
- Import verifies hashes first.
- A fresh server runtime is installed on the destination.
- The restored world/profile state matches the source server.

## Useful log fields

Manager log lines include fields such as:

```text
session=<session-id>
op=<operation-id>:<operation-name>
serverId=<profile-uuid>
serverName="<display-name>"
```

Examples of operation names include:

```text
ScanExistingServers
ImportExistingServer
CreateServer
StartServer
StopServerSafely
ForceStopServer
UpdateServer
CreateBackup
RestoreBackup
ExportPortablePackage
ImportPortablePackage
LoadSettings
SaveSettings
ExportDiagnostics
```

When reviewing a failure, all lines sharing the same `op=<operation-id>` belong to the same high-level attempt.


## v0.2.5 process-lifetime tests

1. Start a disposable server. Confirm status becomes `Running (monitored)` and Start is disabled for that profile.
2. While it is running, attempt to start/switch as applicable. The manager must not create a second instance of the same profile.
3. Close the PalServer console/window manually. Within a few seconds the UI must transition to Stopped and the manager/server log must contain observed process exit code entries.
4. Start again, then induce a disposable-server crash/non-zero exit if safe to do so. The UI should report `Stopped / Error (exit N)` and offer diagnostics.
5. Start Server A and use Start / Switch To on Server B. B must not launch until A has fully exited and its lifetime monitor has finalized.
6. Safe Stop and Force Stop should be logged as expected stops rather than crashes.
