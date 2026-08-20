# Manager Updates

!!! warning "Development status: check and download only"
    As of this page, Palworld Server Manager can **check for** and **download** its own updates from a genuine Setup.exe install, using [Velopack](https://velopack.io). It cannot yet **install** a downloaded update or restart itself to apply one — that is still planned (a future "apply and restart" phase). There is also no published Setup.exe release yet; the installer itself is built and locally verified but not yet attached to GitHub Releases. Nothing below is available to current portable-ZIP users.

## What exists today

From **Help → Check for Updates...** in the Manager, a dedicated Updates window shows:

- Current version and [execution mode](#execution-modes)
- The selected [update channel](update-channels.md)
- When you last checked
- The available version, its size, and its release notes, once a check finds one
- The current update state

You can **Check for Updates** and **Download Update**. Downloading shows live progress and can be canceled mid-download. Once a download finishes, the window says *"Update downloaded and ready to install"* — installing and restarting the Manager to apply it is a future phase, so nothing further happens automatically or from a button here yet.

## Execution modes

Whether updating is possible at all depends on how this copy of the Manager is running:

| Mode | Meaning | Can check/download? |
|---|---|---|
| **Installed** | Installed via a genuine Velopack Setup.exe | Yes |
| **Portable** | A Velopack-managed portable package (not yet published) | No — would overwrite its own running executable |
| **Development** | A developer build (`bin\Debug`, `bin\Release`) or the current plain portable ZIP release | No |

Portable and Development copies show a plain-language explanation in the Updates window instead of a working Check button, and point to the GitHub Releases page for a manual download.

## Safety

Checking for or downloading a Manager update **never** touches a running Palworld server: no save, no shutdown, no force-stop, no SteamCMD update, no backup, and nothing is written to the runtime handoff state used for process reattachment. A Palworld server can be running the entire time you check and download. What happens to it when an update is actually *applied* is a future phase's concern — this page will be updated once that exists.

## See also

- [Checking for updates](checking-for-updates.md)
- [Update channels](update-channels.md)
