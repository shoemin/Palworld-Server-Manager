# File locations

Palworld Server Manager keeps all persistent data under one root, separate from the application binaries themselves:

```
%LocalAppData%\PalworldServerManager\
```

| Path | Contents |
|---|---|
| `servers.json` | The managed-server profile registry. |
| `servers\<profile-id>\PalServer\` | Each managed server's isolated Palworld runtime (executable, `Pal\Saved`, `Mods`, config). |
| `backups\<profile-id>\` | Filesystem backups created via [Backups](../guide/backups.md). |
| `logs\` | Manager session logs; `logs\servers\` holds per-server correlated logs. |
| `steamcmd\` | The Manager's own SteamCMD installation, used to provision/update managed runtimes. |
| `lan\lan-state.json` | LAN enabled/disabled state, instance ID, trusted-peer records. Inbound bearer tokens are stored hashed, not in plaintext. |
| `incoming\` | Received `.palserver` files from LAN transfers (and their `.partial` files while a transfer is in progress). |
| `outgoing\` | Temporary `.palserver` files being staged for a LAN send. |
| `runtime\update-handoff.json` | A short-lived, one-shot hint written before a Manager self-update restart so the new process can reattach to already-running servers. Contains no passwords/tokens — only process/profile identifiers. Deleted as soon as it's read. |

Application binaries (the portable build today, or a future installed build) live wherever you extracted/installed them — never inside the path above. See [Installation](../getting-started/installation.md) for why that separation is a hard requirement, not an accident.
