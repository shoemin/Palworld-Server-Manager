# CLAUDE.md

This is the concise, mandatory execution contract Claude must read before working on this repository. It is enforced alongside, not instead of, ordinary engineering judgment.

- Repository: `shoemin/Palworld-Server-Manager`
- Canonical GitHub Project: **Palworld Server Manager Development**, project number **1**, owned by `shoemin` — https://github.com/users/shoemin/projects/1
- Full human-readable process: [`docs/developer/agent-workflow.md`](docs/developer/agent-workflow.md)
- Canonical v0.5 invariant registry: [`docs/developer/v0.5-invariants.md`](docs/developer/v0.5-invariants.md) — normative for all v0.5 work

The **GitHub Issue** and **Project** are authoritative. Do not execute a remembered conversational plan that is absent from the authorized Issue.

## A. Load current context

Before starting work:

```powershell
git fetch origin --tags --prune
git status
git branch --show-current
```

Read the current GitHub Issue:

```powershell
gh issue view <ISSUE> `
  --repo shoemin/Palworld-Server-Manager `
  --json number,title,body,state,parent,subIssues,subIssuesSummary,labels,milestone,url
```

Read the Project state (raise `--limit` well past the current item count — this project will keep growing, and `item-list` silently truncates rather than erroring if the cap is too low):

```powershell
gh project item-list 1 --owner shoemin --format json --limit 500 `
  --jq '.items[] | [.id, .content.number, .["workflow State"], .priority, .["work Type"], .area, .["target Release"], .effort, .validation] | @tsv'
```

(Note the field-name casing in the JSON output: `"workflow State"`, `"work Type"`, `"target Release"` — GitHub's API, not a typo. Using `--format json --jq` rather than `item-list`'s `--field` flag keeps this working across `gh` CLI versions, since `--field` support on `item-list` is comparatively recent. The first column, `.id`, is the item's node ID — needed below.)

For any v0.5 work, also read `docs/developer/v0.5-invariants.md` (or the version on the current issue's branch, if it has one) — the issue's own "applicable invariants" list is a pointer into that registry, not a substitute for reading it.

## B. What `Ready` actually means

A Ready **execution** issue must contain, at minimum:

1. one bounded objective;
2. explicit parent/Epic;
3. satisfied dependencies;
4. applicable invariant IDs (from `docs/developer/v0.5-invariants.md`, for v0.5 work);
5. exact authorized scope;
6. explicit out-of-scope items;
7. exact acceptance criteria;
8. required tests/validation commands;
9. allowed implementation/architecture decisions;
10. Product Decision / stop conditions;
11. a defined review checkpoint;
12. an exact `After PASS` action.

**A parent Epic is never itself an executable Ready unit**, even if its `Workflow State` happens to say `Ready` — an Epic is a container tracking bounded children, and Claude executes the children, never the Epic as one task. If an issue reads like "implement this whole subsystem," it needs decomposing (§N below) before it's actually Ready, regardless of its Workflow State label.

Claude may begin only when:

- `Workflow State = Ready` **and** the issue satisfies the twelve points above, OR
- it is continuing its already-authorized `In Progress` issue.

The node-ID form is the **required, canonical** path — the repository does not pin a minimum `gh` CLI version, and this form works on any reasonably recent install. Field IDs and their single-select option IDs come from `gh project field-list 1 --owner shoemin --format json`; the item ID is the `.id` column from the Project-state query in §A:

```powershell
gh project item-edit --project-id <PROJECT_NODE_ID> `
  --id <ITEM_ID> `
  --field-id <WORKFLOW_STATE_FIELD_ID> `
  --single-select-option-id <IN_PROGRESS_OPTION_ID>
```

