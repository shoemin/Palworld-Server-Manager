# Release process

## CI

`.github/workflows/ci.yml` runs on every push/PR to `main`: checks out, sets up the .NET SDK from `global.json`, and runs `scripts\build.ps1` (restore + Release build + self-tests), uploading the build transcript as an artifact.

## Releases

`.github/workflows/release.yml` builds and publishes a tagged release end to end. It triggers on `v*` tags (or a manual `workflow_dispatch` naming an existing tag), and:

1. Validates the tag is SemVer-like (`v0.4.0`, `v0.4.0-alpha.1`, `v0.4.0-beta.1`, `v0.4.0-rc.1`, ...) and derives the package version, whether it's a GitHub prerelease, and the Velopack **channel**: any prerelease suffix maps to `win-beta`, otherwise `win`. This must exactly match the channel names `VelopackUpdateBackend` expects — see [Update channels](../manager-updates/update-channels.md) — so a stable tag can never accidentally pack to `win-beta` or vice versa.
2. Checks out the exact tagged commit and verifies `git rev-list` for the tag matches the checked-out `HEAD` before doing anything else, so a release always builds the commit the tag actually points to.
3. Restores the pinned `.config/dotnet-tools.json` tools (`vpk` 1.2.0), runs `scripts\build.ps1` (full build + all self-tests), and runs `mkdocs build --strict` as an additional release-qualifying gate — a release does not get built from a state where the docs site itself is broken.
4. Publishes a self-contained win-x64 build (**not** single-file — Velopack needs individual files for delta diffing and its own bootstrap shim).
5. Best-effort downloads the previous release for the same channel (`vpk download github`) into the same output directory, so `vpk pack`'s default delta mode can generate a delta package against it automatically. This is allowed to find nothing (a fresh channel, or the very first release) without failing the workflow.
6. Packs the release (`vpk pack`) with generated release notes (an experimental-build banner is prepended for prereleases), producing the Setup.exe, full (and delta, if applicable) nupkg, portable zip, and channel feed JSON.
7. Verifies every required asset actually exists on disk before going any further (fails closed rather than silently publishing a partial release).
8. Generates `SHA256SUMS.txt` over every packaged file.
9. Uploads everything as a workflow-run artifact and the build transcript as a separate artifact.
10. Publishes the release.

### Single release authority

`vpk upload github` is the **only** thing that creates or updates the GitHub Release itself. It uploads the Setup.exe, nupkg(s), portable zip, and channel feed JSON, and is called with `--merge` so re-running an existing tag adds to that same release instead of a second tool racing to create a duplicate. `SHA256SUMS.txt` isn't a Velopack asset, so it's attached afterward with a plain `gh release upload --clobber` against the release `vpk` just published — one authority creates the release, a simple follow-up adds the one extra file. There is deliberately no separate `gh release create` call anywhere in this workflow.

### Installer packaging (Velopack)

The `Velopack` NuGet package (pinned to `1.2.0` in `PalworldServerManager.App.csproj`) and the matching `vpk` CLI (pinned to `1.2.0` via the local tool manifest, `.config/dotnet-tools.json`) produce the per-user Windows installer described above.

Local packaging (what the release workflow itself runs, minus the actual upload):

```powershell
dotnet publish src\PalworldServerManager.App\PalworldServerManager.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:Version=<version> -o <publish-dir>
dotnet vpk download github --repoUrl https://github.com/shoemin/Palworld-Server-Manager --channel <win|win-beta> --outputDir <releases-dir> --token <token>   # best-effort, for deltas
dotnet vpk pack `
  --packId ShoeMin.PalworldServerManager `
  --packVersion <version> `
  --channel <win|win-beta> `
  --packDir <publish-dir> `
  --packTitle "Palworld Server Manager" `
  --packAuthors shoemin `
  --mainExe PalworldServerManager.exe `
  --runtime win-x64 `
  --instLocation PerUser `
  --releaseNotes <notes.md> `
  --outputDir <releases-dir>
```

This produces `ShoeMin.PalworldServerManager-<channel>-Setup.exe`, a `ShoeMin.PalworldServerManager-<version>-<channel>-full.nupkg`, `ShoeMin.PalworldServerManager-<channel>-Portable.zip`, and `releases.<channel>.json`/`assets.<channel>.json` metadata — note the asset filenames themselves are channel-suffixed (`-win-beta-Setup.exe` for a prerelease, not `-win-Setup.exe`), confirmed against a real local `vpk pack --channel win-beta` run before trusting it in CI. The pack ID is deliberately `ShoeMin.PalworldServerManager`, not `PalworldServerManager`, so the installer's default per-user install location — verified with a real install/uninstall cycle — is `%LocalAppData%\ShoeMin.PalworldServerManager\`, which can never collide with the persistent data root at `%LocalAppData%\PalworldServerManager\`.

**No release has actually been published through this pipeline yet** — it is implemented and locally verified (including a real, read-only `vpk download github` check against this repository confirming graceful "no previous release" handling), but publishing the first one is a separate, deliberate action.

## In-app update checking and installing (Velopack)

`ApplicationUpdateService` (in `PalworldServerManager.Core.Services.Update`) checks for, downloads, and applies updates through `VelopackUpdateBackend`, which wraps Velopack's `UpdateManager`/`GithubSource` against this repository's public GitHub Releases, anonymously. It is built behind `IApplicationUpdateBackend` specifically so the state machine, channel handling, and concurrency logic are unit-testable with a fake backend — self-tests never call GitHub or require a real Velopack install.

Applying an update writes the `RuntimeHandoffService` handoff itself, then hands off to Velopack's external updater and exits — the restarted Manager reattaches to any Palworld server that kept running using the same reconciliation path a normal Manager restart uses. See [architecture](architecture.md#manager-self-update) for the full apply sequence and the `CriticalOperationTracker` gating that stops this from interrupting another Manager-owned operation. Execution-mode detection (Installed / Portable / Development) uses Velopack's own `IsInstalled`/`IsPortable` locator properties, falling back to a narrow sibling-`.csproj` check (not just a loose path guess) to distinguish a developer build from the current non-Velopack portable ZIP when neither Velopack flag is set.

The first time the Manager ever runs with no saved channel preference, `IApplicationUpdateBackend.InstalledChannel` (read from Velopack's ambient `VelopackLocator.Current.Channel`, set process-wide by `VelopackApp.Build().Run()` at startup) seeds the *initial* preference — a package built for `win-beta` starts on Prerelease rather than silently defaulting everyone to Stable and never surfacing its own subsequent alpha/beta updates. Any preference the user has ever explicitly saved always takes priority over this from then on.

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
