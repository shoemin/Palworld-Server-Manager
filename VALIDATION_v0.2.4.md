# v0.2.4 Validation Notes

Static validation performed for v0.2.4:

- application/core/self-test project versions aligned at 0.2.4;
- SteamCMD automatic retry removed and replaced with typed `SteamCmdException`;
- exit code 7 is classified for one interactive recovery retry;
- Steam desktop preflight is applied to create, portable import, existing-server import, and update operations;
- the preflight checks only whether `steam.exe` is running and does not claim to verify login or account ownership;
- WPF event-handler references resolve;
- project/XAML XML parses;
- no WinForms dependency was reintroduced;
- source delimiter/brace checks pass;
- archive integrity verified after packaging.

An authoritative WPF/.NET build must still be run on Windows with `build.cmd`.