(A snapshot of this project's field/option IDs as of setup is recorded in `docs/developer/agent-workflow.md` for reference, but always re-fetch live before using one — treat any cached copy as potentially stale.)

Only after directly verifying that `gh project item-edit --help` exposes `--url` and `--field` on the installed `gh` CLI may the shorter URL/field-name form be used instead, as an optional convenience — never assume it's available:

```powershell
gh project item-edit 1 --owner shoemin `
  --url https://github.com/shoemin/Palworld-Server-Manager/issues/<ISSUE> `
  --field "Workflow State" --value "In Progress"
```

Do not present this form as universally reliable — PR #32's Codex review found these convenience flags are version-dependent.

Never start work sitting in `Backlog`, `Product Decision`, `Review Required`, `Changes Required`, or `Field Test` without the corresponding authorization/action first.

## C. Mandatory preflight / impact analysis

Before editing anything, report internally/in the current work log:

- applicable invariant IDs;
- projects/files/layers expected to change;
- dependency-direction changes, if any;
- authority-creation paths affected;
- credential holders/consumers affected;
- persistence writers affected;
- operation executors affected;
- cross-section consequences that must be rechecked once the change is made.

For security/authority-sensitive work, enumerate **all** paths to the effect, not merely the section being edited — e.g. all grant-creation paths, all private-key holders, all remote-operation-initiation paths. #19's repeated correction rounds happened because individual sections were fixed correctly while a sibling section that depended on the same fact was missed; this step exists specifically to catch that class of mistake before it's written down, not after a reviewer finds it.

If the preflight discovers an unresolved product/architecture decision outside the issue's allowed decisions, **STOP** and use Product Decision (§M) rather than inventing one.

## D. Normal implementation

Claude may autonomously:

- fix compile errors caused by its current work;
- fix tests caused by its current work;
- make bounded refactors necessary to satisfy the issue;
- update tests;
- update docs required by the issue;
- retry normal verification.

Claude must **not** autonomously:

- expand product scope;
- change frozen architecture decisions;
- create a new major dependency;
- alter release strategy;
- silently defer acceptance criteria;
- remove features to make migration easier;
- change another planned issue's scope;
- begin an unrelated Ready item before the current issue is finished.

## E. Unexpected findings

If a new finding affects product behavior, architecture, data/save safety, a security boundary, release strategy, cross-platform direction, or feature scope: **STOP.**

Set the current item's Workflow State to `Product Decision` or `Review Required`, whichever fits, and report it. Do not silently redesign around it.

Routine implementation mistakes do not require escalation.

## F. Mandatory full invariant audit before Review Required

Claude must not enter Review Required merely because the requested delta was implemented.

Before the checkpoint (§G), audit the resulting **whole** — not just the change just made — against every applicable invariant, and report a compact matrix:

| Invariant | Why affected | Evidence/check | Result |
|---|---|---|---|

Also perform a stale/contradictory-reference search appropriate to the change (grep for terms/patterns the change should have touched everywhere, not just where it was deliberately edited — this is exactly the kind of check that would have caught the `#3. Local client transport` heading accidentally dropped during a #19 correction round, and the "asymmetric completion is safe" claim that survived one correction round after the fact it depended on had changed).

When a change touches both `CLAUDE.md` and `docs/developer/agent-workflow.md`, explicitly diff every duplicated executable command and compatibility claim between the two for **semantic** equivalence, not just prose intent — a #33 review round caught `CLAUDE.md` presenting the version-dependent `gh project item-edit --url/--field` form as primary while `agent-workflow.md` correctly required the portable node-ID form, which individually-correct edits to each file had let drift apart.

**Any failed applicable invariant means the issue remains `In Progress`/`Changes Required` — it does not move to `Review Required`.**

## G. Push-before-ChatGPT-review checkpoint (canonical)

This is the standard checkpoint sequence for every issue, replacing a direct implement→PR flow:

1. implement the bounded issue;
2. run required validation (§ issue's own test/validation commands);
3. run the full invariant audit (§F);
4. commit;
5. **push the issue's branch to origin** — no PR yet;
6. set `Workflow State = Review Required` and **STOP**;
7. ChatGPT reviews the exact pushed branch (not a PR — none exists yet);
8. a PASS/`continue` at this stage authorizes **only** opening the PR and entering CI + the robust Codex review protocol (§H);
9. once PR CI and a valid Codex review are clean, return to `Review Required` **again** for a second, final ChatGPT review;
10. a second PASS/`continue` may authorize merge **only if** the issue's own `After PASS` section explicitly says so.

Once a PR exists, every further authorized correction is committed and pushed to that same PR branch — never a new branch, never a fresh PR for the same issue.

Return a report suitable for the product owner to paste directly to ChatGPT (§I for the pre-PR report; §J for the PR/Codex report). End every such report with:

> ChatGPT review required. Please send this report to ChatGPT. If ChatGPT returns PASS with no changes, reply `continue`. If ChatGPT requires changes, paste the revised ChatGPT plan/prompt instead.

Do **not** continue while waiting.

## H. PR / Codex gate

Reached only after the first pre-PR PASS (§G, step 8). Every PR must:

- link its issue (`Closes #<issue>` when merge should complete it);
- run required CI (and docs CI when applicable);
- use the robust Codex review protocol below;
- incorporate valid Codex findings;
- credit `@chatgpt-codex-connector[bot]` only for actual findings Codex made.

**Robust Codex review protocol** — check ALL four surfaces, every time:

1. top-level PR issue comments;
2. PR review objects;
3. inline review comments;
4. review threads.

A clean review may appear only as a top-level comment. A findings-bearing review may appear as a review object plus inline comments. A non-review response does **not** count.

If the first Codex response is not a valid review, or no valid review arrives within a reasonable polling window, request `@codex review` **one additional time**. Maximum for one unchanged head: **2 attempts**. Any pushed fix changes the head and resets the protocol.

A clean Codex result must resolve its reported short SHA to the exact current 40-character PR head (`git rev-parse <short-sha>`). Do not merge merely because one GitHub review endpoint is empty.

## I. Required pre-PR review report (§G checkpoint)

Must include: exact 40-character pushed HEAD; confirmation the origin branch matches; exact changed files; acceptance-criteria mapping; the full invariant-audit matrix (§F); validation results; unexpected findings; clean/dirty working tree; and the explicit next action after PASS (which, at this stage, is always "open the PR" — never merge).

## J. Required PR/Codex review report (§H checkpoint)

In addition to everything in §I, restated against the actual PR: Issue (number/title/parent/milestone), Project (Workflow State/Priority/Target Release), Git (base/branch/exact 40-char HEAD/working tree), PR (number/URL/changed files/CI/docs), the robust Codex protocol evidence (attempts, all four surfaces checked, final reviewed SHA resolved and matched, unresolved thread count), Implementation (what changed/tests/docs impact/field validation/unexpected discoveries/known limitations), and Next (the issue's exact `After PASS` action).

## K. Meaning of "continue"

The product owner replying `continue` means: ChatGPT reviewed the preceding checkpoint and found no changes requiring a new plan. Under the two-stage model (§G), which PASS this is matters:

- **First `continue`** (after the §I pre-PR report): authorizes opening the PR and running CI + the robust Codex review protocol (§H) — nothing more. Do not merge.
- **Second `continue`** (after the §J PR/Codex report): may authorize merge, but **only if** the issue's own `After PASS` section explicitly names merge as the next action.

Before acting on either:

- refresh GitHub;
- verify the branch/PR HEAD has not changed since the report was written;
- verify CI remains green (PR stage only);
- directly re-check all Codex response surfaces (PR stage only);
- ensure no new unresolved review finding exists.

If the Issue says the next action after the second PASS is merge, Claude may merge the accepted PR. After merge: sync main, clean the local/remote feature branch when appropriate, update Project state, allow GitHub's issue-closing linkage to close the Issue where applicable, and verify the repository is clean. Then refresh the Project.

Claude may automatically begin the **next** item only if it is `Workflow State = Ready` **and** actually satisfies §B's twelve-point contract, all its dependencies are satisfied, and it is within the already-authorized plan. If multiple `Ready` items exist, choose by Priority, then established plan/dependency order. If no `Ready` item exists, **STOP**. If the next item is `Product Decision`, **STOP** and tell the product owner: "Planning discussion required before further implementation."

`continue` does **not** authorize: new scope; a new release/tag; deleting history; moving an unplanned `Backlog` item to `Ready`; resolving a `Product Decision`; overriding acceptance criteria; merging on a first-stage PASS; opening a PR before the first-stage pre-PR review has actually happened.

## L. ChatGPT "changes required"

If the product owner supplies a revised ChatGPT plan/prompt, do not merely implement it from chat memory. First synchronize the canonical GitHub plan: update the current issue body, acceptance criteria, dependencies, sub-issues, and Project metadata as instructed, and record a concise issue comment explaining the plan revision. Then set `Workflow State = In Progress` and execute the revised plan. After completion, return to whichever checkpoint was actually active — never unconditionally to §G:

- **No PR exists yet**: return to the pre-PR checkpoint (§G) — implement, validate, invariant audit, commit, push the same branch, `Review Required`.
- **A PR already exists for this issue**: push corrections to that same PR branch — never a new branch or a fresh PR — and return to the PR/Codex checkpoint (§H), reporting per §J. Do not follow §G's no-PR steps once a PR exists; there is no PR to "not open."

This loop repeats until PASS.

## M. Product Decision

When requirements themselves are unresolved, set `Workflow State = Product Decision` and **STOP**. Do not invent a product choice — the product owner and ChatGPT will decide what happens next.

## N. Issue decomposition

- Large roadmap items remain Epics/containers (§B) — they are never themselves Ready execution units.
- Claude executes bounded child issues/vertical slices only.
- One issue should normally change one coherent architectural seam, or one coherent user-visible vertical slice — not several unrelated ones bundled because they happen to share a milestone.
- Do not combine unrelated platform, security, persistence, migration, and UI subsystems into a single issue merely for scheduling convenience.
- Only the next dependency-satisfied child issue(s) become Ready — decomposing an Epic doesn't make every child Ready simultaneously.

## Release guardrail

No phrase like `continue` implicitly authorizes creation or publication of a release unless the current approved Issue explicitly names that release action and the product owner has deliberately authorized it.
