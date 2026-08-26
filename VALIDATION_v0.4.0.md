# v0.4.0 Validation Notes

Status: prereleases `v0.4.0-alpha.1`, `v0.4.0-alpha.2`, and `v0.4.0-beta.1` have been published and field-tested. **This document does not constitute RC or Stable approval** — see the release-readiness assessment in the pre-RC readiness PR for that classification.

Each item below is marked:

- **PASS** — verified with real evidence (log/process/network), not merely assumed.
- **FIELD-OBSERVED** — a real human directly observed and reported the result, but full independent evidence (structured logs, process identity) was not captured for this specific instance.
- **AUTOMATED-ONLY** — covered by the self-test suite; not yet exercised against a real installed build / real Palworld server.
- **PENDING** — not yet exercised in any form.

## Compiler/test gate

```powershell
.\scripts\build.ps1
```

**PASS** — 99/99 self-tests, 0 build warnings, 0 build errors, verified repeatedly across the alpha.1, alpha.2, and beta.1 release workflow runs and locally during this validation.

## Installed-mode baseline (v0.4.0-alpha.1)

- Real Setup.exe install/uninstall cycle, Installed execution mode — **PASS**
- Persistent server/profile data preserved across install — **PASS**
- Fresh install defaults to the Prerelease channel (package built for `win-beta`) — **PASS**
- Update check against the live public feed — **PASS**
- Real Palworld start and lifetime monitoring (launcher + Shipping process) — **PASS**
- Manager restart while Palworld keeps running, with correct process reattachment — **PASS**
- Graceful Safe Stop (REST `/save` + `/shutdown`, exit codes captured) — **PASS**
- Detection of an externally terminated / manually closed Palworld process, correctly classified as unexpected rather than a fabricated clean stop — **PASS**

## Live self-update (v0.4.0-alpha.1 -> v0.4.0-alpha.2)

A real installed `v0.4.0-alpha.1` checked for, downloaded, and installed the published `v0.4.0-alpha.2` update while a real, disposable Palworld dedicated server ran throughout.

- Palworld Shipping process ID preserved across the Manager replacement — **PASS** (pre-update PID 1856, post-update PID 1856, directly re-queried)
- Palworld Shipping process start time preserved — **PASS** (pre/post: `8/23/2026 2:05:29 PM`, identical)
- No update-triggered REST `/save` or `/shutdown` — **PASS** (confirmed absent from the structured log across the entire apply window)
- No update-triggered SteamCMD activity — **PASS**
- Correct reattachment to the same profile, no cross-profile attachment — **PASS**
- Dashboard/REST polling recovery after Manager replacement — **PASS**
- LAN API/discovery recovery after Manager replacement — **PASS**
- Post-update Safe Stop on the reattached server — **PASS** (REST save/shutdown 200, both processes exited 0)

## Release pipeline hardening

- Previous-prerelease retrieval with `--pre` (pinned `vpk` 1.2.0 defaults it to `false`) — **PASS**, verified against the real repository both as a positive control (found `v0.4.0-alpha.1`/`v0.4.0-alpha.2`) and a negative control (reproduced the original failure without the flag)
- Real Velopack delta package generation between consecutive same-channel releases — **PASS**, confirmed on both alpha.1→alpha.2 and alpha.2→beta.1
- Canonical LICENSE byte verification (raw checked-out bytes proven identical to the Git blob committed in the tag, traced through packaging directory, both archive formats, and a real installed copy) — **PASS**, first correctly caught a real CRLF/LF divergence on alpha.1, then verified clean on alpha.2 and beta.1
- Public `SHA256SUMS.txt` scope (lists only files Velopack actually publishes) — **PASS**
- Public `SHA256SUMS.txt` generated from actual published GitHub Release bytes, not local packaging-stage copies, with independent post-upload re-verification — **PASS on v0.4.0-beta.1**, the first release to exercise this corrected pipeline. `v0.4.0-alpha.2`'s `releases.win-beta.json` checksum entry is confirmed to not match its actual published bytes (root cause: `vpk upload github` regenerates that one file's content in-memory at publish time); this is a release-pipeline checksum-manifest defect, not a Manager runtime or update-feed defect, and alpha.2 remains immutable/uncorrected as historical evidence.

## Critical-operation apply gating

- `CriticalOperationTracker` blocking `Install and Restart` during a Manager-owned operation — **AUTOMATED-ONLY**. Two real field attempts did not exercise this: the first used Backup, which correctly refuses to run against a running server before the blocker is ever reached; the second found no update available to block (the Manager was already on the latest release) and a real LAN transfer completed too quickly to hold open. Real LAN transfer transmission/completion itself is separately confirmed working (**PASS**, `v0.4.0-beta.1`, real paired peer, matching byte counts and SHA-256 throughout).
- Running Palworld server does NOT block `Install and Restart` — **PASS**, confirmed in the same alpha.1→alpha.2 field test.
- Planned: deliberately hold a destination transfer offer pending (declining to accept/reject immediately) during a `beta.1 -> rc.1` update to finally exercise this blocker with real evidence.

## alpha.2 -> beta.1 update

- Update succeeded with Palworld running and correct reattachment — **FIELD-OBSERVED** (independently reported; the session that actually applied this update did not capture a fresh process-identity timeline comparable to the alpha.1→alpha.2 evidence above, since it happened before observation began for that session).

## Not yet exercised at all

- **PENDING** — a genuinely pending LAN transfer offer held open long enough to observe an active `CriticalOperationTracker` lease and its effect on `Install and Restart`, end to end.
