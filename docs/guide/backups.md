# Backups

Palworld Server Manager can create and restore filesystem backups of a managed server's save data and mods, independent of Palworld's own in-game auto-save/backup settings.

## Creating a backup

The server must be **stopped** first — the Manager will not back up a live, possibly-mid-write save directory. A backup is a zip file containing the server's `Pal/Saved` (world/player save data, configuration) and `Mods` directories, stored under `%LocalAppData%\PalworldServerManager\backups\<server-id>\` with a timestamp and reason in the filename.

## Restoring a backup

The server must also be stopped to restore. Before touching anything, the Manager automatically creates a **pre-restore** backup of the server's current state, so a restore is never a one-way door — if you restore the wrong file, your prior state is itself a backup you can restore back to. It then replaces `Pal/Saved` and `Mods` with the backup's contents.

## What's not covered here

A filesystem backup captures save/config/mods, not the Palworld runtime binaries themselves — a fresh install always gets those from SteamCMD. If you want a single portable file you can move to another PC or send over LAN, see [Portable packages](portable-packages.md) instead; the two features serve different purposes and use different (but similarly safe) mechanics.
