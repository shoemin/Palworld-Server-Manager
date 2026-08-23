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
6. Copies the repository `LICENSE` into the publish output before packing (byte-for-byte, verified with a hash check) so every packaged distributable carries the PolyForm Noncommercial License terms and the required copyright notice, not just the source repository.
7. Packs the release (`vpk pack`) with generated release notes (an experimental-build banner is prepended for prereleases), producing the Setup.exe, full (and delta, if applicable) nupkg, portable zip, and channel feed JSON.
8. Verifies every required asset actually exists on disk, that the checked-out `LICENSE` is byte-identical to the canonical Git blob committed in the release tag, that the same bytes are actually present inside the portable zip and full nupkg contents (compared by SHA-256 of the archive entry, not just entry presence), and that a real silent install of the freshly-built Setup.exe places those same byte-identical bytes in the installed application directory (then cleanly uninstalls itself again) — fails closed rather than silently publishing a partial, license-missing, or line-ending-corrupted release. See [Canonical LICENSE bytes](#canonical-license-bytes) below.
9. Generates `SHA256SUMS.txt` over exactly the files that will actually become public GitHub Release assets — see [Public checksum manifest scope](#public-checksum-manifest-scope) below.
10. Uploads everything as a workflow-run artifact and the build transcript as a separate artifact.
11. Publishes the release.

### Single release authority

`vpk upload github` is the **only** thing that creates or updates the GitHub Release itself. It uploads the Setup.exe, nupkg(s), portable zip, and channel feed JSON, and is called with `--merge` so re-running an existing tag adds to that same release instead of a second tool racing to create a duplicate. `SHA256SUMS.txt` isn't a Velopack asset, so it's attached afterward with a plain `gh release upload --clobber` against the release `vpk` just published — one authority creates the release, a simple follow-up adds the one extra file. There is deliberately no separate `gh release create` call anywhere in this workflow.

### Permissions stay least-privilege

The Release workflow declares only `permissions: contents: write` at the top level — it does not need, and should never be given, broader access. The repository's own default Actions token permission can (and should) stay at its restricted default; nothing about this workflow requires raising it.

This matters because a real publication attempt once failed with `403 Resource not accessible by integration` from GitHub's Create Release API, and it was tempting to misdiagnose that as "the repository default token is read-only." It wasn't: the failing job's own `GITHUB_TOKEN Permissions` log block showed `Contents: write` was actually granted. The real cause was GitHub's Create Release endpoint requiring **workflow-modification authorization** — which the built-in `GITHUB_TOKEN` can never be granted, by design — whenever the `target_commitish` passed to it identifies a commit whose `.github/workflows/` content differs from the repository's default branch. Passing `v0.4.0-alpha.1`'s original tagged commit as `target_commitish` after `main`'s own copy of `release.yml` had since been corrected triggered exactly that guard. Broadening the repository's default workflow permissions would not have fixed this either, since the guard isn't about `contents` scope at all, and `GITHUB_TOKEN` structurally cannot receive `workflows` write access no matter how it's configured.

The fix was narrower: GitHub's Create Release API documents `target_commitish` as "Unused if the Git tag already exists," so once a tag already exists, omitting `target_commitish` avoids the guard entirely rather than needing to work around it.

### Recovering from a failed release

Release tags (`v*`) are immutable once pushed — a failed or partial publication is never a reason to move, delete, or recreate one. `vpk upload github` is the only step that actually creates or updates the GitHub Release; if the workflow fails before that step runs, no Release or assets exist yet, so there is nothing to roll back.

If the failure can be corrected entirely within the Release workflow *definition* itself (`.github/workflows/release.yml`), fix it through a normal PR to `main`, exactly like any other change — do not touch the tag. Once merged, manually dispatch the corrected workflow against the existing tag:

```powershell
gh workflow run release.yml --ref main -f tag=v0.4.0-alpha.1
```

`--ref main` selects the corrected workflow *definition* from `main`, but the job itself still checks out and packages `refs/tags/v0.4.0-alpha.1` as the product source — so the released product remains exactly the commit the tag pointed to when it was created. Every other repository file the workflow references after that checkout (`scripts/build.ps1`, `docs/requirements.txt`, project files, and any other supporting script) is the version stored in the *tagged commit*, not `main` — this recovery procedure does not pick those up. A failure whose real fix lives outside `.github/workflows/release.yml` itself needs a different recovery decision, not this one.

### Public checksum manifest scope

`SHA256SUMS.txt` is generated from an explicit, allow-listed set of filenames — never from a blind listing of everything `vpk pack` happened to write to its output directory. That set is derived by mirroring pinned Velopack 1.2.0's actual GitHub upload behavior (audited directly from its source at tag `1.2.0`: `src/vpk/Velopack.Deployment/GitHubRepository.cs`, `src/vpk/Velopack.Core/DefaultName.cs`, `src/lib-csharp/Util/CoreUtil.cs`), so the manifest can only ever describe a file a consumer can actually download from the release:

- `<PackId>-<channel>-Setup.exe` and `<PackId>-<channel>-Portable.zip` — always channel-suffixed, even for the default `win` channel.
- The full nupkg (and delta nupkg, if one was generated) — the exact filename is constructed from the release version and channel (`<PackId>-<version>-full.nupkg` for `win`, `<PackId>-<version>-<channel>-full.nupkg` for every other channel, per `DefaultName.GetSuggestedReleaseName`'s Windows-default-channel suffix rule), not discovered by globbing the output directory. A glob is unsafe here: "Download previous release of this channel" deliberately leaves the *previous* same-channel release's full nupkg in this same directory for delta diffing, so on every release after a channel's first, a glob would match two files.
- `releases.<channel>.json` — always.
- A legacy file named exactly `RELEASES` (no channel suffix) — **only** when publishing to the default `win` channel. `CoreUtil.GetReleasesFileName` special-cases `win` the same way on the local side: for `win`, the file `vpk pack` writes locally is *also* the unsuffixed `RELEASES` (identical to what gets uploaded); for every other channel, `vpk pack` writes a channel-suffixed `RELEASES-<channel>` file locally for its own bookkeeping, but that suffixed file is never uploaded for any channel.

`assets.<channel>.json` is local-only build bookkeeping (`BuildAssets.Write`) and is never uploaded for any channel, so it's deliberately excluded rather than filtered after the fact. The workflow fails closed if an intended public asset is missing before generating the manifest, and the manifest itself is deduplicated and sorted for deterministic output.

v0.4.0-alpha.1's published `SHA256SUMS.txt` was generated by the older blind-directory-listing approach and lists `assets.win-beta.json` and `RELEASES-win-beta` as if they were downloadable — they were never actually uploaded (expected Velopack behavior, not a defect in what got published), but the checksum manifest describing them was misleading. alpha.1 is immutable and was not corrected retroactively; this is fixed starting with alpha.2.

### Canonical LICENSE bytes

The release workflow proves — not just asserts — that every `LICENSE` copy it ships is byte-identical to the `LICENSE` blob actually committed in the release tag:

1. Repository-root `.gitattributes` declares `LICENSE text eol=lf`, so checkout is deterministic regardless of a runner's `core.autocrlf` setting.
2. Before `LICENSE` is copied into the publish directory, the workflow independently verifies this rather than trusting the attribute alone: it resolves the committed blob with `git rev-parse HEAD:LICENSE`, computes the blob ID of the **raw** working-tree bytes with `git hash-object --no-filters LICENSE` (plain `git hash-object` without `--no-filters` would run Git's own clean/EOL filter first and could hide a real divergence), and fails the release if the two IDs don't match exactly.
3. From there, every subsequent copy is checked by SHA-256 against that canonical source: the packaging-directory copy, the portable zip's `current/LICENSE` entry, the full nupkg's `lib/app/LICENSE` entry (both compared by extracting and hashing the actual archive entry bytes, not just checking the entry exists), and the real installed copy from a genuine silent install/uninstall cycle.

v0.4.0-alpha.1 exposed the gap this closes: `actions/checkout` on `windows-latest` normalized the committed LF `LICENSE` to CRLF, and the release workflow's old checks only ever compared the working copy to its own copy elsewhere in the same pipeline — always self-consistent, never actually checked against the canonical Git object. The file contents (including the "Required Notice: Copyright 2026 shoemin") were never in question, only the line-ending encoding — but the workflow couldn't have caught a genuine content divergence either, since it never had a canonical reference point. alpha.1 is immutable and was not corrected retroactively; this is fixed starting with alpha.2.

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

**License preservation:** `<publish-dir>` must contain `LICENSE` (copied from the repository root, verbatim) before `vpk pack` runs, or the packaged distributables would silently omit the PolyForm Noncommercial License terms and required copyright notice. It ends up at `current/LICENSE` in the portable zip and `lib/app/LICENSE` in the full nupkg; a real silent install (`Setup.exe --silent --installto <dir>`) places it at `<installdir>\current\LICENSE`, confirmed with a real install/uninstall cycle, not just by inspecting archive contents.

**v0.4.0-alpha.1 was the first release published through this pipeline** (`win-beta` channel). Its checksum manifest and `LICENSE` line-ending encoding predate the fixes described in [Public checksum manifest scope](#public-checksum-manifest-scope) and [Canonical LICENSE bytes](#canonical-license-bytes) above; alpha.1 itself is immutable and was not corrected retroactively.

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
