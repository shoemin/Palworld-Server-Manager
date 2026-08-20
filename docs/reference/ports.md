# Ports

| Port | Protocol | Purpose | Default | Notes |
|---|---|---|---|---|
| Game port | UDP | Palworld's own game traffic | `8211` | Set per-server when you create it. |
| Palworld REST API | TCP | Used by the Manager's own [Dashboard](../guide/dashboard.md) and Safe Stop | `8212` | Bound to `127.0.0.1` only — the Manager talks to it locally, never exposes it directly to remote peers. |
| Manager LAN API | TCP | Authenticated Manager-to-Manager API (pairing, remote Dashboard, transfers) | `8215` | Disabled unless you [enable LAN](../guide/lan.md). Binds for LAN reachability; not intended for Internet exposure. |
| Manager LAN discovery | UDP broadcast | Peer discovery advertisements | `8216` | Disabled unless LAN is enabled. |

Do not forward any of these ports through your router for Manager-to-Manager LAN features — they are designed for trusted local networks only. The Palworld game port itself is the only one you'd ever forward for players outside your LAN to connect to your server, which is a normal Palworld hosting consideration independent of the Manager.
