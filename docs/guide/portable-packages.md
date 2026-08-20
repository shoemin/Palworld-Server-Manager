# Portable `.palserver` packages

A `.palserver` file is Palworld Server Manager's portable, self-contained way to move a managed server between installations — including [directly between two Managers over LAN](lan.md#sending-a-server-to-another-pc).

## What's included

- `Pal/Saved` — world/player save data and configuration.
- `Mods` — any mods the server uses.
- A manifest listing every included file with its length and SHA-256 hash.

## What's deliberately excluded

The package does **not** include the Palworld Dedicated Server runtime binaries. Importing always installs a fresh, clean runtime through SteamCMD rather than trusting a bundled copy — this keeps packages smaller and means every import starts from a known-good runtime state.

## Exporting

From **Servers → select a server → Send to PC** (for LAN transfer) or the equivalent export action, the Manager:

1. Stops the server safely first if it's running (a live server is never copied).
2. Enumerates the save/config/mods payload and computes a SHA-256 for each file.
3. Writes everything plus the manifest into a zip-based `.palserver` archive.

## Importing

Importing a `.palserver` file:

1. Validates the package and reads its manifest.
2. Re-hashes every file inside and compares it against the manifest — a corrupted or tampered package is rejected before anything is installed.
3. Installs a fresh Palworld runtime through SteamCMD.
4. Copies the verified save/config/mods data into the new runtime.
5. Registers the result as a new managed server profile.

If any step fails, the partially-created destination is cleaned up rather than left behind in a broken state.

## Two layers of integrity

If a package arrives via [LAN transfer](lan.md), there are two independent hash checks: the whole-file SHA-256 verified when the transfer itself completes, and then this per-file manifest verification when it's actually imported. Both have to pass.
