# Troubleshooting

## SteamCMD

**A provision/update fails with exit code 7.** This is a field-observed SteamCMD failure mode usually tied to the Steam desktop client's state. The Manager detects it and offers an interactive recovery: launch Steam, continue anonymously, or cancel — then retry the operation once after choosing.

**Steam isn't running when I try to create/update a server.** The Manager will offer to launch it, continue with anonymous SteamCMD, or cancel. Note that the Manager cannot reliably verify your Steam account's ownership/sign-in state — that check isn't an official requirement for downloading the Palworld dedicated server files anyway.

## REST API / Dashboard

**Dashboard shows "REST API is disabled" or "AdminPassword is not configured."** Enable `RESTAPIEnabled` and set an `AdminPassword` for that server in the [Settings editor](../guide/settings-editor.md), then restart the server.

**Dashboard shows "Server is not running."** The Dashboard only has live data while Palworld is actually running — this isn't an error.

**Safe Stop fails / is unavailable.** Safe Stop needs the same REST API + AdminPassword configuration as the Dashboard. Without it, use Force Stop after saving in-game manually.

## LAN discovery and pairing

**Peers aren't showing up.** Confirm LAN is enabled on **both** machines, and that Windows Firewall allowed the app on your private network profile when first prompted (the first LAN enable may trigger that prompt). Peer entries also expire after a short period without a fresh advertisement, so a peer that just enabled LAN can take a few seconds to appear.

**Pairing code rejected.** Codes are one-use and expire after 5 minutes — generate a fresh one. Ten wrong attempts locks out the current code entirely, even if you then enter the right one.

## Transfers

**Transfer failed.** A failed or interrupted transfer never leaves a usable package — no partial data is imported. Retry the transfer, or delete the leftover partial file from **LAN & Transfers** and try again. Resuming a partial transfer isn't supported yet.

**"Received package SHA-256 did not match."** The transfer's whole-file integrity check failed — this can happen from a bad network path or, rarely, real tampering. The package was not saved as a valid `.palserver` file; nothing was imported.

## Process reattachment

**A managed server shows "Running (external)" instead of "Running (monitored)."** This means the Manager sees the process but hasn't (yet) reattached its full lifetime monitor to it — this can briefly happen right after the Manager restarts, before reconciliation finishes. It resolves on its own within a moment; Start still correctly stays disabled either way.

**Status shows "exit code unavailable — Manager was restarting."** Palworld exited during the brief window the Manager itself was restarting, so no exit code could be recovered. This is reported honestly rather than as a fabricated clean stop — check `logs\` for what was happening around that time if you need more context.

## Manager updates

**Check for Updates is grayed out / says self-update is disabled.** Update checking only works from a genuine Setup.exe install. A developer build or a Portable-mode copy will say so explicitly instead of pretending to check — see [Manager Updates](../manager-updates/index.md#execution-modes).

**Check for Updates says it's up to date, but I expected a newer version.** A few things to check: confirm the [execution mode](../manager-updates/index.md#execution-modes) is actually **Installed**, not Portable or Development (those can't check at all). Confirm which [channel](../manager-updates/update-channels.md) is selected — **Prerelease** maps to the `win-beta` Velopack channel, **Stable** maps to `win`. Prerelease releases exist today; Stable currently has no published release yet, so Stable will correctly report nothing available until one exists — that's expected, not a bug. The Manager also never offers the version you're currently running as an "update," and it never automatically offers to downgrade to an older version on either channel — see [No automatic downgrade](../manager-updates/update-channels.md#no-automatic-downgrade).

**A check fails with a network/GitHub error.** Update checks are anonymous public requests to GitHub; a failure here doesn't affect anything else in the Manager, and you can retry immediately with no cooldown.

**Install and Restart is disabled / explains it's blocked by something.** A running Palworld server never blocks this by itself. What does block it is an active Manager operation — starting/stopping a server, a SteamCMD install/update, a backup/restore, a legacy import, a `.palserver` export/import, a settings save, or an active LAN transfer. The message names exactly which one; finish or cancel it and try again. See [Updating while your server is running](../manager-updates/update-while-server-running.md).

**Install and Restart failed.** The Manager stays on its current version and Palworld is unaffected either way — check the error message and `logs\` for the specific cause (a handoff write failure or an updater launch failure are the two points where this can fail), then retry.

## Diagnostics

If something doesn't fit the cases above, use **Export Diagnostic Bundle** from the main window. It collects recent Manager/server logs, runtime metadata, and sanitized settings — it never includes `.sav` save files, and passwords/tokens are redacted before anything is written.
