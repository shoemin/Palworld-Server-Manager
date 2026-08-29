# v0.4.0 Validation Notes

Status: prereleases `v0.4.0-alpha.1`, `v0.4.0-alpha.2`, `v0.4.0-beta.1`, and `v0.4.0-rc.1` have all been published and field-tested. Every required Stable gate below is now **PASS** (four Stable/`win`-specific gates are PASS by non-publishing rehearsal or structural review, since `win` has never actually been published — see [First Stable/`win` packaging path](#first-stable-win-packaging-path-non-publishing-rehearsal)). This document is the detailed evidence base; the formal Stable-readiness classification (blockers vs. non-blocking limitations) is in this PR's description.

Each item below is marked:

- **PASS** — verified with real evidence (log/process/network), not merely assumed.
- **FIELD-OBSERVED** — a real human directly observed and reported the result, but full independent evidence (structured logs, process identity) was not captured for this specific instance.
- **AUTOMATED-ONLY** — covered by the self-test suite; not yet exercised against a real installed build / real Palworld server.
- **PENDING** — not yet exercised in any form.

## Compiler/test gate

```powershell
.\scripts\build.ps1
```

**PASS** — 99/99 self-tests, 0 build warnings, 0 build errors, verified repeatedly across the alpha.1, alpha.2, beta.1, and rc.1 release workflow runs and locally during this validation.

## v0.4.0-rc.1 publication

The real, normal tag-push Release workflow (run [33229687457](https://github.com/shoemin/Palworld-Server-Manager/actions/runs/33229687457), event `push`, attempt 1) published `v0.4.0-rc.1` successfully — **PASS**, exercising the same corrected checksum pipeline for a second consecutive real release:

- 99/99 self-tests, 0 warnings, 0 errors, `mkdocs build --strict` clean
- `v0.4.0-beta.1` correctly retrieved as the previous same-channel release using `--pre` — **PASS**
- Real Velopack delta package generated (`0.4.0-beta.1 -> 0.4.0-rc.1`) — **PASS**
- Canonical LICENSE byte verification clean — **PASS**
- Post-publication asset download from the actual GitHub Release, `SHA256SUMS.txt` generated from those downloaded bytes, and the published manifest independently re-downloaded and re-verified — **PASS**
- `releases.win-beta.json`'s manifest hash matches its actual published asset bytes exactly, independently re-verified after publication — **PASS** (this is the specific regression `v0.4.0-alpha.2` originally exposed)
- Public `win-beta` update feed correctly advertises `v0.4.0-rc.1` as latest — **PASS**

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
- Real Velopack delta package generation between consecutive same-channel releases — **PASS**, confirmed on alpha.1→alpha.2, alpha.2→beta.1, and beta.1→rc.1
- Canonical LICENSE byte verification (raw checked-out bytes proven identical to the Git blob committed in the tag, traced through packaging directory, both archive formats, and a real installed copy) — **PASS**. `v0.4.0-alpha.1`'s post-publication audit exposed a real CRLF/LF divergence; the resulting canonical-byte verification was then added prospectively and verified clean on `v0.4.0-alpha.2` and `v0.4.0-beta.1`
- Public `SHA256SUMS.txt` scope (lists only files Velopack actually publishes) — **PASS**
- Public `SHA256SUMS.txt` generated from actual published GitHub Release bytes, not local packaging-stage copies, with independent post-upload re-verification — **PASS**, confirmed on both `v0.4.0-beta.1` and `v0.4.0-rc.1`, the first two releases to exercise this corrected pipeline. `v0.4.0-alpha.2`'s `releases.win-beta.json` checksum entry is confirmed to not match its actual published bytes (root cause: `vpk upload github` regenerates that one file's content in-memory at publish time); this is a release-pipeline checksum-manifest defect, not a Manager runtime or update-feed defect, and alpha.2 remains immutable/uncorrected as historical evidence.

## Critical-operation apply gating

- `CriticalOperationTracker` blocking `Install and Restart` during a genuine Manager-owned operation — **PASS**, field-proven during the `v0.4.0-beta.1 -> v0.4.0-rc.1` test below via a real `LanTransferReceive` operation (a real ~24.5 MB `.palserver` transfer from a paired peer, SHA-256 verified). In an already-open Updates window: Install and Restart was **enabled** before the transfer, directly observed **disabled** while the real receive was actively in progress, and **automatically re-enabled** in that same window (no reopening) once the transfer completed and the lease released. This proves the full `ICriticalOperationTracker.Changed` → `ApplicationUpdateService.StatusChanged` → `UpdatesWindow.RefreshUi` → `GetApplyBlockReason` refresh path end-to-end, not just the gate's static logic. The exact user-facing blocker text was visually observed but not captured verbatim/screenshotted — this does not weaken the finding, since the button-state transition, the real production lease, and the automatic re-enable were all directly observed.
- Running Palworld server does NOT block `Install and Restart` — **PASS**, confirmed in both the alpha.1→alpha.2 and beta.1→rc.1 field tests.
- **Non-blocking UX/testability limitation discovered during this test:** `SendServerWindow` and `UpdatesWindow` are both modal to `MainWindow`, and neither is modal to the other — there is no UI sequence where both can be open simultaneously in either direction. A send-direction observation attempt was invalidated when closing the transfer dialog (the only way to reach the Updates menu) cancelled the transfer itself. The receive direction was used instead, since `LanTransferReceive`'s lease is held only during the post-accept byte-transfer phase (not while an offer merely awaits an accept/reject decision), so accepting on `MainWindow` and then immediately opening Updates avoided the conflict. This is a real architectural finding worth a future non-modal redesign, but it is not a safety defect — the underlying gate was directly proven to work.

## v0.4.0-beta.1 -> v0.4.0-rc.1 live self-update

A real installed `v0.4.0-beta.1` checked for, downloaded, and installed the published `v0.4.0-rc.1` update while a real, disposable Palworld dedicated server (`disposable` profile) ran continuously throughout, monitored by an independent external process watcher sampling roughly every 300 ms — a stronger evidentiary standard than the earlier alpha.1→alpha.2 before/after-snapshot test.

- Palworld Shipping process ID preserved across the Manager replacement — **PASS** (pre-update PID 10016, post-update PID 10016, identical)
- Palworld Shipping process start time preserved — **PASS** (pre/post: `2026-08-28T22:20:43.9516877-05:00`, identical to the microsecond)
- Continuous external-watcher confirmation — **PASS**: 10,682 samples across ~56 minutes; the only 46 `ABSENT` samples fall entirely within the deliberate final Safe Stop window (23:17:05–23:17:19); zero absences anywhere near the actual Manager replacement (23:15:51–23:15:56)
- No update-triggered REST `/save` or `/shutdown` — **PASS** (confirmed absent from the structured log across the entire apply window)
- No update-triggered SteamCMD activity — **PASS**
- Correct reattachment to the `disposable` profile only, no cross-profile attachment — **PASS** (`Runtime handoff verified process identity`, `Reattached lifetime monitor to already-running server`, `Startup runtime reconciliation for 'disposable': Attached`)
- Dashboard/REST polling recovery after Manager replacement — **PASS** (resumed within ~40ms of Manager startup)
- LAN API/discovery recovery after Manager replacement — **PASS**, same LAN instance ID
- Existing pairing/trust with the paired peer (`DESKTOP-7T5JM31`) preserved unchanged across the Manager replacement — **PASS** (`lan-state.json` identical before/after; two real, successful, SHA-256-verified transfers using this exact pairing occurred both immediately before and immediately informing the blocker test)
- Post-update Safe Stop on the reattached server — **PASS** (REST `/save` 200 in 80ms, `/shutdown` 200 in 13ms, both processes exited 0, both PIDs confirmed gone at the OS level)

## alpha.2 -> beta.1 update

- Update succeeded with Palworld running and correct reattachment — **FIELD-OBSERVED** (independently reported; the session that actually applied this update did not capture a fresh process-identity timeline comparable to the alpha.1→alpha.2/beta.1→rc.1 evidence above, since it happened before observation began for that session). The stronger, fully-instrumented beta.1→rc.1 test above supersedes this as evidence of the underlying mechanism's reliability, but this entry is left as originally recorded rather than retroactively upgraded.

## First Stable/`win` packaging path (non-publishing rehearsal)

`win` has never been exercised by any of the four published prereleases (all four packaged to `win-beta`). Before recommending Stable readiness, the same packaging path the release workflow would take for a real `v0.4.0` tag was rehearsed locally with pinned `vpk` 1.2.0, version `0.4.0`, channel `win`, `is_prerelease=false` — no tag was created, nothing was uploaded or published, and no existing release/tag was touched.

- Previous-release discovery for `win` without `--pre` (the workflow's actual invocation for a stable channel) — **PASS**: `vpk download github --channel win` against the real repository reported `No releases found` / `Found 0 release(s)` and exited `0`, with nothing downloaded — the expected "no previous release yet" outcome for the first-ever Stable release, and no cross-channel contamination from the four existing `win-beta` releases.
- Local `dotnet publish` (self-contained `win-x64`, `-p:Version=0.4.0`) plus canonical LICENSE copy — **PASS**, byte-identical to the canonical Git blob (`f0b0056d...4b28ae`), same hash confirmed across all four prereleases.
- `vpk pack --channel win --packVersion 0.4.0` — **PASS**, completed with no previous package to diff against (full-only, no delta, as expected for a channel's first release). Verified the `win`-specific Velopack default-channel naming rules empirically, not just from source:
  - `ShoeMin.PalworldServerManager-win-Setup.exe` — channel-suffixed (Setup.exe/Portable.zip are always suffixed, even for the default channel)
  - `ShoeMin.PalworldServerManager-win-Portable.zip` — channel-suffixed
  - `ShoeMin.PalworldServerManager-0.4.0-full.nupkg` — **no** `win` suffix (the default-channel nupkg special case)
  - No delta nupkg produced — correct, no previous `win` release existed to diff against
  - `RELEASES` (legacy, unsuffixed) written locally — correct per `CoreUtil.GetReleasesFileName`'s `win` special-case; this file is only ever written unsuffixed for the default channel, `RELEASES-win-beta` for others
  - `assets.win.json` also written locally (bookkeeping only, never a public asset on any channel)
- Rehearsed the release workflow's exact "Determine and verify the intended public release asset set" logic (same name-construction rules, executed against the real rehearsal output, not just read) — **PASS**: computed set was exactly `RELEASES`, `releases.win.json`, `ShoeMin.PalworldServerManager-0.4.0-full.nupkg`, `ShoeMin.PalworldServerManager-win-Portable.zip`, `ShoeMin.PalworldServerManager-win-Setup.exe` (no delta, correctly not fabricated), all five present, zero missing, `assets.win.json` correctly excluded.
- LICENSE bytes inside the rehearsal `Portable.zip` (`current/LICENSE`) and full `nupkg` (`lib/app/LICENSE`) — **PASS**, both byte-identical to the canonical Git blob.
- Post-publication checksum-from-published-bytes design (PR #13's fix) — **PASS by structural review**: every step from `vpk upload github` onward operates purely on the already-verified `$publicAssets` list and the tag/channel string for labeling; nothing in that sequence branches on channel name beyond what `$publicAssets` already encodes, so the same protection applies unchanged to `win`. This is a design/code review, not a live test — since this rehearsal deliberately never published anything, whether `vpk upload github` actually uploads the unsuffixed `RELEASES` asset for `win` exactly as its source was audited to do (see `docs/developer/release-process.md`) remains unproven by a real publish. This is the one specific thing worth watching narrowly during the real first Stable publish, not a reason to delay it.

No tag, Release, or GitHub Actions run was created or modified by this rehearsal. All local rehearsal output was written to a scratch directory outside the repository and never staged or committed.

## v0.4.0 Stable gate matrix

Every gate required before Stable publication:

| Gate | Status |
|---|---|
| Setup/install baseline | PASS |
| Persistent-data preservation | PASS |
| Manager restart reattachment | PASS |
| Manual/crash exit monitoring | PASS |
| alpha.1 -> alpha.2 live update | PASS |
| alpha.2 -> beta.1 update | FIELD-OBSERVED |
| beta.1 -> rc.1 live update | PASS |
| Exact process continuity | PASS |
| Delta generation | PASS |
| Canonical LICENSE | PASS |
| Post-publication checksums | PASS |
| Public feed | PASS |
| Real LAN transfer | PASS |
| Active Manager-operation apply blocker | PASS |
| Dynamic blocker release | PASS |
| Dashboard recovery | PASS |
| LAN/trust recovery | PASS |
| Safe Stop after reattachment | PASS |
| First Stable/`win` previous-release discovery (no `--pre`) | PASS (rehearsal) |
| First Stable/`win` packaging (naming, no-delta, LICENSE) | PASS (rehearsal) |
| Stable public-asset-set computation | PASS (rehearsal) |
| Post-publication checksum design applies to `win` | PASS (structural review) |

No required gate remains AUTOMATED-ONLY, PENDING, or FAIL. `alpha.2 -> beta.1` is retained as FIELD-OBSERVED rather than retroactively upgraded, since the stronger beta.1→rc.1 test — not a rewrite of missing evidence — is what actually closes the risk. The four Stable/`win`-specific rows are marked "(rehearsal)"/"(structural review)" rather than plain PASS because they were deliberately exercised without ever publishing — the one thing they cannot prove is whether `vpk upload github` actually uploads the unsuffixed `RELEASES` asset for `win` exactly as documented; that gets its first live proof on the real first Stable publish.
