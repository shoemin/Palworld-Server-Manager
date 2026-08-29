# Palworld Server Manager

Palworld Server Manager is a Windows desktop application for creating, importing, configuring, backing up, and running **Palworld dedicated servers**, without hand-editing SteamCMD commands, configuration files, or save directories.

!!! warning "Back up your data independently"
    Palworld Server Manager is under active development. Back up important Palworld server data independently before installing any release, on either channel. Some features described in this site are still **planned** rather than shipped — each page says clearly which is which.

## What it does

- Creates new, isolated managed Palworld dedicated servers.
- Safely imports an existing Palworld server without modifying the original installation.
- Starts, safely stops, force-stops, and switches between managed servers, with crash/exit monitoring.
- Edits `PalWorldSettings.ini` through a GUI while preserving settings the editor doesn't recognize.
- Creates and restores backups.
- Exports/imports servers as portable `.palserver` packages.
- Shows a native live Dashboard (Overview, Players, Metrics, read-only Settings) for local and LAN-paired servers.
- Discovers other Manager instances on the same LAN, pairs them explicitly, and transfers `.palserver` packages directly between them.

## Where to start

<div class="grid cards" markdown>

- **New to Palworld Server Manager?** Start with [Installation](getting-started/installation.md) and [Your first server](getting-started/first-server.md).
- **Already running a Palworld server by hand?** See [Importing an existing server](getting-started/existing-server-import.md) — your original install is never modified.
- **Want the live Dashboard or LAN transfer?** See the [User Guide](guide/server-lifecycle.md).
- **Something isn't working?** Go to [Troubleshooting](troubleshooting/index.md).
- **Contributing code?** See the [Developer Guide](developer/architecture.md).

</div>

## Core safety principle

Your Palworld world — save data, player progress, base builds — is more valuable than anything Palworld Server Manager does to manage it. Every destructive-shaped operation in the app (import, restore, package import) follows a **validate → back up → perform → verify** pattern rather than deleting first and hoping. See [Security & Safety Model](reference/security-model.md) for the details.
