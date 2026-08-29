# CLAUDE.md

This is the concise, mandatory execution contract Claude must read before working on this repository. It is enforced alongside, not instead of, ordinary engineering judgment.

- Repository: `shoemin/Palworld-Server-Manager`
- Canonical GitHub Project: **Palworld Server Manager Development**, project number **1**, owned by `shoemin` — https://github.com/users/shoemin/projects/1
- Full human-readable process: [`docs/developer/agent-workflow.md`](docs/developer/agent-workflow.md)

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

## B. Begin work

Claude may begin only when:

- `Workflow State = Ready`, OR
- it is continuing its already-authorized `In Progress` issue.

Set it by issue URL and field name — this is the simplest reliable form and needs no node IDs at all:

```powershell
gh project item-edit 1 --owner shoemin `
  --url https://github.com/shoemin/Palworld-Server-Manager/issues/<ISSUE> `
  --field "Workflow State" --value "In Progress"
```

For scripted/machine use, the node-ID form also works (project ID, item ID from the `.id` column above, field ID and option ID from `gh project field-list 1 --owner shoemin --format json`):

```powershell
gh project item-edit --project-id <PROJECT_NODE_ID> `
  --id <ITEM_ID> `
  --field-id <WORKFLOW_STATE_FIELD_ID> `
  --single-select-option-id <IN_PROGRESS_OPTION_ID>
```

(A snapshot of this project's field/option IDs as of setup is recorded in `docs/developer/agent-workflow.md` for reference, but always re-fetch live before using one — treat any cached copy as potentially stale.)

Never start work sitting in `Backlog`, `Product Decision`, `Review Required`, `Changes Required`, or `Field Test` without the corresponding authorization/action first.

## C. Normal implementation

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

## D. Unexpected findings

If a new finding affects product behavior, architecture, data/save safety, a security boundary, release strategy, cross-platform direction, or feature scope: **STOP.**

Set the current item's Workflow State to `Product Decision` or `Review Required`, whichever fits, and report it. Do not silently redesign around it.

Routine implementation mistakes do not require escalation.

## E. PR / Codex gate

Every implementation PR must:

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

## F. ChatGPT review checkpoint

When the current Issue's defined review gate is reached, set `Workflow State = Review Required` and **STOP**. Return a report suitable for the product owner to paste directly to ChatGPT (see the required report structure in `docs/developer/agent-workflow.md`).

End the report with:

> ChatGPT review required. Please send this report to ChatGPT. If ChatGPT returns PASS with no changes, reply `continue`. If ChatGPT requires changes, paste the revised ChatGPT plan/prompt instead.

Do **not** continue while waiting.

## G. Required review report

Must include: Issue (number/title/parent/milestone), Project (Workflow State/Priority/Target Release), Git (base/branch/exact 40-char HEAD/working tree), PR (number/URL/changed files/CI/docs/Codex attempts/Codex final reviewed SHA/unresolved threads), Implementation (acceptance criteria/what changed/tests/docs impact/field validation/unexpected discoveries/known limitations), and Next (exact action authorized by the Issue after PASS, next dependency/issue if applicable).

## H. Meaning of "continue"

The product owner replying `continue` means: ChatGPT reviewed the preceding checkpoint and found no changes requiring a new plan. It authorizes Claude to perform **only** the next action already defined by the current approved Issue's "After PASS" section.

Before continuing:

- refresh GitHub;
- verify PR HEAD has not changed;
- verify CI remains green;
- directly re-check all Codex response surfaces;
- ensure no new unresolved review finding exists.

If the Issue says the next action after PASS is merge, Claude may merge the accepted PR. After merge: sync main, clean the local/remote feature branch when appropriate, update Project state, allow GitHub's issue-closing linkage to close the Issue where applicable, and verify the repository is clean. Then refresh the Project.

Claude may automatically begin the **next** item only if it is `Workflow State = Ready`, all its dependencies are satisfied, and it is within the already-authorized plan. If multiple `Ready` items exist, choose by Priority, then established plan/dependency order. If no `Ready` item exists, **STOP**. If the next item is `Product Decision`, **STOP** and tell the product owner: "Planning discussion required before further implementation."

`continue` does **not** authorize: new scope; a new release/tag; deleting history; moving an unplanned `Backlog` item to `Ready`; resolving a `Product Decision`; overriding acceptance criteria.

## I. ChatGPT "changes required"

If the product owner supplies a revised ChatGPT plan/prompt, do not merely implement it from chat memory. First synchronize the canonical GitHub plan: update the current issue body, acceptance criteria, dependencies, sub-issues, and Project metadata as instructed, and record a concise issue comment explaining the plan revision. Then set `Workflow State = In Progress` and execute the revised plan. After completion, return to the normal review checkpoint. This loop repeats until PASS.

## J. Product Decision

When requirements themselves are unresolved, set `Workflow State = Product Decision` and **STOP**. Do not invent a product choice — the product owner and ChatGPT will decide what happens next.

## Release guardrail

No phrase like `continue` implicitly authorizes creation or publication of a release unless the current approved Issue explicitly names that release action and the product owner has deliberately authorized it.
