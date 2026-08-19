# Steam Preflight / SteamCMD Code 7 — v0.2.4

Field testing showed SteamCMD exit code 7 on a test PC when the Steam desktop client was closed; opening/signing in to Steam resolved that machine's failure. Palworld's official dedicated-server instructions still use `+login anonymous`, so v0.2.4 does not make Steam desktop login or Palworld ownership a hard prerequisite.

Behavior:

1. Before create/import/update operations that invoke SteamCMD, the manager checks whether `steam.exe` is running.
2. If Steam is not running, the user may launch Steam, continue with anonymous SteamCMD, or cancel.
3. The manager does not inspect Steam credentials and does not claim to verify account ownership.
4. SteamCMD nonzero exit codes are surfaced as `SteamCmdException`.
5. Exit code 7 receives a one-time interactive recovery path instead of a blind automatic retry.
6. All decisions are written to the structured manager log.
