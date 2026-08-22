# Ports

| Port | Protocol | Purpose | Default | Notes |
|---|---|---|---|---|
| Game port | UDP | Palworld's own game traffic | `8211` | Set per-server when you create it. |
| Palworld REST API | TCP | Used by the Manager's own [Dashboard](../guide/dashboard.md) and Safe Stop | `8212` | The Manager itself always connects to `127.0.0.1` — see below for what that does and doesn't guarantee about Palworld's own listener. |
| Manager LAN API | TCP | Authenticated Manager-to-Manager API (pairing, remote Dashboard, transfers) | `8215` | Disabled unless you [enable LAN](../guide/lan.md). Binds for LAN reachability; not intended for Internet exposure. |
| Manager LAN discovery | UDP broadcast | Peer discovery advertisements | `8216` | Disabled unless LAN is enabled. |

Do not forward any of these ports through your router for Manager-to-Manager LAN features — they are designed for trusted local networks only. The Palworld game port itself is the only one you'd ever forward for players outside your LAN to connect to your server, which is a normal Palworld hosting consideration independent of the Manager.

## About the Palworld REST API (port 8212)

The Manager's `PalworldRestClient` always talks to Palworld's REST API at `127.0.0.1` — the Manager itself never sends REST requests anywhere else, and never exposes a passthrough to it (a remote paired peer only ever receives the sanitized `DashboardSnapshot` model over the separate, authenticated [Manager LAN API](../guide/lan.md), never a raw REST connection).

That describes the Manager's own behavior, not Palworld's. **The Manager does not configure, restrict, or otherwise control which network interfaces Palworld's own REST listener binds to** — `ConfigureManagerDefaults` enables and configures the REST API in Palworld's settings, but does not establish or guarantee a loopback-only listener. Whether port 8212 is reachable from other machines on your network (or, if forwarded, the Internet) depends on Palworld's own behavior/configuration, your Windows Firewall rules, and your router/network setup — none of which the Manager controls.

Treat TCP 8212 the same way you'd treat any admin API with a plaintext-adjacent password: **never expose it to the Internet**, and restrict it on your local network/firewall to only the machine(s) that actually need it. If you want to reach a Palworld server's dashboard from another machine, use the Manager's own paired, authenticated [LAN API](../guide/lan.md) instead of pointing anything directly at Palworld's REST port — that's the only remote-access path the Manager actually designs and secures for.
