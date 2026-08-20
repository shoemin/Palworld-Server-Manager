# Installation

## Current distribution

=== "Portable ZIP (available now)"

    Palworld Server Manager currently ships as a **self-contained, portable** win-x64 build attached to each [GitHub Release](https://github.com/shoemin/Palworld-Server-Manager/releases).

    1. Download the `PalworldServerManager-vX.Y.Z-win-x64.zip` asset from the latest release.
    2. Extract it anywhere you like (it does not need to be `Program Files`).
    3. Run `PalworldServerManager.exe`. No installer, no separate .NET runtime download — the build is self-contained.
    4. Windows SmartScreen may warn about an unrecognized publisher, since the build is not currently code-signed. This is a known, documented limitation, not a sign of tampering — see [Security & Safety Model](../reference/security-model.md).

=== "Setup.exe installer (not yet released)"

    A per-user Setup.exe installer, built with [Velopack](https://velopack.io), is implemented and has been verified with a real local install/uninstall cycle: it installs to its own `%LocalAppData%\ShoeMin.PalworldServerManager\` location, separate from the persistent data directory below, and uninstalling removes only that program-files-style directory, shortcuts, and its Add/Remove Programs entry — never your servers, backups, or logs.

    It is **not yet part of the automated release pipeline** and is not attached to GitHub Releases, so there is no published version to install yet. In-app **Check for Updates** (Help menu) is implemented and works against a real install, but since nothing is published there is currently nothing for it to find — see [Manager Updates](../manager-updates/index.md). This page will be updated once a Setup.exe is actually published.

## Where things live

Palworld Server Manager separates **program files** from **persistent data** on principle, so that reinstalling, updating, or moving the application never touches your servers.

| What | Where |
|---|---|
| Application files (the portable build today; installed binaries once Setup.exe ships) | Wherever you extracted the ZIP, or the future installer's own program-files-style location |
| Persistent data: managed servers, backups, logs, SteamCMD, profile registry, LAN state | `%LocalAppData%\PalworldServerManager\` |

See [File Locations](../reference/file-locations.md) for the full breakdown.

## Uninstalling / removing

To remove the portable build today, just delete the folder you extracted it to and, if you want to remove your managed servers/backups/logs too, separately delete `%LocalAppData%\PalworldServerManager\`. The application itself never does this for you automatically, and the future installer's uninstall action is designed the same way: it will **not** delete `%LocalAppData%\PalworldServerManager\` by default. A "delete all Manager and server data" feature would be a separate, explicitly-confirmed action if it is ever added.
