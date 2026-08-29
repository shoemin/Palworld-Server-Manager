# Agent development workflow

Palworld Server Manager's post-v0.4.0 development uses a deliberately explicit planning and execution system, so that AI-assisted implementation stays traceable, reversible at every checkpoint, and never silently exceeds what's actually been authorized. This page is the full human-readable explanation; [`CLAUDE.md`](https://github.com/shoemin/Palworld-Server-Manager/blob/main/CLAUDE.md) at the repository root is the concise contract Claude reads before working.

The canonical Project is **Palworld Server Manager Development** (project number 1, owned by `shoemin`): [github.com/users/shoemin/projects/1](https://github.com/users/shoemin/projects/1).

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

## The control loop

```
Plan
  -> Claude executes
  -> defined review checkpoint
  -> product owner sends report to ChatGPT
  -> PASS = product owner says "continue"
  -> Claude resumes the already-authorized plan
```

If ChatGPT finds problems:

```
ChatGPT revises the plan
  -> product owner pastes that plan to Claude
  -> Claude updates the GitHub issue/project to reflect the revised plan
  -> Claude executes the corrected plan
  -> review repeats
```

If the next step needs a new product decision:

```
STOP
  -> Workflow State = Product Decision
  -> product owner + ChatGPT planning discussion
```

## What `continue` means — and doesn't

The product owner replying `continue` means ChatGPT reviewed the preceding checkpoint and found no changes requiring a new plan. It authorizes Claude to perform **only** the next action already defined by the current approved Issue's "After PASS" section — nothing broader.

Before actually continuing, Claude re-verifies: the PR HEAD hasn't changed since the report was written, CI is still green, and all four Codex surfaces have been directly re-checked with no new unresolved finding.

`continue` does **not** authorize: new scope, a new release/tag, deleting history, moving an unplanned `Backlog` item straight to `Ready`, resolving a `Product Decision` on Claude's own judgment, or overriding acceptance criteria.

**Release rule:** no phrase like `continue` implicitly authorizes creating or publishing a release. That only happens when the current approved Issue explicitly names that release action and the product owner has deliberately authorized it.

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

**Happy path.** Issue is `Ready`. Claude sets it `In Progress`, implements, opens a PR, gets CI green and a clean Codex review, sets `Review Required`, and reports. Product owner pastes the report to ChatGPT, gets PASS, replies `continue`. Claude re-verifies nothing changed, merges (since the Issue's "After PASS" says to), syncs `main`, updates Project state to `Done`.

**Review finds an issue.** Same as above, but ChatGPT's review of the checkpoint report finds a gap the PR didn't cover. Product owner pastes ChatGPT's revised plan. Claude updates the Issue body/acceptance criteria to match, leaves a comment explaining the revision, sets `In Progress`, and executes the correction — then returns to the checkpoint.

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
