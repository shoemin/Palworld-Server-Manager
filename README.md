# Palworld Server Manager

A Windows desktop application for managing **Palworld dedicated servers** through a simple graphical interface.

Palworld Server Manager is designed to make it easier to create, import, configure, back up, export, update, start, stop, and switch between isolated Palworld dedicated server installations without manually managing SteamCMD, configuration files, save directories, and server processes.

> **Project status:** Early development / prerelease
> The application is currently being actively tested. Back up important Palworld server data independently before using prerelease builds.

**[Documentation](https://shoemin.github.io/Palworld-Server-Manager/)** · **[Download the latest release](https://github.com/shoemin/Palworld-Server-Manager/releases/latest)** · **[Report a bug](https://github.com/shoemin/Palworld-Server-Manager/issues)**

---

## Features
<!-- PSM-v0.3.0-LAN-DASHBOARD -->
### Native REST Dashboard

The application includes a native WPF **Dashboard** tab for both local managed servers and servers hosted by paired Palworld Server Manager instances on the same trusted LAN.

The Dashboard provides:

* Overview/status information.
* Live player information.
* Rolling server FPS, player-count, and frame-time history.
* Live REST API settings in a **strictly read-only** view.
* Redaction of password/token/secret-like setting values.

Palworld REST credentials remain on the host Manager and are not sent to paired Manager instances.

### LAN Manager Pairing and Server Transfer

The **LAN & Transfers** tab can discover nearby Palworld Server Manager instances, pair them with a one-use expiring code, view their managed servers through the same Dashboard, and transfer a server using the existing .palserver format.

Incoming transfers require explicit acceptance. Received data is written as a temporary .partial file and is finalized only after byte-length and SHA-256 verification. The normal .palserver importer then performs its existing per-file manifest/hash checks before installation.

LAN functionality is disabled by default and is intended only for trusted private networks. Do not port-forward the Manager LAN API to the Internet.

---

### Multiple Server Profiles

Manage multiple independent Palworld dedicated servers from one application.

Each managed server receives its own isolated Palworld server installation and save/configuration directories, which helps prevent one server from accidentally modifying another server's files.

The manager is designed around the rule:

> One managed server profile = one isolated Palworld server installation.

---

### Existing Server Discovery

Palworld Server Manager can scan the expected Steam and SteamCMD installation locations for Palworld dedicated servers that existed before the manager was installed.

The scanner:

* Checks known Steam library locations.
* Checks expected SteamCMD locations.
* Does **not** recursively scan entire drives.
* Identifies Palworld server installations based on their expected file structure.
* Detects save data, configuration data, and server mods.
* Detects whether a discovered server is currently running.
* Avoids repeatedly offering servers that have already been imported.

A manual folder-selection option is also available for servers installed in custom locations.

---

### Safe Existing-Server Import

Existing Palworld servers can be imported into the manager without taking ownership of or modifying the original installation.

The import process:

1. Analyzes the existing server.
2. Hashes the original server-specific files.
3. Creates a new isolated managed server directory.
4. Installs a clean Palworld Dedicated Server runtime using SteamCMD.
5. Copies the existing server's save/configuration data.
6. Copies supported server-specific mod data.
7. Re-hashes the original server.
8. Verifies that the original files were not changed.

The original server remains available as an independent fallback.

---

### Create New Servers

Create new Palworld dedicated servers directly through the application.

Palworld Server Manager can:

* Download and manage SteamCMD.
* Install the Palworld Dedicated Server.
* Create an isolated server profile.
* Initialize server directories.
* Configure launch settings.
* Start the new server.

---

### Server Lifecycle Management

Start and stop managed Palworld servers from the UI.

The manager tracks the server process for its entire lifetime rather than simply launching it and assuming it remains running.

When a server starts, the manager:

1. Launches `PalServer.exe`.
2. Retains the process handle.
3. Detects the Palworld Unreal Engine shipping process.
4. Monitors both processes.
5. Prevents another Start operation while the server lifetime is active.
6. Waits for process termination.
7. Captures exit codes.
8. Distinguishes manager-requested shutdowns from unexpected exits.
9. Automatically updates the server state.

This allows the manager to detect:

* Normal server shutdowns.
* Manual console/window closes.
* Server crashes.
* Force stops.
* Unexpected process termination.

---

### Safe Server Shutdown

When the Palworld REST API is enabled and properly configured, Palworld Server Manager can perform a safe shutdown sequence:

1. Request a world save.
2. Request server shutdown.
3. Wait for the server processes to exit.
4. Capture the resulting exit codes.
5. Finalize the server state.

A Force Stop option is available when necessary, but should not be used as the normal shutdown method for important worlds.

---

### Server Switching

Switch from one managed server to another without manually locating files or executables.

When switching servers, the manager waits for the currently running server to completely terminate and finalize its lifecycle before launching the selected server.

This helps prevent:

* Multiple servers unintentionally sharing the same ports.
* Overlapping server processes.
* Save corruption caused by conflicting instances.
* Race conditions during shutdown/startup.

---

## Server Settings Editor

Palworld Server Manager includes a graphical editor for `PalWorldSettings.ini`.

Instead of editing the large `OptionSettings=(...)` entry manually, settings can be viewed and changed through the application.

Settings are organized into categories such as:

* General
* Gameplay
* Player
* Pals
* World
* Bases and Guilds
* PvP
* Hardcore
* Network
* Administration
* Advanced

The editor currently recognizes a large set of known Palworld settings while also preserving unknown settings.

### Forward Compatibility

Palworld updates may introduce configuration settings that the current manager version does not yet recognize.

The configuration parser is designed to preserve unknown settings instead of deleting them.

Unknown settings can still be displayed through the advanced settings interface.

---

## Backups

Create manual backups of managed server data.

Backups are stored separately from the active Palworld server installation.

Before restoring a backup, the manager automatically creates a **pre-restore backup** of the current state.

This provides an additional recovery point if the wrong backup is selected or the restored world is not what was expected.

---

## Portable Server Export / Import

Entire managed servers can be exported into a portable:

```text
.palserver
```

package.

A portable server package contains server-specific data such as:

* World saves
* Player saves
* Server configuration
* Relevant Palworld server state
* Supported server mod data
* Package metadata
* SHA-256 file hashes

Standard Palworld Dedicated Server binaries are intentionally **not** included.

When a `.palserver` file is imported on another computer, the manager:

1. Validates the package.
2. Verifies the stored SHA-256 hashes.
3. Creates a new managed server profile.
4. Installs a fresh Palworld server runtime using SteamCMD.
5. Restores the exported server data.
6. Prepares the server for launch.

This is intended to make moving a Palworld dedicated server to another PC substantially easier.

---

## SteamCMD Integration

Palworld Server Manager uses SteamCMD to install and update the official Palworld Dedicated Server.

Supported operations include:

* SteamCMD bootstrap download.
* Palworld server installation.
* Server update.
* Server file validation.
* Clean runtime installation during import.
* Pre-update backups.

### Steam Preflight

Before operations that require SteamCMD, the manager checks whether the Steam desktop client is currently running.

If Steam is not running, the application can offer to launch it before proceeding.

The manager does **not** attempt to inspect Steam credentials or determine which Steam account is signed in.

SteamCMD may still be used anonymously where supported.

If SteamCMD encounters a known provisioning failure, the manager can provide a recovery prompt rather than silently retrying indefinitely.

---

## Logging and Diagnostics

Palworld Server Manager includes detailed logging intended to make problems reproducible and diagnosable.

Logs include:

* Application startup information.
* Server profile operations.
* Operation correlation IDs.
* SteamCMD stdout/stderr.
* SteamCMD exit codes.
* PalServer process creation.
* PalServer process exit codes.
* Server start/stop state changes.
* REST save/shutdown requests.
* Import/export operations.
* Backup and restore operations.
* Existing-server discovery.
* Source-file integrity verification.
* Settings load/save events.
* Exceptions and stack traces.

Logs are stored under the application's local data directory.

The UI includes:

* **Open Logs Folder**
* **Export Diagnostic Bundle**

### Diagnostic Bundles

Diagnostic bundles are intended to be safe to share when reporting bugs.

They can contain:

* Manager logs.
* Server-manager operation logs.
* Recent Palworld server logs.
* Sanitized server configuration.
* Sanitized profile information.
* Environment/process information.

Diagnostic exports are designed to exclude Palworld `.sav` files.

Sensitive configuration values such as:

* `AdminPassword`
* `ServerPassword`

are redacted.

Additional token/password/secret redaction is also applied where possible.

**Always review diagnostic files yourself before uploading them publicly.**

---

# Installation

## Recommended: GitHub Release

Download the latest Windows release from the repository's **Releases** page.

Prerelease versions are used while the application is still undergoing significant testing.

The self-contained `win-x64` build does not require the user to separately install the .NET runtime. Starting with v0.4.0, releases are packaged with [Velopack](https://velopack.io) and include both a `Setup.exe` per-user installer (installs to its own location, separate from your server data — see the [documentation](https://shoemin.github.io/Palworld-Server-Manager/reference/file-locations/)) and a portable zip you can extract and run `PalworldServerManager.exe` from directly, no install required. See [Manager Updates](https://shoemin.github.io/Palworld-Server-Manager/manager-updates/) for in-app update checking once installed.

---

## Building from Source

### Requirements

* Windows 10 or Windows 11
* .NET 8 SDK
* Git, if cloning the repository

Clone the repository:

```powershell
git clone <repository-url>
cd PalworldServerManager
```

Run:

```powershell
.\build.cmd
```

The build script performs:

```text
Restore
â†’ Release Build
â†’ Self-tests
```

Build transcripts are stored under:

```text
build-logs\
```

A successful build should end with:

```text
Build and self-tests completed successfully.
```

---

## Publishing Locally

To create a distributable Windows build:

```powershell
.\publish.cmd
```

The publish process creates a self-contained `win-x64` application.

---

# GitHub CI

This repository includes GitHub Actions CI.

CI runs automatically for:

* Pushes to `main`
* Pull requests targeting `main`
* Manual workflow runs

CI performs:

```text
Restore
â†’ Release Build
â†’ Self-tests
```

Build transcripts are uploaded as GitHub Actions artifacts, including when a build fails.

---

# Automated GitHub Releases

GitHub Releases are created from version tags.

Examples:

```text
v0.2.5-beta.1
v0.2.6-beta.1
v0.3.0-rc.1
v1.0.0
```

Tags containing prerelease suffixes such as:

```text
-beta
-alpha
-rc
```

are intended to produce GitHub prereleases.

Stable version tags such as:

```text
v1.0.0
```

are intended to produce normal GitHub releases.

The release pipeline performs:

```text
Build
â†’ Self-tests
â†’ Documentation strict build
â†’ Publish
â†’ Velopack package (Setup.exe, update package, portable zip)
â†’ SHA-256 generation
â†’ GitHub Release
```

A prerelease tag (`-alpha.`, `-beta.`, `-rc.`, ...) packages to the `win-beta` update channel and is published as a GitHub prerelease; a stable tag packages to `win` and is published as a normal release — never mixed. See [Release process](https://shoemin.github.io/Palworld-Server-Manager/developer/release-process/) for the full pipeline detail.

A build, self-test, or documentation failure prevents the release from being published.

---

# Development Status

Palworld Server Manager is currently under active development.

The project is being tested incrementally against real Palworld dedicated servers.

Current development priorities include:

* Server lifecycle reliability.
* Existing-server import safety.
* Backup/restore verification.
* Server switching.
* Portable migration.
* SteamCMD error recovery.
* Settings compatibility.
* Diagnostic quality.
* Crash detection.

Until a stable release is published, prerelease builds should be considered test software.

---

# Important Backup Warning

**Always maintain an independent backup of important Palworld server data.**

Although the application is designed around non-destructive operations, backup-before-change behavior, and integrity verification, no software should be considered a substitute for an independent backup.

Before first using Palworld Server Manager with an existing server, it is strongly recommended that you:

1. Shut the existing Palworld server down cleanly.
2. Copy the entire existing PalServer directory to a separate backup location.
3. Keep that backup outside the manager's directories.
4. Do not delete the original installation until the managed copy has been thoroughly verified.

For particularly valuable worlds, keeping an additional backup on another physical drive or computer is recommended.

---

# Contributing

Contributions are welcome for noncommercial purposes.

You may:

* Fork the repository.
* Modify the source.
* Submit pull requests.
* Fix bugs.
* Add features.
* Improve documentation.
* Redistribute permitted versions under the terms of the license.

Before submitting a large feature, consider opening an issue first so implementation direction can be discussed.

When contributing code, please try to preserve the project's safety principles:

* Never modify an unmanaged server merely because it was discovered.
* Prefer copy/verify/commit workflows over destructive changes.
* Back up before potentially destructive operations.
* Preserve unknown Palworld configuration settings.
* Treat user save data as more important than application convenience.
* Make lifecycle operations observable through logging.
* Prefer testable behavior over implicit assumptions.

---

# Reporting Bugs

When reporting a bug, please include:

1. Palworld Server Manager version.
2. What operation you were attempting.
3. What you expected to happen.
4. What actually happened.
5. Whether the issue is reproducible.
6. The exported diagnostic bundle, when appropriate.

Please remove or redact any personal information before posting logs publicly.

Do **not** upload your Palworld world/player save files unless they are specifically necessary for reproducing an issue and you understand what they contain.

---

# Feature Requests

Feature requests are welcome through GitHub Issues.

Useful requests should explain:

* The problem being solved.
* The desired behavior.
* How the feature fits into normal Palworld server administration.
* Any safety concerns involving saves or configuration.
* Whether the feature applies to existing servers, newly created servers, or both.

---

# Project Safety Principles

Palworld Server Manager follows several core development principles.

## User Save Data Comes First

A Palworld world may represent hundreds or thousands of hours of player activity.

The application should always prefer preserving server data over convenience.

---

## Unmanaged Servers Are Read-Only

Finding an existing Palworld server does not give the manager permission to modify it.

Discovery should be observational.

Import should copy from the source rather than converting the source in place.

---

## Destructive Operations Require Recovery Paths

Where practical:

```text
Validate
â†’ Backup
â†’ Perform operation
â†’ Verify
â†’ Commit
```

should be preferred over:

```text
Delete
â†’ Replace
â†’ Hope
```

---

## Managed Servers Are Isolated

Each server profile should have its own dedicated server installation so that saves, configuration, updates, mods, and runtime behavior remain isolated.

---

## Failures Should Be Diagnosable

Important actions should produce enough logging to determine:

* What operation was requested.
* Which server was affected.
* Which process or file operation failed.
* What exit code/error occurred.
* What recovery action was attempted.

---

# License

Palworld Server Manager is **source-available software** licensed under the:

**PolyForm Noncommercial License 1.0.0**

See:

```text
LICENSE
```

for the complete license terms.

The intent of the license is to allow individuals and qualifying noncommercial organizations to:

* Use the software.
* Study the source code.
* Modify the software.
* Create derivative versions.
* Redistribute the software.
* Redistribute modified versions.

Commercial use is not granted by the public license.

This project should therefore **not** be described as OSI-approved open-source software. It is source-available software provided for noncommercial use.

If you are interested in commercial use, redistribution, integration, or licensing that falls outside the PolyForm Noncommercial License, contact the project owner to discuss separate permission.

---

# Donations and Project Support

Palworld Server Manager is free for permitted noncommercial use.

If the project is useful to you and you would like to support continued development, testing, documentation, and maintenance, optional donations are welcome.

Donations are voluntary and are **not required to use the software**.

A donation:

* Does not purchase the software.
* Does not unlock additional functionality.
* Does not grant additional licensing rights.
* Does not convert the software to a commercial license.
* Does not change the terms of the PolyForm Noncommercial License.

Donation links may be added here:

```text
TBD lol
```

---

# Commercial Licensing

The copyright holder may separately grant commercial permission or licenses.

If you would like to:

* Include Palworld Server Manager in a commercial product.
* Sell modified or redistributed versions.
* Offer the software as part of a paid service.
* Use the software in another way not permitted by the public license.

please contact the project owner before doing so.

---

# Disclaimer

This software is provided without warranty.

Use it at your own risk.

Always maintain backups of important Palworld server data.

The developers and contributors are not responsible for lost worlds, corrupted saves, lost configuration, downtime, data loss, Steam account issues, or other damages resulting from the use of this software.

Refer to the included `LICENSE` file for the complete legal terms governing use of the software.

---

# Third-Party Projects and Trademarks

Palworld Server Manager is an independent community project.

It is **not affiliated with, endorsed by, sponsored by, or officially supported by Pocketpair, Inc., Valve Corporation, or Steam**.

**Palworld** and related names, trademarks, game assets, and intellectual property belong to their respective owners.

**Steam** and **SteamCMD** are products/services of Valve Corporation and their respective trademarks belong to Valve Corporation.

Palworld Server Manager does not distribute the Palworld Dedicated Server binaries. Official server files are obtained through Steam/SteamCMD.

---

# Acknowledgements

Thanks to:

* Pocketpair for Palworld and the Palworld Dedicated Server.
* Valve for Steam and SteamCMD.
* Everyone who tests prerelease builds and provides diagnostic logs.
* Contributors who help improve reliability, compatibility, documentation, and safety.

---

# Summary

Palworld Server Manager aims to make Palworld dedicated-server administration approachable without sacrificing the safety of existing worlds.

Its primary goals are:

* Simple server management.
* Safe migration of existing servers.
* Isolated server profiles.
* Easy configuration.
* Reliable backups.
* Portable server migration.
* Predictable server lifecycle handling.
* Useful diagnostics when something goes wrong.

Most importantly:

> **Your Palworld world is more important than the server manager.**

The application should always be designed accordingly.

