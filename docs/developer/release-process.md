# Release process

## CI

`.github/workflows/ci.yml` runs on every push/PR to `main`: checks out, sets up the .NET SDK from `global.json`, and runs `scripts\build.ps1` (restore + Release build + self-tests), uploading the build transcript as an artifact.

## Releases (current state)

`.github/workflows/release.yml` builds a tagged release: exact tag checkout, version validation, restore, Release build, self-tests, a self-contained win-x64 single-file publish, packaging into a `PalworldServerManager-vX.Y.Z-win-x64.zip`, a `SHA256SUMS.txt` checksum file, and creation of the GitHub Release with those assets attached.

!!! note "Installer / self-update packaging is in progress"
    A Velopack-based `Setup.exe` installer and in-app self-update are planned (see the [architecture](architecture.md) page for how runtime reattachment already supports this). This page will be updated with the actual `vpk` packaging steps, pinned Velopack version, and release-asset list once that lands — do not assume it's already part of the release workflow until this note is removed.

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
