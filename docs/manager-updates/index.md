# Manager Updates

!!! warning "Development status: implemented, not yet field-tested against a real release"
    Palworld Server Manager can **check for**, **download**, and **install** its own updates from a genuine Setup.exe install, using [Velopack](https://velopack.io) — including restarting itself while a Palworld server keeps running. The GitHub Actions release pipeline that packages and publishes both the `win` (Stable) and `win-beta` (Prerelease) channels is also implemented and locally verified end-to-end. What's still missing is an actual published release: **no Setup.exe has been published yet**, so the real "old Manager → new Manager, real Palworld server never interrupted" scenario has not been field-tested against a real update. Nothing on this page is available to current portable-ZIP users.

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
| **Portable** | A Velopack-managed portable package (not yet published) | No — would overwrite its own running executable |
| **Development** | A developer build (`bin\Debug`, `bin\Release`) or the current plain portable ZIP release | No |

Portable and Development copies show a plain-language explanation in the Updates window instead of a working Check button, and point to the GitHub Releases page for a manual download.

## Safety

Checking for or downloading a Manager update **never** touches a running Palworld server: no save, no shutdown, no force-stop, no SteamCMD update, no backup, and nothing is written to the runtime handoff state used for process reattachment. Installing an update is more involved — see [Updating while your server is running](update-while-server-running.md) for the full guarantee and what can block it.

## See also

- [Checking for updates](checking-for-updates.md)
- [Update channels](update-channels.md)
- [Updating while your server is running](update-while-server-running.md)
