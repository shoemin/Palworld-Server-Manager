# Manager Updates

!!! success "Field status: live self-update fully validated with Palworld running"
    Palworld Server Manager can **check for**, **download**, and **install** its own updates from a genuine Setup.exe install, using [Velopack](https://velopack.io) — including restarting itself while a Palworld server keeps running. This has been field-tested end to end across two real update cycles (`v0.4.0-alpha.1 -> alpha.2` and `v0.4.0-beta.1 -> rc.1`): a real installed Manager checked for, downloaded, and installed a published update while a real Palworld dedicated server was running throughout. In both tests the Palworld Shipping process kept the **exact same process ID and start time** before and after the Manager was replaced — proof it was never restarted — and the new Manager correctly reattached to it; the second test additionally confirmed continuous, uninterrupted Shipping presence via an independent external process watcher sampling roughly every 300ms across the whole transition. No REST save/shutdown or SteamCMD update was triggered by the Manager update itself; LAN and Dashboard/REST polling resumed automatically with existing peer pairing intact, and a **Safe Stop** performed afterward on the reattached server succeeded normally. A genuinely **in-flight Manager-owned operation actively blocking Install and Restart** has also now been field-tested using a real, active LAN transfer receive: Install and Restart was directly observed enabled, then disabled while the transfer was actively in progress, then automatically re-enabled once it completed, with no reopening or manual recheck needed. Nothing on this page is available to Portable-mode users; see [Installation](../getting-started/installation.md).

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
