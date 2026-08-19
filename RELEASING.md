# Releasing Palworld Server Manager

GitHub Actions owns the public build/release path. Do not build a public release locally and then upload the executable manually unless the automated release workflow is unavailable and the reason is documented.

## Workflows

### CI (`.github/workflows/ci.yml`)

Runs on:

- pushes to `main`
- pull requests targeting `main`
- manual workflow dispatch

It checks out the source, installs the .NET SDK selected by `global.json`, runs `scripts/build.ps1`, and preserves the timestamped build transcript as a GitHub Actions artifact even if the build fails.

### Release (`.github/workflows/release.yml`)

Runs when a release tag is pushed. It can also be manually dispatched for an **existing** tag.

The workflow:

1. validates the tag format;
2. checks out exactly that tag;
3. verifies the checked-out commit matches the tag;
4. runs the same build/self-test script used locally and by CI;
5. publishes a self-contained `win-x64` single-file application;
6. packages the EXE with `README.md`, `LICENSE`, and `CHANGELOG.md`;
7. generates `SHA256SUMS.txt`;
8. uploads the package and checksum as Actions artifacts;
9. creates the GitHub Release and attaches the same files.

No personal access token is required for a normal repository. The release workflow uses the repository's GitHub Actions token with only `contents: write` permission.

## Version/tag policy

Use SemVer-style tags prefixed with `v`.

Examples:

- `v0.2.5-beta.1` — GitHub prerelease
- `v0.2.6-rc.1` — GitHub prerelease
- `v0.3.0` — normal GitHub release
- `v1.0.0` — normal GitHub release

Any version containing a `-suffix` is automatically marked as a GitHub prerelease by the workflow.

During current field testing, prefer prerelease tags such as `v0.2.5-beta.1`. Do not publish the 0.2.x test builds as stable merely because the workflow can do so.

The release workflow passes the tag version into `dotnet publish`, so the distributed executable's version follows the tag even if the project file still contains the previous development version.

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
git tag -a v0.2.5-beta.1 -m "Palworld Server Manager v0.2.5-beta.1"
git push origin v0.2.5-beta.1
```

The tag push starts the Release workflow automatically.

When it completes, the GitHub Release should contain:

```text
PalworldServerManager-v0.2.5-beta.1-win-x64.zip
SHA256SUMS.txt
```

GitHub also exposes source-code archives automatically for the tag.

## Creating a stable release

Use a tag without a prerelease suffix only after the corresponding acceptance criteria are complete:

```powershell
git tag -a v0.3.0 -m "Palworld Server Manager v0.3.0"
git push origin v0.3.0
```

That produces a normal GitHub Release rather than a prerelease.

## Verifying a downloaded release

A user can verify the ZIP in PowerShell:

```powershell
Get-FileHash .\PalworldServerManager-v0.2.5-beta.1-win-x64.zip -Algorithm SHA256
```

Compare the reported hash with `SHA256SUMS.txt` on the GitHub Release.

## Release failure policy

A release is not valid unless all of these complete successfully:

- restore
- Release configuration build
- self-tests
- `win-x64` self-contained publish
- package creation
- checksum creation
- GitHub Release asset upload

If build or self-test fails, download the `release-build-logs-*` workflow artifact and inspect the same timestamped transcript format used during local testing.

## Code signing

The current workflow produces an unsigned Windows executable. That is acceptable for early field-test prereleases, but public distribution should eventually add Authenticode signing **before packaging and checksumming**. Signing credentials must be stored as GitHub/Azure secrets or federated identity; they must never be committed to the repository.
