# LAN & Transfers

The **LAN & Transfers** tab lets Palworld Server Manager instances on the same private network discover each other, pair explicitly, view each other's Dashboards, and transfer servers directly.

!!! warning "LAN is disabled by default"
    You must explicitly enable it. It is designed only for trusted private/home networks — do not port-forward the Manager's LAN ports to the Internet.

## Enabling LAN

Go to **LAN & Transfers** and click **Enable LAN**. This starts:

- an authenticated Manager API on TCP `8215` (Kestrel-hosted)
- UDP peer discovery/broadcast on `8216`

See [Ports](../reference/ports.md) for the full list.

## Discovery

Once enabled, Manager instances on the same LAN broadcast a small, non-sensitive advertisement (protocol name/version, instance ID, machine name, API port, Manager version) roughly every few seconds and list each other under **Nearby Managers**. Discovery only means *a compatible Manager instance is here* — it grants no access by itself.

## Pairing

Being on the same LAN is not enough to access another Manager. Pairing is explicit:

1. On the host machine, click **Generate Code**. You get a random 6-digit code that expires in 5 minutes and can only be used once.
2. On the other machine, select the host in **Nearby Managers**, click **Pair Selected**, and enter the code.
3. Ten wrong attempts locks out that code. A successful pairing exchanges a random bearer token in both directions — your Palworld AdminPassword is **never** part of this exchange or transmitted to the peer at all.

Either side can **Unpair** later to revoke access.

## Remote Dashboard

Once paired, a peer's managed servers appear under the **LAN** section of the [Dashboard](dashboard.md) source dropdown, using the exact same Overview/Players/Metrics/read-only-Settings views as a local server. The remote Manager talks to its own local Palworld REST API and only ever sends you the sanitized result — your paired peer's Palworld credentials never leave their machine, and the remote Dashboard is fully read-only: there is no remote settings editing, shutdown, kick, or ban in this version.

## Sending a server to another PC

From **Servers → select a server → Send to PC**, choose a paired destination. If the server is running, it's saved and gracefully stopped first — a running server is never copied live. The Manager then:

1. Exports the server as a `.palserver` package (see [Portable packages](portable-packages.md)) and computes its SHA-256.
2. Sends a transfer **offer** (name, size, expected hash) to the destination — nothing is uploaded yet.
3. The destination must **explicitly accept or reject**. Nothing is ever imported automatically just because a peer sent something.
4. Once accepted, the package streams to a `.partial` file. Only after the byte count and SHA-256 both verify does it get renamed to a real `.palserver` file — a failed or cancelled transfer never leaves a usable package behind.
5. From **LAN & Transfers** on the receiving side, choose **Import** to hand the verified package to the same importer used for any other `.palserver` file, which performs its own internal per-file hash verification and installs a fresh runtime.

Transfer progress (bytes/percentage/state) is shown live and can be cancelled.
