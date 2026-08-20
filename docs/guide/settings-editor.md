# Settings editor

Palworld Server Manager includes a GUI editor for `PalWorldSettings.ini`, reached from **Servers → select a server → Settings**.

## What it edits

The editor reads and writes the server's `Pal\Saved\Config\WindowsServer\PalWorldSettings.ini`. It parses the `OptionSettings` line (including quoted values containing commas, and nested lists like `DenyTechnologyList=(...)`) and presents them as editable fields.

Unknown or future settings the editor doesn't specifically recognize are **preserved on save**, not dropped — round-tripping through the editor should never silently delete a setting you or a future Palworld patch added. Comments in the file are also retained.

If the server is currently running, it must be stopped before settings can be changed, consistent with the rest of the app's server-lifecycle rules.

## This is not the same as the Dashboard's Settings view

It's easy to confuse two different "Settings" in the app — they're deliberately different:

| | Configuration editor (this page) | [Dashboard → Settings](dashboard.md) |
|---|---|---|
| What it shows | The **desired** persistent configuration in `PalWorldSettings.ini` | The **effective, live** settings the *running* server currently reports via its REST API |
| Editable? | Yes | **No — strictly read-only** |
| Server state | Works whether the server is running or stopped (must be stopped to save changes) | Only populated while the server is running with REST enabled |

The Dashboard Settings view will never grow an editor — if you want to change a setting, use this page.
