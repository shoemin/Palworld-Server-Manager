# Release process

## CI

`.github/workflows/ci.yml` runs on every push/PR to `main`: checks out, sets up the .NET SDK from `global.json`, and runs `scripts\build.ps1` (restore + Release build + self-tests), uploading the build transcript as an artifact.

## Releases (current state)

`.github/workflows/release.yml` builds a tagged release: exact tag checkout, version validation, restore, Release build, self-tests, a self-contained win-x64 single-file publish, packaging into a `PalworldServerManager-vX.Y.Z-win-x64.zip`, a `SHA256SUMS.txt` checksum file, and creation of the GitHub Release with those assets attached.

## Installer packaging (Velopack)

The `Velopack` NuGet package (pinned to `1.2.0` in `PalworldServerManager.App.csproj`) and the matching `vpk` CLI (pinned to `1.2.0` via the local tool manifest, `.config/dotnet-tools.json`) produce a per-user Windows installer. This is implemented and has been verified locally, but is **not yet wired into `release.yml`** — that's a separate, not-yet-done step (in-app update checking/downloading is also not implemented yet).

Local packaging:

```powershell
dotnet publish src\PalworldServerManager.App\PalworldServerManager.App.csproj -c Release -r win-x64 --self-contained true -o <publish-dir>
dotnet vpk pack `
  --packId ShoeMin.PalworldServerManager `
  --packVersion <version> `
  --packDir <publish-dir> `
  --packTitle "Palworld Server Manager" `
  --packAuthors shoemin `
  --mainExe PalworldServerManager.exe `
  --runtime win-x64 `
  --instLocation PerUser `
  --outputDir <releases-dir>
```

This produces `ShoeMin.PalworldServerManager-win-Setup.exe`, a `-full.nupkg` update package, a Velopack-native portable zip, and `releases.win.json`/`assets.win.json` metadata. The pack ID is deliberately `ShoeMin.PalworldServerManager`, not `PalworldServerManager`, so the installer's default per-user install location — verified with a real install/uninstall cycle — is `%LocalAppData%\ShoeMin.PalworldServerManager\`, which can never collide with the persistent data root at `%LocalAppData%\PalworldServerManager\`.

`vpk pack` is not yet invoked from `.github/workflows/release.yml`; that integration, along with attaching these assets to GitHub Releases and publishing an actual `win-beta` prerelease channel, is future work.

## In-app update checking (Velopack)

`ApplicationUpdateService` (in `PalworldServerManager.Core.Services.Update`) checks for and downloads updates through `VelopackUpdateBackend`, which wraps Velopack's `UpdateManager`/`GithubSource` against this repository's public GitHub Releases, anonymously. It is built behind `IApplicationUpdateBackend` specifically so the state machine, channel handling, and concurrency logic are unit-testable with a fake backend — self-tests never call GitHub or require a real Velopack install.

This currently stops at a "ready to install" state; it does not apply an update or restart the Manager (that is a future phase, which will use the already-implemented `RuntimeHandoffService` to let the restarted Manager reattach to any Palworld server that kept running). Execution-mode detection (Installed / Portable / Development) uses Velopack's own `IsInstalled`/`IsPortable` locator properties, falling back to a narrow sibling-`.csproj` check (not just a loose path guess) to distinguish a developer build from the current non-Velopack portable ZIP when neither Velopack flag is set.

## Versioning

Project version is kept consistent across `PalworldServerManager.Core`, `.App`, `.Lan`, and `.SelfTest` `.csproj` files. The version is only bumped once an implementation milestone is actually complete and verified — not at the start of work on it.

## Documentation site

Documentation (this site) builds via `.github/workflows/docs.yml`: pull requests get a `mkdocs build --strict` check only; pushes to `main` build and deploy to GitHub Pages. See [Documentation local development](#local-documentation-development) below.

### Local documentation development

```powershell
python -m venv .venv-docs
.venv-docs\Scripts\Activate.ps1
pip install -r docs\requirements.txt
mkdocs serve
```

Before committing docs changes, run the same strict check CI runs:

```powershell
mkdocs build --strict
```
