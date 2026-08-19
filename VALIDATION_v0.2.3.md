# v0.2.3 Validation Notes

This container cannot execute an authoritative Windows WPF/.NET build. The project should therefore still be built with `build.cmd` on Windows before runtime testing.

Static validation performed for v0.2.3:

- all project files and XAML files parse as XML;
- all project references resolve;
- XAML event-handler names resolve to methods in the corresponding code-behind file;
- no WinForms project setting/reference was reintroduced;
- application/core/self-test project versions are aligned at 0.2.3;
- SteamCMD retry is bounded to a single retry and only when SteamCMD was absent before provisioning;
- diagnostic server-log extension filter includes `.json`;
- diagnostic self-test now creates and requires a JSON server log;
- ZIP archive integrity verified after packaging.
