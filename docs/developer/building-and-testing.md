# Building & testing

## Requirements

- Windows (the app targets `net8.0-windows` for WPF; `Core`/`Lan`/`SelfTest` target plain `net8.0`).
- .NET SDK matching `global.json` (currently pinned to the `8.0.100` family with `rollForward: latestFeature`).

## Build and self-test

```powershell
.\build.cmd
```

or directly:

```powershell
powershell -File scripts\build.ps1
```

This restores, builds the whole solution in `Release`, and runs the self-test console app, writing a timestamped transcript to `build-logs\`.

## Running just the self-tests

```powershell
dotnet run --project tests\PalworldServerManager.SelfTest\PalworldServerManager.SelfTest.csproj -c Release --no-build
```

## Test style

`PalworldServerManager.SelfTest` is a plain console app, not a conventional test framework — it runs a linear list of `(name, Func<Task>)` pairs and prints `PASS`/`FAIL` per entry with a final count. Prefer this style for new tests rather than introducing a new test framework dependency.

Network-touching tests bind to loopback on an ephemeral port rather than depending on a real LAN, and process-reattachment tests use a harmless synthetic process (this project's own already-built apphost, run in a `--harness <seconds> <exitCode>` mode) rather than requiring a real Palworld installation. CI never depends on live GitHub releases, a real Palworld server, or a real second LAN peer — those remain manual field tests.

## Publishing a local build

```powershell
dotnet publish src\PalworldServerManager.App\PalworldServerManager.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o publish
```

Produces a self-contained, single-file `PalworldServerManager.exe` — no separate .NET runtime install needed on the target machine.
