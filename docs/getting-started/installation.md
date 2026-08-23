# Installation

## Current distribution

!!! note "Prerelease only"
    The current public release, `v0.4.0-alpha.1`, is a **Prerelease** (Velopack channel `win-beta`) — an experimental build for testing, not a Stable/`win` release. There is no Stable release published yet. Both options below come from that same prerelease.

=== "Setup.exe installer (recommended)"

    A per-user Setup.exe installer, built with [Velopack](https://velopack.io). Recommended if you want the Manager to check for, download, and install its own updates in place — see [Manager Updates](../manager-updates/index.md).

    1. Go to the [Releases page](https://github.com/shoemin/Palworld-Server-Manager/releases) and open the latest release marked **Pre-release**.
    2. Download the `ShoeMin.PalworldServerManager-win-beta-Setup.exe` asset.
    3. Run it. It installs to its own per-user location (see the table below), separate from your persistent server data — no admin rights needed.
    4. Windows SmartScreen may warn about an unrecognized publisher, since the build is not currently code-signed. This is a known, documented limitation, not a sign of tampering — see [Security & Safety Model](../reference/security-model.md).

    Uninstalling (via Windows "Apps" settings or Add/Remove Programs) removes only the installed program files, shortcuts, and its Add/Remove Programs entry — it does **not** delete your managed servers, backups, or logs. A "delete all Manager and server data" feature would be a separate, explicitly-confirmed action if it is ever added.

=== "Portable package"

    A self-contained, portable win-x64 build with no installer and no separate .NET runtime download. Use this if you'd rather run the Manager from a folder of your choice without installing anything — but note that Portable mode **cannot self-update**; you'll need to manually download each new version.

    1. Go to the [Releases page](https://github.com/shoemin/Palworld-Server-Manager/releases) and open the latest release marked **Pre-release**.
    2. Download the `ShoeMin.PalworldServerManager-win-beta-Portable.zip` asset.
    3. Extract it anywhere you like (it does not need to be `Program Files`).
    4. Run `PalworldServerManager.exe` from the extracted folder.
    5. Windows SmartScreen may warn about an unrecognized publisher, since the build is not currently code-signed. This is a known, documented limitation, not a sign of tampering — see [Security & Safety Model](../reference/security-model.md).

## Where things live

Palworld Server Manager separates **program files** from **persistent data** on principle, so that reinstalling, updating, or moving the application never touches your servers.

| What | Where |
|---|---|
| Installed application files (Setup.exe installer) | `%LocalAppData%\ShoeMin.PalworldServerManager\` |
| Portable application files | Wherever you extracted the portable package |
| Persistent data: managed servers, backups, logs, SteamCMD, profile registry, LAN state | `%LocalAppData%\PalworldServerManager\` |

Uninstalling the installed application, or deleting a portable folder, never touches the persistent-data path above. See [File Locations](../reference/file-locations.md) for the full breakdown.

## Uninstalling / removing

**Installed (Setup.exe):** uninstall like any other Windows application (Windows "Apps" settings or Add/Remove Programs). This removes the program files, shortcuts, and Add/Remove Programs entry only — it does **not** touch `%LocalAppData%\PalworldServerManager\`.

**Portable:** just delete the folder you extracted it to.

Either way, if you also want to remove your managed servers/backups/logs, separately delete `%LocalAppData%\PalworldServerManager\` — the application never does this for you automatically. A "delete all Manager and server data" feature would be a separate, explicitly-confirmed action if it is ever added.
