# v0.2.1 Build Fix

The first external Windows build of v0.2.0 exposed this compile error:

`CS0104: 'Application' is an ambiguous reference between 'System.Windows.Forms.Application' and 'System.Windows.Application'`

## Root cause

The WPF application project also enabled WinForms solely to use `FolderBrowserDialog`. That made both `System.Windows.Application` and `System.Windows.Forms.Application` available to the application compilation.

## Correction

- Removed `<UseWindowsForms>true</UseWindowsForms>` from the WPF application project.
- Replaced `System.Windows.Forms.FolderBrowserDialog` with the WPF/.NET 8 `Microsoft.Win32.OpenFolderDialog`.
- Made the application base class explicitly `System.Windows.Application`.
- Added an explicit `OperatingSystem.IsWindows()` guard inside the Steam registry helper to address the CA1416 warnings reported by the same build.
- Bumped source version to 0.2.1.

No server save, profile, backup, export-package, or diagnostic-bundle format was changed.

## Retest

Run `build.cmd` from the repository root. If it still fails, send the newest file under `build-logs`.
