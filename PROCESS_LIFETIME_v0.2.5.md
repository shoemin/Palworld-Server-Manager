# Process Lifetime Monitoring — v0.2.5

This revision changes the server lifecycle contract.

When the manager launches `PalServer.exe`, it keeps the returned `Process` object and starts a background lifetime monitor. The monitor also attaches to PalServer child/shipping processes found under the same managed installation.

A managed profile remains locked against another Start until:

1. all PalServer processes for the installation have exited;
2. a short launcher-to-shipping handoff grace period has passed; and
3. the manager has captured/logged the available process exit codes and finalized the lifetime.

Expected safe/force stops are recorded as expected. Manual closes and crashes are recorded as unexpected; a non-zero exit code is surfaced as an error.

The WPF UI remains responsive while this monitoring occurs.
