# Dashboard

The **Dashboard** tab shows live information about a running Palworld server, sourced from Palworld's own REST API. It works for both servers running on this PC and servers hosted by a [paired Manager on the same LAN](lan.md) — pick the source from the dropdown at the top:

```
Dashboard Source:

LOCAL
  FRANKY / PalServer
  FRANKY / Test Server

LAN
  SHOE-LAPTOP / Friends Server
```

## Requirements

The Dashboard needs the target server's Palworld REST API enabled with an AdminPassword configured, and the server must be running. If either isn't true, the Dashboard says so plainly instead of showing a blank or broken view.

## Sections

**Overview** — source machine, local/LAN status, running state, server name/version, world GUID, uptime, current/max players, server FPS and frame time, base-camp count, in-game day count, game/REST ports, and the last Manager backup timestamp if one exists.

**Players** — name, level, ping, building count, and location for each connected player. More identifying details (account name, user ID, IP) are hidden behind an **Advanced Details** toggle rather than shown by default.

**Metrics** — rolling charts for server FPS, player count, and frame time. History is sampled roughly every 5 seconds and kept for about the last hour in memory; it resets if the Manager restarts.

**Settings** — the live settings the *running* server currently reports. This is **strictly read-only**; see [Settings editor](settings-editor.md) for how the distinction from the persistent-configuration editor works. Password/token/secret-shaped values are redacted before they're ever displayed or sent anywhere.

## Refreshing

The Dashboard polls automatically (about every 5 seconds) and skips a scheduled refresh rather than overlapping if the previous one hasn't finished, so it won't pile up requests against a slow or unreachable server.
