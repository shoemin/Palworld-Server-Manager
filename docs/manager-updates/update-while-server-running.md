# Updating while your server is running

!!! success "Field-tested against a real Palworld server and a real published update"
    This describes the actual, current code behavior. Beyond the automated tests against a synthetic stand-in process, it has now been field-tested for real: a real installed `v0.4.0-alpha.1` updated in place to the published `v0.4.0-alpha.2` while a real Palworld dedicated server stayed running throughout, with the exact same Palworld process ID and start time before and after — proof Palworld itself was never touched by the update. See [Manager Updates](index.md) for the full field-test summary. One related scenario — a genuinely in-flight Manager-owned operation actively blocking Install and Restart — has automated coverage but is still awaiting its own field pass; see the same summary for why.

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

If anything goes wrong before the external updater is actually launched — the server list can't be read, the runtime handoff can't be written, a background service doesn't stop cleanly, or the updater itself can't be started — the whole attempt is rolled back as one unit: any handoff file that was already written is discarded, the Manager's own background services (LAN, if they had been stopped) resume, and the Manager returns to its normal, fully usable state on its current version with the actual error shown. Nothing is left half-applied — not a stuck "Applying" state, not a leftover handoff file that a later restart could misread, and Palworld was never touched regardless. You can try again.

The only point that isn't undone is once the external updater has actually been launched — from there the Manager is committed to restarting.
