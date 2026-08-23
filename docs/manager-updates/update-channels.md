# Update channels

The Updates window lets you choose between two channels:

- **Stable** — only fully-released versions (tags like `v0.4.0`).
- **Prerelease** — alpha/beta/RC builds (tags like `v0.4.0-alpha.1`, `v0.4.0-beta.1`, `v0.4.0-rc.1`), for testers who deliberately opt in.

## How this is enforced

Each channel maps to a distinct Velopack release channel packaged and published by the release pipeline (`win` for Stable, `win-beta` for Prerelease), and Stable checks are additionally restricted to non-prerelease GitHub Releases. Selecting Prerelease does not retroactively make Stable installs see beta builds, and Stable never silently receives a prerelease build. The release workflow derives the channel directly from the tag's SemVer prerelease suffix, so this mapping can't drift out of sync between what gets built and what the app expects.

## Default channel

The very first time the Manager runs with no saved channel preference, it defaults to whichever channel **the installed package itself was actually built for** — installing a `v0.4.0-alpha.1` build (packaged for `win-beta`) starts you on Prerelease, not Stable, so you naturally keep receiving subsequent alpha/beta updates without having to find and flip a setting first. Once you ever explicitly choose a channel in the Updates window, that choice is remembered and always wins from then on, regardless of which package you originally installed.

!!! note "Current release state"
    `v0.4.0-alpha.1` was the first published release through this pipeline — a Prerelease on the `win-beta` channel. There is no Stable/`win` release published yet. If you're running `v0.4.0-alpha.1` (or any prerelease) and switch to the Stable channel, **Check for Updates** currently reports an error rather than a clean "up to date" result — with zero releases published on a channel, there's no release feed at all for the Manager to query, and that's surfaced the same way a network/GitHub failure would be (see [Checking for updates](checking-for-updates.md)). That error is expected given the current release state (no Stable release exists yet), not a sign anything is actually broken. See [Manager Updates](index.md) for what's field-tested so far.

## Switching channels

Changing the channel takes effect immediately and is remembered for next time (stored locally, alongside the rest of the Manager's application data — this is not a secret setting). Switching channels clears any previously-found "update available" result, since it no longer necessarily applies: you'll need to **Check for Updates** again after switching.

## No automatic downgrade

The Manager never automatically offers to install an older version than what's currently running, on either channel. If you switch from Prerelease back to Stable after installing a prerelease build that's newer than the latest stable release, checking for updates on Stable will not offer a downgrade — you would need to reinstall manually if you want to go back to a stable version.
