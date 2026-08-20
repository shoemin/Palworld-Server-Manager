# v0.3.0 Validation Notes

## Static validation completed before packaging

The update overlay was checked for:

- valid XML in all included XAML and project files;
- matching WPF `x:Class` code-behind types;
- all XAML event-handler names resolving to code-behind methods;
- all `ProjectReference` targets existing in the payload/repository layout;
- all solution project paths existing;
- balanced C# braces, brackets, parentheses, strings, characters, and comments;
- all App/Core/LAN/SelfTest project versions set to `0.3.0`;
- Dashboard Settings DataGrid configured `IsReadOnly="True"`;
- dashboard secret-like REST settings redacted before display/transport;
- `.palserver` LAN receives finalized only after byte-length and SHA-256 verification;
- failed receive paths retain no completed `.palserver` file;
- LAN services disabled by default for a new Manager state.

## Compiler/test gate

This packaging environment does not have the Windows .NET 8/WPF SDK, so it cannot be the authoritative compiler gate.

After applying the overlay on Windows, run:

```powershell
.\build.cmd
```

The repository build and self-tests must pass before committing/releasing v0.3.0.

## Field-test gate

After the Windows build passes, perform the two-PC test in `LAN_DASHBOARD_v0.3.0.md`. In particular verify:

1. local Dashboard REST data;
2. read-only Settings display and redaction;
3. two-PC discovery and one-use pairing;
4. remote Dashboard data;
5. explicit incoming-transfer acceptance;
6. package transfer SHA-256 verification;
7. received-package import;
8. transferred world/player/base/Pal/settings integrity.
