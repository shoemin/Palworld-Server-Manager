# Validation status — v0.2.2

Source validation performed in the packaging environment:

- PASS: all `.xaml` and `.csproj` files parse as XML.
- PASS: solution and project-reference targets exist.
- PASS: XAML `x:Class` names have matching code-behind partial classes.
- PASS: XAML event handlers referenced by markup exist in code-behind.
- PASS: static C# lexical/delimiter validation completed with zero errors.
- PASS: diagnostic bundle implementation only packages manager logs, selected-server text logs, sanitized settings/profile metadata, and process/environment metadata; it does not enumerate or package Palworld `.sav` world/player files.
- PASS: diagnostic settings sanitizer explicitly redacts `AdminPassword` and `ServerPassword`, with additional generic secret/token redaction for text logs.
- PASS: structured logger includes per-launch session IDs, operation-correlation IDs, and per-server correlated log mirrors.
- PASS: global WPF/AppDomain/Task exception handlers write exception details to the manager log.
- PASS: SteamCMD/process/REST lifecycle paths now emit diagnostic logging without intentionally logging REST credentials.
- PASS: existing-server import retains the pre/post source-tree hash verification guarantee.
- PASS: self-test source includes structured-logging and diagnostic-redaction/save-exclusion tests in addition to parser/copy/registry/discovery tests.

## External Windows build feedback incorporated

### v0.2.0 report

The first external Windows build reported CS0104 in `App.xaml.cs`: `Application` was ambiguous between WPF and WinForms because the application project enabled both UI frameworks. v0.2.1 removed the WinForms dependency, switched folder selection to WPF's .NET 8 `OpenFolderDialog`, explicitly derived `App` from `System.Windows.Application`, and guarded Steam registry access at the helper boundary.

### v0.2.1 report

The second external Windows build confirmed those fixes, then reported four errors in `MainWindow.xaml.cs`: unresolved `Directory` / `Path` names and a definite-assignment error around the selected `profile`. v0.2.2 adds explicit `System.IO` usage and rewrites the profile null-check so assignment is explicit.

The repository now also includes `global.json` so builds prefer an installed .NET 8 SDK feature band rather than silently selecting a newer major SDK.

## Build execution status

An actual .NET/WPF compile could not be executed in the packaging environment because the .NET SDK/MSBuild and Windows WPF runtime are not installed there. The included Windows build entry point performs the real compile and test pass on your machine:

```powershell
.\scripts\build.ps1
```

or simply:

```text
build.cmd
```

It performs:

1. `dotnet restore`
2. Release build of the solution
3. execution of the dependency-free `PalworldServerManager.SelfTest` console test suite
4. capture of the full build/test transcript under `build-logs\build-YYYYMMDD-HHmmss.log`

A live Palworld lifecycle/import test still requires Windows plus an actual Palworld dedicated-server installation.
