# Server lifecycle

## Start

Selecting a server and clicking **Start** launches `PalServer.exe` from that server's isolated install directory. The Manager retains a process handle and monitors it for its **entire** lifetime, including any `PalServer-Win64-Shipping*` child process it spawns.

Starting the same server twice is prevented: **Start** stays disabled while a server is running or while the Manager is still finishing capturing a previous run's exit code. Starting a different server while one is already running triggers a safe stop of the running server first — the next server does not launch until that stop completes.

## Safe Stop vs. Force Stop

| Action | What it does |
|---|---|
| **Safe Stop** | Requests a world save through the Palworld REST API, then a graceful shutdown, and waits for the process to exit. Requires the REST API to be enabled with an AdminPassword configured for that server. |
| **Force Stop** | Terminates the PalServer process tree directly. Use this only after saving in-game, or when Safe Stop isn't available (REST API not configured) or isn't responding. |

## Crash / unexpected-exit detection

If a server's process exits on its own — the window was closed manually, it crashed, Windows terminated it — the Manager detects this and captures the process exit code where possible. A non-zero exit is surfaced as a crash and points you toward exporting a diagnostic bundle; an expected stop (one the Manager itself requested) is not treated as an error.

## Manager restart reattachment

If you close **only** Palworld Server Manager while a managed server keeps running — or the Manager restarts for any reason — it does not lose track of that server. On startup, the Manager looks for already-running PalServer processes and reattaches its lifetime monitor to any that clearly belong to one of your managed profiles (matched by install path, process name, and — when available — a hint about which specific process to expect). It never attaches to a PalServer.exe outside your managed profiles' own install directories.

Once reattached, the server shows as **Running (monitored)** again, Start stays disabled, Safe Stop/Force Stop work normally, and a future crash or manual close is still detected and logged. If Palworld happened to exit during the brief window the Manager was restarting, the status honestly reflects that the exit code could not be recovered, rather than showing a false clean stop.

!!! info "This underlies Manager self-update too"
    The same reattachment mechanism is what lets [installing a Manager update](../manager-updates/update-while-server-running.md) restart the Manager without disturbing a running Palworld server.
