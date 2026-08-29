# Update channels

The Updates window lets you choose between two channels:

- **Stable** — only fully-released versions (tags like `v0.4.0`).
- **Prerelease** — alpha/beta/RC builds (tags like `v0.4.0-alpha.1`, `v0.4.0-beta.1`, `v0.4.0-rc.1`), for testers who deliberately opt in.

## How this is enforced

Each channel maps to a distinct Velopack release channel packaged and published by the release pipeline (`win` for Stable, `win-beta` for Prerelease), and Stable checks are additionally restricted to non-prerelease GitHub Releases. Selecting Prerelease does not retroactively make Stable installs see beta builds, and Stable never silently receives a prerelease build. The release workflow derives the channel directly from the tag's SemVer prerelease suffix, so this mapping can't drift out of sync between what gets built and what the app expects.

## Default channel

The very first time the Manager runs with no saved channel preference, it defaults to whichever channel **the installed package itself was actually built for** — installing a `v0.4.0-alpha.1` build (packaged for `win-beta`) starts you on Prerelease, not Stable, so you naturally keep receiving subsequent alpha/beta updates without having to find and flip a setting first. Once you ever explicitly choose a channel in the Updates window, that choice is remembered and always wins from then on, regardless of which package you originally installed.

!!! note "If a channel has no published releases yet"
    `v0.4.0-alpha.1` was the first release ever published through this pipeline, on the Prerelease/`win-beta` channel. If you ever switch to a channel that currently has zero releases published on it — for example, Stable/`win` before its own first release exists — **Check for Updates** reports an error rather than a clean "up to date" result: with no release feed to query at all, that's surfaced the same way a network/GitHub failure would be (see [Checking for updates](checking-for-updates.md)). That's expected behavior for an empty channel, not a sign anything is broken. See [Manager Updates](index.md) for what's field-tested.

## Switching channels

Changing the channel takes effect immediately and is remembered for next time (stored locally, alongside the rest of the Manager's application data — this is not a secret setting). Switching channels clears any previously-found "update available" result, since it no longer necessarily applies: you'll need to **Check for Updates** again after switching.

Moving from a prerelease build to the matching Stable release (for example, from a `0.4.0-rc.1` prerelease install to the `0.4.0` Stable release once it's published) is a normal, one-time channel switch: select **Stable** in the Updates window, then **Check for Updates** — it is never offered automatically while Prerelease stays selected. This is treated as a genuine upgrade, not a downgrade: a release without a prerelease suffix always outranks its own prerelease builds under SemVer ordering (`0.4.0` > `0.4.0-rc.1`), regardless of which one you happen to be running when you switch.

## No automatic downgrade

The Manager never automatically offers to install an older version than what's currently running, on either channel. If you switch from Prerelease back to Stable after installing a prerelease build that's newer than the latest stable release, checking for updates on Stable will not offer a downgrade — you would need to reinstall manually if you want to go back to a stable version.
