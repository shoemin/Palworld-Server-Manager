# Update channels

The Updates window lets you choose between two channels:

- **Stable** (default) — only fully-released versions.
- **Prerelease** — may include beta/RC builds, for testers who deliberately opt in.

## How this is enforced

Each channel maps to a distinct Velopack release channel published alongside the build (`win` for Stable, `win-beta` for Prerelease), and Stable checks are additionally restricted to non-prerelease GitHub Releases. Selecting Prerelease does not retroactively make Stable installs see beta builds, and Stable never silently receives a prerelease build.

!!! note "Prerelease channel not yet published"
    The release pipeline does not yet package or publish a `win-beta` channel (that is a future release-integration phase). Selecting Prerelease today will simply find no available update until that exists.

## Switching channels

Changing the channel takes effect immediately and is remembered for next time (stored locally, alongside the rest of the Manager's application data — this is not a secret setting). Switching channels clears any previously-found "update available" result, since it no longer necessarily applies: you'll need to **Check for Updates** again after switching.

## No automatic downgrade

The Manager never automatically offers to install an older version than what's currently running, on either channel. If you switch from Prerelease back to Stable after installing a prerelease build that's newer than the latest stable release, checking for updates on Stable will not offer a downgrade — you would need to reinstall manually if you want to go back to a stable version.
