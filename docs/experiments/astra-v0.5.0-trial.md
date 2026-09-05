# Astra v0.5.0 development trial

## Baseline and isolation

- Repository: `shoemin/Palworld-Server-Manager`.
- Frozen baseline: `97e99e2bc826a1e553064d99b3e6d87ddc696920`, accepted through #40.
- Trial started: 2026-09-05, in a previously empty dedicated workspace.
- Integration branch: `experiment/astra-v0.5.0`, created and pushed at exactly the baseline.
- No normal-lane implementation branch or PR was read. Only the exact baseline was fetched.
- Canonical Issues and Project are read-only inputs; all progress below is shadow state.
- No real release, tag, package publication, or main merge is authorized.
- Input snapshots: local `.git/astra-trial-inputs/issues.json` and `project.json`; these contain issue bodies and Project metadata, not PR implementations or comments.

## Trial start planning discrepancy

The live Project marks #41 **Product Decision**, while the current issue body says **Ready**, reconciled 2026-09-04, and lists all dependencies as satisfied. The trial instruction permits shadow execution of canonical Backlog work but explicitly preserves Product Decision stop conditions. Clarification has been requested without inspecting the normal-lane PR or its review findings. Resolved by explicit Product Owner reply: use the current #41 issue body for this isolated trial. Canonical Project remains unchanged.

## Shadow dependency graph

Accepted baseline inputs: #17, #19, #33, #39, #40. Existing acceptance is inherited, not claimed as new trial validation.

| Items | Dependencies beyond accepted baseline | Initial shadow state |
|---|---|---|
| #41 Windows platform | None; Product Owner confirmed current issue body | SHADOW READY |
| #34 Design system/shell | None; readiness audit needed | NOT REACHED |
| #43 Protocol/contracts | None; readiness audit needed | NOT REACHED |
| #35, #36, #37 Design surfaces | #34 | NOT REACHED |
| #38 Final design | #34–#37 | NOT REACHED |
| #42 Local IPC/identity | #41 | SHADOW BLOCKED |
| #27 Host secure storage | #41 | SHADOW BLOCKED |
| #44 Remote transport/pairing | #42, #43, #27 | SHADOW BLOCKED |
| #45 Authorization | #42, #43, #44 | SHADOW BLOCKED |
| #28 Revocation | #27, #44, #45 | SHADOW BLOCKED |
| #46 Durable operations | #43, #45 | SHADOW BLOCKED |
| #47 Semantic settings | #43, #46 | SHADOW BLOCKED |
| #48 Server lifecycle | #41, #42, #43, #46 | SHADOW BLOCKED |
| #49 Server data | #46, #48 | SHADOW BLOCKED |
| #50 Observability | #43, #48 | SHADOW BLOCKED |
| #51 Migration | #42, #45, #27, #28 | SHADOW BLOCKED |
| #52 Avalonia shell | #38, #41, #42, #43 | SHADOW BLOCKED |
| #53 Avalonia server workspace | #35, #36, #47–#50, #52 | SHADOW BLOCKED |
| #54 Avalonia Manager Settings | #37, #38, #44–#46, #27, #28, #52 | SHADOW BLOCKED |
| #55 Windows parity | All required design/implementation children | SHADOW BLOCKED |
| #21 Linux Epic | #55; bounded decomposition after gate only | NOT REACHED |
| #22 Packaging | #20, #21; bounded decomposition required | NOT REACHED |
| #23, #24, #25 Qualification/docs/release | Final platform and validation gates | NOT REACHED |
| #16, #18, #20 | Containers only, never executed wholesale | NOT REACHED |

## #41 preflight

Canonical issue: **#41 — Windows platform slice — Host service lifecycle, activation, and client platform services**, parent #20, milestone v0.5.0.

- Dependencies #33/#19/#39/#40 are accepted at the baseline.
- Expected production files/layers: Host composition root; Platform.Contracts/Windows; Client.Platform.Contracts/Windows. SelfTest references/target and explicit integration harness/CI are authorized test changes.
- No production project-reference topology change is permitted. App/Core/Lan remain frozen for this issue.
- Authority paths to inspect: service provisioning, service DACL, query/start client path, privileged lifecycle/boot configuration, Host-data directory ACL, startup mutex acquisition, SQLite open/migration, deterministic stop cleanup.
- Credential holders: per-user DPAPI store only, shared by ordinary clients; injected key generator with no production signature choice. Host machine secrets remain #27.
- Persistence writers: #40 HostDatabase under #40 HostExclusivityLock only; per-user client credential storage is separate from authoritative Host state.
- Operation executors: SCM lifecycle and bounded client shell launch only. No server operations, IPC/authentication, Owner issuance, Linux, packaging-layout choice, or idle policy.
- Cross-section audit: group membership grants transport/start eligibility only; never Owner or enrollment; service start is not Host authentication; uninstall cannot remove authoritative data; boot-start and login-start remain separate.
- Applicable invariants: ARCH-001/002, HOST-001/002, PERSIST-001, CLIENT-001/002/003, LOCAL-002/003/004, OWNER-002, SEC-001, PLATFORM-001/002, LINUX-001. Full registry read; implementation audit not yet performed.
- Environment: current token is medium integrity, Administrators SID deny-only. Privileged integration cannot be reported as passed locally under this token. Mandatory actual service/multi-user evidence remains required.
- Branch/PR: not created yet; implementation result: preflight only.
- Tests: pending baseline run; no #41 tests exist yet.
- GitHub Codex: not requested; no reviewed implementation HEAD.
- Allowed Decisions made: none yet.
- Product Decisions: resolve Project/body discrepancy without importing normal-lane work.
- Field-test requirements: all 15 issue-specified real Windows integration criteria remain unfulfilled.
- Deviations: normal workflow review/wait/Project-mutation steps replaced only as explicitly authorized by the trial request.
- Final result: pending.

