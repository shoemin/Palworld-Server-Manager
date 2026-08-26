# Releasing Palworld Server Manager

GitHub Actions owns the public build/release path. Do not build a public release locally and then upload artifacts manually unless the automated release workflow is unavailable and the reason is documented.

This file is a quick-reference summary for maintainers cutting a release. For the full mechanism — exact checksum-manifest scope, canonical LICENSE-byte verification, `--pre` prerelease-retrieval handling, and 403/recovery troubleshooting — see [`docs/developer/release-process.md`](docs/developer/release-process.md), which is the source of truth if this file and that page ever disagree.

## Workflows

### CI (`.github/workflows/ci.yml`)

Runs on:

- pushes to `main`
- pull requests targeting `main`
- manual workflow dispatch

It checks out the source, installs the .NET SDK selected by `global.json`, runs `scripts\build.ps1`, and preserves the timestamped build transcript as a GitHub Actions artifact even if the build fails.

### Release (`.github/workflows/release.yml`)

Runs when a `v*` tag is pushed. It can also be manually dispatched for an **existing** tag (used for recovery — see [Recovering from a failed release](docs/developer/release-process.md#recovering-from-a-failed-release)).

Summary of the pipeline (full detail in `docs/developer/release-process.md`):

1. Validates the tag is SemVer-like and derives the version, whether it's a GitHub prerelease, and the Velopack channel (`win-beta` for any prerelease suffix, `win` for a stable tag).
2. Checks out exactly that tag and verifies the checkout matches it.
3. Restores pinned `.config/dotnet-tools.json` tools (`vpk` 1.2.0), runs `scripts\build.ps1` (full build + self-tests), and runs `mkdocs build --strict` as a release-qualifying gate.
4. Publishes a self-contained `win-x64` build (**not** single-file — Velopack needs individual files for delta diffing and its own bootstrap shim).
5. Verifies the checked-out `LICENSE` is byte-identical to the canonical Git blob committed in the tag (`.gitattributes` pins deterministic LF checkout; the workflow independently proves it with `git hash-object --no-filters` rather than trusting the attribute alone), then copies it into the publish output.
6. Best-effort downloads the previous release on the same channel (`vpk download github`, with `--pre` for prerelease channels) so `vpk pack` can generate a delta package against it.
7. Packs the release with `vpk pack`, producing `Setup.exe`, the full (and delta, if applicable) `nupkg`, the portable zip, and the channel feed JSON.
8. Verifies LICENSE bytes inside the packaged archives and via a real silent install/uninstall cycle.
9. Publishes the release with `vpk upload github` — the **sole** authority that creates or updates the GitHub Release.
10. Only after publication succeeds: downloads every intended public asset back from the real GitHub Release, generates `SHA256SUMS.txt` from those downloaded bytes (never the local packaging-stage copies), uploads it, then re-downloads the published manifest and independently re-verifies it. This two-phase design exists because `v0.4.0-alpha.2` proved that `vpk upload github` can regenerate a channel feed file's bytes in-memory at publish time rather than uploading the local on-disk copy verbatim — see [Public checksum manifest scope](docs/developer/release-process.md#public-checksum-manifest-scope).

No personal access token is required for a normal repository. The release workflow uses the repository's GitHub Actions token with only `contents: write` permission — see [Permissions stay least-privilege](docs/developer/release-process.md#permissions-stay-least-privilege).

## Version/tag policy

Use SemVer-style tags prefixed with `v`, matching this project's actual convention:

- `v0.4.0-alpha.1`, `v0.4.0-beta.1` — GitHub prereleases, Velopack channel `win-beta`
- `v0.4.0-rc.1` — GitHub prerelease, Velopack channel `win-beta`
- `v0.4.0`, `v1.0.0` — normal GitHub releases, Velopack channel `win`

Any version containing a `-suffix` is automatically marked as a GitHub prerelease and packaged to `win-beta`; a suffix-free version is a normal release packaged to `win`. This mapping is derived directly from the tag by the workflow, so it can't drift out of sync with what `VelopackUpdateBackend` expects on the app side.

During current field testing, prefer prerelease tags. Do not publish a Stable/`win` tag merely because the workflow can do so — publish Stable only once the corresponding acceptance criteria for that milestone are actually complete.

The release workflow passes the tag version into `dotnet publish`, so the distributed executable's version follows the tag even if the project files still contain a different development version.

## First repository setup

1. Create the GitHub repository.
2. Add this source tree so `PalworldServerManager.sln`, `global.json`, and `.github/` are at the repository root.
3. Push `main`.
4. Open the repository's **Actions** tab and verify the **CI** workflow passes.
5. Under repository rules/rulesets, protect `main` from force pushes. If using pull requests, require the `Build and self-test` status check before merge.
6. Keep Actions token permissions at the minimum practical level. CI requests only `contents: read`; the release workflow alone requests `contents: write` to create release assets.

If an organization policy blocks the release workflow from creating releases, inspect **Settings → Actions → General → Workflow permissions** or the organization's Actions policy.

## Creating a prerelease

From a clean `main` checkout after CI passes:

```powershell
git pull --ff-only
git status
git tag -a v0.4.0-beta.2 -m "Palworld Server Manager v0.4.0-beta.2"
git push origin v0.4.0-beta.2
```

The tag push starts the Release workflow automatically. Tags are immutable once pushed — never move, delete, or recreate one; see [Recovering from a failed release](docs/developer/release-process.md#recovering-from-a-failed-release) if the workflow itself fails.

When it completes, the GitHub Release should contain (for a prerelease, channel `win-beta`):

```text
ShoeMin.PalworldServerManager-win-beta-Setup.exe
ShoeMin.PalworldServerManager-win-beta-Portable.zip
ShoeMin.PalworldServerManager-<version>-win-beta-full.nupkg
ShoeMin.PalworldServerManager-<version>-win-beta-delta.nupkg   (if the previous release on this channel was successfully retrieved — that download is best-effort, so a valid release can still complete as full-only even when a previous release exists)
releases.win-beta.json
SHA256SUMS.txt
```

GitHub also exposes source-code archives automatically for the tag.

## Creating a stable release

Use a tag without a prerelease suffix only after the corresponding acceptance criteria are complete:

```powershell
git tag -a v0.4.0 -m "Palworld Server Manager v0.4.0"
git push origin v0.4.0
```

That produces a normal GitHub Release (Velopack channel `win`) rather than a prerelease.

## Verifying a downloaded release

Download `SHA256SUMS.txt` from the GitHub Release alongside the asset you downloaded, then in PowerShell:

```powershell
Get-FileHash .\ShoeMin.PalworldServerManager-win-beta-Setup.exe -Algorithm SHA256
```

Compare the reported hash against the matching line in `SHA256SUMS.txt`. That manifest is generated from the actual published Release bytes (not local build artifacts), so it should always match exactly.

## Release failure policy

A release is not valid unless all of these complete successfully:

- restore
- Release configuration build and self-tests
- documentation strict build
- `win-x64` self-contained publish
- canonical LICENSE byte verification
- Velopack packaging (`vpk pack`)
- Velopack publication (`vpk upload github`)
- post-publication checksum generation and re-verification against the real Release

If build or self-test fails, download the `release-build-logs-*` workflow artifact and inspect the same timestamped transcript format used during local testing.

## Code signing

The current workflow produces an unsigned Windows executable. That is acceptable for early field-test prereleases, but public distribution should eventually add Authenticode signing **before packaging and checksumming**. Signing credentials must be stored as GitHub/Azure secrets or federated identity; they must never be committed to the repository.
