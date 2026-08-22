# Updating while your server is running

!!! warning "Implemented, not yet field-tested against a real release"
    This describes the actual, current code behavior, verified with automated tests against a harmless synthetic process standing in for Palworld. It has **not** yet been field-tested against a real Palworld dedicated server and a real published Manager update, because no Setup.exe release exists yet to update *to*. Treat this page as accurate about what the code does, not as a field-proven guarantee.

## The guarantee

**The Manager may restart. Palworld must not.**

Installing a Manager update closes and reopens Palworld Server Manager. Any Palworld dedicated server you have running through it keeps running the entire time, completely unaffected:

- It is not saved.
- It is not shut down or force-stopped.
- It is not restarted.
- Its SteamCMD runtime is not touched.
- No backup is taken.
- Connected players are not disconnected by the Manager update itself.

None of this is conditional on whether a server happens to be running — a running server never blocks or delays installing a Manager update.

## What actually happens when you click Install and Restart

1. The Manager confirms an update is downloaded and ready, and that nothing else is blocking (see below).
2. It writes a small **runtime handoff** file recording which of your managed servers are currently running and their process identity (process ID, executable path, start time) — no passwords or tokens, ever.
3. It stops its own LAN service (the Kestrel host and peer discovery) so a paired remote Manager sees a clean disconnect rather than a dropped connection. This never touches Palworld, and never deletes your saved pairing trust.
4. It hands off to Velopack's external updater and exits.
5. Palworld's process(es) are never part of this exit — they keep running under Windows exactly as before, just without a Manager watching them for a few seconds.
6. The updated Manager starts, reads the handoff, and verifies the process identity of anything it finds still running before reattaching its lifetime monitor — the same safe matching used for a [normal Manager restart](../guide/server-lifecycle.md#manager-restart-reattachment). Your server shows **Running (monitored)** again, Start stays disabled, and Safe Stop/Force Stop work normally.

If Palworld happens to exit on its own during the brief window the Manager is restarting, the Manager reports it honestly as stopped with an unavailable exit code, rather than inventing a clean exit — see [Process reattachment](../troubleshooting/index.md#process-reattachment).

## What can block Install and Restart

A running server, by itself, never blocks installing an update. What does block it is the Manager being in the middle of one of its own operations that a sudden restart could interrupt inconsistently:

- Starting or stopping a server (safe-stop or force-stop)
- Installing, updating, or validating a server's Palworld runtime through SteamCMD
- Creating or restoring a backup
- Importing an existing (legacy) server
- Exporting or importing a `.palserver` package
- Saving changes in the settings editor
- Sending or receiving a `.palserver` package over LAN
- Another update install already in progress

If any of these are active, **Install and Restart** shows exactly which one and why, rather than silently disabling itself. Finish or cancel that operation and try again.

This is enforced at the point Install and Restart actually commits, not just by graying out a button — a new one of these operations cannot slip in during the moment between checking and committing to restart.

## LAN during an update

If you have [LAN & Transfers](../guide/lan.md) enabled and a paired remote Manager connected to yours, updating the host Manager causes a brief, expected disconnect: the remote Dashboard loses its connection while the host restarts, then reconnects on its own once the host is back up. Your paired trust is never cleared by an update. An **active `.palserver` transfer**, sending or receiving, is treated as a critical operation exactly like the others above and blocks Install and Restart until it finishes or is canceled — the Manager will not abandon a half-written transfer to apply its own update.

## If installing fails

If writing the handoff fails, or the external updater can't be launched, the Manager stays on its current version, reports the actual error, and remains fully usable — nothing is left half-applied, and Palworld was never touched regardless. You can try again.
