# Agent development workflow

Palworld Server Manager's post-v0.4.0 development uses a deliberately explicit planning and execution system, so that AI-assisted implementation stays traceable, reversible at every checkpoint, and never silently exceeds what's actually been authorized. This page is the full human-readable explanation; [`CLAUDE.md`](https://github.com/shoemin/Palworld-Server-Manager/blob/main/CLAUDE.md) at the repository root is the concise contract Claude reads before working.

The canonical Project is **Palworld Server Manager Development** (project number 1, owned by `shoemin`): [github.com/users/shoemin/projects/1](https://github.com/users/shoemin/projects/1).

## Why this page changed shape

[#19](https://github.com/shoemin/Palworld-Server-Manager/issues/19) — the v0.5 Manager Host/platform architecture issue — went through four ChatGPT correction rounds, and each round found genuine, previously-missed contradictions even though the *specific* correction requested in the prior round had been implemented correctly. The pattern was consistent: fixing one section correctly, without rechecking every other section that depended on the same fact, left stale claims standing until a reviewer found them by reading the whole document again. [#33](https://github.com/shoemin/Palworld-Server-Manager/issues/33) exists to close that gap structurally rather than relying on reviewers to keep catching it: a short, checkable **invariant registry** replaces "reread the whole prose document and hope," a **mandatory preflight** makes Claude enumerate every affected path before editing, a **mandatory full invariant audit** runs before every Review Required checkpoint (not just a check of the specific delta), and the review checkpoint itself now happens **before** a PR exists, so a reviewer sees the exact pushed state rather than a PR description that may drift from it.

## The v0.5 invariant registry

[`docs/developer/v0.5-invariants.md`](v0.5-invariants.md) is the **normative** source of truth for cross-cutting v0.5 requirements — stable-ID, one-line, checkable statements like `HOST-001` ("exactly one authoritative machine-wide Manager Host per physical PC"), grouped into families (`ARCH`, `HOST`, `PERSIST`, `CLIENT`, `IDENT`, `LOCAL`, `OWNER`, `REMOTE`, `PAIR`, `AUTH`, `PROTO`, `OPS`, `RECOVERY`, `SEC`, `MIG`, `PLATFORM`, `LINUX`).

Detailed design documents (`docs/developer/v0.5-architecture.md`, future implementation docs) explain and elaborate these invariants — they never override them silently. A design document that contradicts a registry entry is the thing that's wrong, not the other way around.

Every v0.5 issue's "applicable invariants" list cites IDs from this registry, not a paraphrase. Every "Mandatory full invariant audit before Review Required" pass (below; `CLAUDE.md` §F) checks the resulting whole against those IDs, not just the section that was deliberately edited.

## Roles

**Product owner**
: Final authority. Makes product decisions and approvals. Nothing ships, merges, or releases without the product owner's deliberate action.

**GitHub Project**
: The canonical roadmap and execution state. If the Project disagrees with what someone remembers from a conversation, the Project wins.

**GitHub Issues**
: Canonical, bounded work contracts. An Issue is where scope, acceptance criteria, and dependencies actually live — not in chat history.

**Claude**
: The primary implementation agent. Executes already-authorized work, following the rules in `CLAUDE.md`.

**ChatGPT**
: Planner and technical reviewer. Reviews checkpoints Claude reports and either approves (`continue`) or sends back a revised plan.

**Codex**
: Independent PR reviewer, run against every implementation PR through the robust review protocol described below.

## The Workflow State field

Every Project item carries a `Workflow State` — the single authoritative execution-state field:

| State | Meaning |
|---|---|
| **Backlog** | Recorded but not currently authorized for implementation. |
| **Ready** | Planned, accepted, dependencies satisfied — Claude may begin. |
| **In Progress** | Claude is actively executing it. |
| **Review Required** | Work reached a defined ChatGPT review checkpoint. |
| **Changes Required** | Review found changes and the current plan is being corrected. |
| **Product Decision** | Implementation must stop until the product owner and ChatGPT make a product/architecture/scope decision. |
| **Field Test** | Implementation is technically complete but requires real-world validation. |
| **Done** | Accepted and complete. |

A `Backlog` item is a recorded intent, not an authorization. Only `Ready` (or a Claude-owned `In Progress` item) may actually be worked on.

## What `Ready` actually means

Labeling an item `Ready` in the Project isn't sufficient by itself. A Ready **execution** issue must actually contain: one bounded objective; explicit parent/Epic; satisfied dependencies; applicable invariant IDs (for v0.5 work); exact authorized scope; explicit out-of-scope items; exact acceptance criteria; required tests/validation commands; allowed implementation/architecture decisions; Product Decision/stop conditions; a defined review checkpoint; and an exact `After PASS` action.

**A parent Epic is never itself an executable Ready unit**, regardless of its Workflow State label — see "Issue decomposition" below. If Claude reads an issue and it amounts to "implement this whole subsystem," that's a sign it needs decomposing before it's actually executable, not a signal to start.

## Mandatory preflight / impact analysis

Before editing anything, Claude reports (internally, or in the current work log): applicable invariant IDs; projects/files/layers expected to change; dependency-direction changes, if any; authority-creation paths affected; credential holders/consumers affected; persistence writers affected; operation executors affected; and cross-section consequences that must be rechecked once the change lands.

For security/authority-sensitive work, this means enumerating **every** path to the effect, not just the section being edited — every grant-creation path, every private-key holder, every remote-operation-initiation path. This is the step that would have caught, before a reviewer had to, that a corrected pairing-security explanation needed the identity-binding mechanism corrected too, or that a fixed local-IPC ACL needed the credential-storage model fixed alongside it.

If the preflight surfaces an unresolved decision outside the issue's allowed decisions, stop and use Product Decision rather than inventing one.

## Mandatory full invariant audit before Review Required

Implementing the requested delta correctly is necessary but not sufficient to reach Review Required. Before that checkpoint, audit the **resulting whole** — not just the section just edited — against every applicable invariant, in a compact matrix:

| Invariant | Why affected | Evidence/check | Result |
|---|---|---|---|

Pair this with a stale/contradictory-reference search scoped to the change — grepping for every place a changed term, mechanism, or claim appears, not only the places deliberately touched. A failed applicable invariant keeps the issue at `In Progress`/`Changes Required`; it does not reach `Review Required`.

When the change touches both this document and `CLAUDE.md`, explicitly diff every duplicated executable command and compatibility claim between the two for semantic equivalence, not just prose intent. A #33 review round caught exactly this gap: `CLAUDE.md` had drifted to presenting the version-dependent `gh project item-edit --url/--field` form as primary while this document correctly required the portable node-ID form as canonical — each file had been edited correctly in isolation, but the two had gone out of sync with each other.

## The control loop

The standard checkpoint now pushes the branch **before** a PR exists, so the first review sees the exact pushed state rather than a PR description that can drift from it. A PR is opened only after that first review passes:

```
Plan
  -> Claude executes
  -> validation + full invariant audit (above)
  -> commit, push the issue branch (no PR yet)
  -> Review Required, STOP
  -> ChatGPT reviews the pushed branch directly
  -> first PASS ("continue") authorizes opening the PR + CI/Codex review only
  -> In Progress (so the Project doesn't misreport ChatGPT as the
     blocker while Claude is actively opening the PR and running CI/Codex)
  -> PR CI + robust Codex review clean
  -> Review Required again, STOP
  -> ChatGPT reviews the PR/CI/Codex evidence
  -> second PASS ("continue") may authorize merge, only if the issue's
     own "After PASS" section says so
```

If ChatGPT finds problems at either stage:

```
ChatGPT revises the plan
  -> product owner pastes that plan to Claude
  -> Claude updates the GitHub issue/project to reflect the revised plan
  -> Claude executes the corrected plan
  -> re-runs the issue's required validation and the full invariant
     audit (never carries forward pre-correction results)
  -> the same checkpoint (pre-PR or PR/Codex, whichever was active) repeats
```

If a PR already exists when changes are required, corrections are pushed to that same PR branch — never a new branch or a second PR for the same issue.

If the next step needs a new product decision:

```
STOP
  -> Workflow State = Product Decision
  -> product owner + ChatGPT planning discussion
```

## What `continue` means — and doesn't

The product owner replying `continue` means ChatGPT reviewed the preceding checkpoint and found no changes requiring a new plan. Under the two-stage checkpoint (above), **which PASS this is matters**:

- **First `continue`**, after the pre-PR pushed-branch report: authorizes opening the PR and running CI + the robust Codex review protocol. Nothing more — not merge, not the next issue.
- **Second `continue`**, after the PR/CI/Codex report: may authorize merge, but only if the issue's own `After PASS` section explicitly names merge as the next action.

Before actually continuing at either stage, Claude re-verifies: the branch/PR HEAD hasn't changed since the report was written; CI is still green (PR stage); all four Codex surfaces have been directly re-checked with no new unresolved finding (PR stage); and no new unresolved review finding exists generally.

`continue` does **not** authorize: new scope, a new release/tag, deleting history, moving an unplanned `Backlog` item straight to `Ready`, resolving a `Product Decision` on Claude's own judgment, overriding acceptance criteria, merging on a first-stage PASS, or opening a PR before the pre-PR review has actually happened.

**Release rule:** no phrase like `continue` implicitly authorizes creating or publishing a release. That only happens when the current approved Issue explicitly names that release action and the product owner has deliberately authorized it.

## Issue decomposition

- Large roadmap items remain Epics/containers — never executable Ready units themselves.
- Claude executes bounded child issues/vertical slices only.
- One issue should normally change one coherent architectural seam, or one coherent user-visible vertical slice — not several unrelated subsystems bundled because they share a milestone.
- Do not combine unrelated platform, security, persistence, migration, and UI work into a single issue for scheduling convenience.
- Decomposing an Epic doesn't make every child Ready at once — only the next dependency-satisfied child(ren) become Ready.

## The robust Codex review protocol

Codex's response can land on any of four different GitHub surfaces, and past experience on this project found real review content missed by checking only one:

1. top-level PR issue comments;
2. PR review objects;
3. inline review comments;
4. review threads.

A clean review may appear as **only** a top-level comment (`Codex Review: Didn't find any major issues... Reviewed commit: <sha>`). A findings-bearing review typically appears as a review object plus inline comments on specific lines. A response that isn't actually a review (for example, an environment-setup prompt) doesn't count as either.

State machine:

- Request review (`@codex review`).
- Poll all four surfaces.
- **Valid clean result**: body contains both "Codex Review" language and a "Reviewed commit" SHA that resolves via `git rev-parse <short-sha>` to the exact current 40-character PR head. Proceed.
- **Valid findings**: fix each one, credit `@chatgpt-codex-connector[bot]` (only for what Codex actually found), reply in the exact thread, resolve the thread once the fix is pushed, then **restart the whole protocol** against the new head.
- **Non-review response, or nothing valid yet**: request `@codex review` one more time. Maximum 2 attempts per unchanged head.
- Any pushed fix changes the head and resets the attempt counter.

Never merge on the assumption that "one endpoint came back empty, so it must be clean."

## Worked examples

**Happy path.** Issue is `Ready`. Claude sets it `In Progress`, runs the mandatory preflight, implements, validates, runs the mandatory full invariant audit, commits, pushes the branch (no PR), sets `Review Required`, and reports the pre-PR review report. Product owner pastes the report to ChatGPT, gets PASS, replies `continue`. Claude opens the PR against the already-pushed branch, gets CI green and a clean Codex review, sets `Review Required` again, and reports the PR/Codex review report. Product owner pastes that to ChatGPT, gets a second PASS, replies `continue`. Claude re-verifies nothing changed, merges (since the Issue's "After PASS" says to), syncs `main`, updates Project state to `Done`.

**Review finds an issue at the pre-PR checkpoint.** Same as above, but ChatGPT's review of the pushed branch finds a gap. Product owner pastes ChatGPT's revised plan. Claude updates the Issue body/acceptance criteria to match, leaves a comment explaining the revision, sets `In Progress`, and executes the correction on the same branch — then returns to the pre-PR checkpoint. No PR existed yet, so there is nothing to reopen or redirect.

**Review finds an issue after the PR is open.** ChatGPT (or Codex) finds a problem once CI/Codex review is underway. Claude pushes the fix to the *same* PR branch rather than starting a new one, which also resets the robust Codex review protocol's attempt count (see "The robust Codex review protocol" below; `CLAUDE.md` §H). Once clean, Claude returns to `Review Required` and reports the PR/Codex review report again — this is still the second-stage checkpoint, not a new first stage.

**Invariant audit catches a stale reference.** Before setting `Review Required`, Claude runs the mandatory full invariant audit and finds that a change made earlier in the same pass makes an existing sentence elsewhere in the document contradict an applicable invariant ID — the same class of problem as the `## 3. Local client transport` heading accidentally dropped during a #19 correction round, or the "asymmetric completion is safe" claim that survived one correction round after the fact it depended on had changed. The audit matrix records this row as FAIL, Claude fixes it in the same pass, and only sets `Review Required` once every applicable row reads PASS.

**Unexpected architecture discovery.** Mid-implementation, Claude finds that a planned approach conflicts with an existing invariant (for example, a cross-platform assumption that doesn't hold). Claude stops immediately, sets `Workflow State = Product Decision`, and reports the finding rather than silently redesigning around it.

**Field-test gate.** An Issue's acceptance criteria require real-world validation (a live update, a real LAN transfer) that can't be simulated in CI. Claude sets `Workflow State = Field Test` and reports what specifically needs to be exercised and how, matching the standard this project has used for every v0.4.0 field validation.

**Product-decision gate.** A new sub-issue's scope genuinely isn't decided yet (for example, the first v0.5 architecture-planning issue). It starts life as `Product Decision`, not `Backlog` or `Ready` — Claude does not attempt to resolve it unilaterally.

**Release gate.** Even a clean `continue` after a packaging-related PR does not, by itself, authorize creating or publishing a release. That requires the current Issue to explicitly name the release action, plus the product owner's own deliberate instruction to do it.

## Command reference

Read the current Issue:

```powershell
gh issue view <ISSUE> `
  --repo shoemin/Palworld-Server-Manager `
  --json number,title,body,state,parent,subIssues,subIssuesSummary,labels,milestone,url
```

Read Project state (raise `--limit` well past the current item count — `item-list` silently truncates rather than erroring if the cap is too low, which would otherwise hide items past the cutoff, including possibly the current issue or higher-priority `Ready` work):

```powershell
gh project item-list 1 --owner shoemin --format json --limit 500 `
  --jq '.items[] | [.id, .content.number, .["workflow State"], .priority, .["work Type"], .area, .["target Release"], .effort, .validation] | @tsv'
```

Note the field-name casing in the JSON output — `"workflow State"`, `"work Type"`, `"target Release"` — that's GitHub's own API, not a typo. This uses `--format json --jq` rather than `item-list`'s `--field` flag, since `--field` support on `item-list` is comparatively recent and not guaranteed present in every `gh` CLI install. The first column, `.id`, is the item's node ID.

Set a Project item's field with the node-ID form — this repository does not pin a minimum `gh` version, and the node-ID form works on any reasonably recent install, so treat it as the required path. Field IDs and their single-select option IDs come from `field-list`'s `--format json` output (the bare/table form only shows field IDs, not option IDs); the item ID is the `.id` column from the project-state query above:

```powershell
gh project field-list 1 --owner shoemin --format json `
  --jq '.fields[] | select(.name=="Workflow State") | {id, options}'
```

```powershell
gh project item-edit --project-id <PROJECT_NODE_ID> `
  --id <ITEM_ID> `
  --field-id <FIELD_ID> `
  --single-select-option-id <OPTION_ID>
```

If the installed `gh` CLI is new enough (check `gh project item-edit --help` for `--url` and `--field` among its flags), the same change can be made more concisely, with no node IDs at all:

```powershell
gh project item-edit 1 --owner shoemin `
  --url https://github.com/shoemin/Palworld-Server-Manager/issues/<ISSUE> `
  --field "Workflow State" --value "In Progress"
```

### Field/option ID snapshot (as of setup — always re-fetch live, treat this as reference only)

- Project node ID: `PVT_kwHOCv-qWc4Bh2Pa`
- `Workflow State` (`PVTSSF_lAHOCv-qWc4Bh2PazhgwcR0`): Backlog `6559bd32`, Ready `720f08ac`, In Progress `a4e60bc2`, Review Required `794f7919`, Changes Required `0e5aef97`, Product Decision `632423f4`, Field Test `667da291`, Done `5acb1b72`
- `Priority` (`PVTSSF_lAHOCv-qWc4Bh2PazhgwcU4`): P0 `def694bf`, P1 `bcae608b`, P2 `6210cd00`, P3 `97f56ade`
- `Work Type` (`PVTSSF_lAHOCv-qWc4Bh2PazhgwcV4`): Epic `38ee3a95`, Feature `edc9d240`, Task `f18485ca`, Bug `584e0a3e`, Investigation `3fe08bf3`, Hardening `77a6bffb`, Documentation `4cafa008`
- `Area` (`PVTSSF_lAHOCv-qWc4Bh2PazhgwcWA`): Planning `d9f60980`, UI/UX `6506c375`, Platform `2bdce103`, Core `61e10b34`, Linux `6d7dddd1`, Windows `d2ed7d51`, Server Lifecycle `650aaf1a`, LAN `a0312636`, Dashboard `d575f497`, Updates/Packaging `d27511b9`, Docs/QA `b9168779`
- `Target Release` (`PVTSSF_lAHOCv-qWc4Bh2PazhgwcX4`): v0.5.0 `f974a2b6`, v0.4.x `9b9550a3`, Later / Unscheduled `dbc9c46f`
- `Effort` (`PVTSSF_lAHOCv-qWc4Bh2PazhgwcYQ`): XS `313d3cf9`, S `dcf9164c`, M `c6918d81`, L `d0faf4aa`, XL `892bb751`, Unknown `45b41a85`
- `Validation` (`PVTSSF_lAHOCv-qWc4Bh2PazhgwcYU`): Not Required `3053092a`, Automated Pending `8122a204`, Automated Passed `5cb4d236`, Field Pending `37a2206b`, Field Passed `a3212d90`

## Known API limitation: view group-by configuration

GitHub's public GraphQL API (`createProjectV2View` / `updateProjectV2View`) does not currently expose a way to set a board view's group-by field programmatically — only `name`, `layout`, `filter`, and `visibleFieldIds` are settable. The **Workflow Board** view was created via the API with the correct layout and visible fields, but its board-column grouping needs a one-time manual switch from the default `Status` field to `Workflow State` in the GitHub UI (Project → view menu → "Group by"). This is a genuine API limitation, not an oversight.
