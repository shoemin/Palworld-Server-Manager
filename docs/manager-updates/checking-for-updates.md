# Checking for updates

!!! info
    This page covers checking and downloading. For what happens when you actually click Install and Restart, see [Updating while your server is running](update-while-server-running.md).

## Manual check

Open **Help → Check for Updates...**. This opens the Updates window and does not, by itself, start a check — click **Check for Updates** to actually query GitHub Releases.

Checking is only available when the Manager's [execution mode](index.md#execution-modes) is **Installed**. In any other mode the Check button is disabled and the window explains why.

## What a check does

The Manager queries the update feed published for the currently selected [channel](update-channels.md) against this project's public GitHub repository, anonymously — no account, no token, no sign-in. If a newer version is available for that channel, the window shows its version, download size (when known), and release notes (plain text; no HTML is rendered). If not, it reports that you're up to date.

A failed check (no network, GitHub unreachable, no matching release published yet) shows a plain-language error and leaves you able to try again immediately — there's no cooldown.

## Downloading

Once a check finds an update, **Download Update** becomes available. Downloading shows a live progress bar and can be canceled with **Cancel Download**; canceling returns you to the "update available" state so you can retry. A successful download ends in **"ready to install"**, at which point an **Install and Restart** button appears — see [Updating while your server is running](update-while-server-running.md) for what that does. Nothing is installed or applied automatically merely by downloading.

## Automatic checking

There is no periodic/automatic background check yet. Every check is user-initiated.

## What never happens during a check or download

- Your Palworld server is never saved, stopped, force-stopped, or restarted.
- SteamCMD is never invoked.
- No backup is taken.
- No runtime handoff file is written — that only happens once you actually click **Install and Restart**, see [Updating while your server is running](update-while-server-running.md).

This holds regardless of whether a managed Palworld server is running at the time.
