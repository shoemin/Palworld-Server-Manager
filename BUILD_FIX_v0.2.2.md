# Build Fix v0.2.2

This revision fixes the compile errors reported from v0.2.1:

- Added an explicit `System.IO` import to `MainWindow.xaml.cs` for `Directory` and `Path`.
- Rewrote `OpenFolder_Click` with an explicit local null check so definite assignment is unambiguous.
- No server save, profile, backup, export, or import format changes were made.

The previous v0.2.1 fixes remain in place: WPF-native `Microsoft.Win32.OpenFolderDialog`, no WinForms dependency, explicit `System.Windows.Application`, and Windows-guarded Steam registry access.
