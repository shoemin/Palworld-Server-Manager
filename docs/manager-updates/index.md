# Manager Updates

!!! warning "Field status: installed-mode baseline verified; live self-update still pending"
    Palworld Server Manager can **check for**, **download**, and **install** its own updates from a genuine Setup.exe install, using [Velopack](https://velopack.io) — including restarting itself while a Palworld server keeps running. `v0.4.0-alpha.1` is a real published Prerelease (`win-beta` channel) with a working Setup.exe, and a real installed-mode field test against that public build has verified: installation, Installed execution mode, persistent profile/data preservation across install, correct Prerelease/`win-beta` channel selection, update checks against the live public feed, real Palworld start/monitoring, Manager restart while Palworld keeps running with correct process reattachment, graceful REST save/shutdown, and detection of an externally terminated/crashed Palworld process. **What's still pending** is the actual release-to-release scenario — checking, downloading, and installing a real update from a running `v0.4.0-alpha.1` to a newer published release while a real Palworld server stays running throughout — which requires a second published release and hasn't happened yet. Nothing on this page is available to Portable-mode users; see [Installation](../getting-started/installation.md).

## What exists today

From **Help → Check for Updates...** in the Manager, a dedicated Updates window shows:

- Current version and [execution mode](#execution-modes)
- The selected [update channel](update-channels.md)
- When you last checked
- The available version, its size, and its release notes, once a check finds one
- The current update state

You can **Check for Updates** and **Download Update**, with live download progress and cancellation. Once a download finishes, an **Install and Restart** button becomes available — see [Updating while your server is running](update-while-server-running.md) for exactly what that does and does not do.

## Execution modes

Whether updating is possible at all depends on how this copy of the Manager is running:

| Mode | Meaning | Can check/download/install? |
|---|---|---|
| **Installed** | Installed via a genuine Velopack Setup.exe | Yes |
| **Portable** | The published portable package, run directly from an extracted folder | No — would overwrite its own running executable; download a newer portable package manually instead |
| **Development** | A developer build (`bin\Debug`, `bin\Release`) | No |

Portable and Development copies show a plain-language explanation in the Updates window instead of a working Check button, and point to the GitHub Releases page for a manual download.

## Safety

Checking for or downloading a Manager update **never** touches a running Palworld server: no save, no shutdown, no force-stop, no SteamCMD update, no backup, and nothing is written to the runtime handoff state used for process reattachment. Installing an update is more involved — see [Updating while your server is running](update-while-server-running.md) for the full guarantee and what can block it.

## See also

- [Checking for updates](checking-for-updates.md)
- [Update channels](update-channels.md)
- [Updating while your server is running](update-while-server-running.md)
