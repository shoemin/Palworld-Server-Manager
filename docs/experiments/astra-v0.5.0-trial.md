# Astra v0.5.0 development trial

## Review model — Product Owner update, 2026-09-05

GitHub Codex external review was intentionally removed from the Astra lane by Product Owner decision, effective immediately during #41. Astra now owns implementation and two distinct substantive reviews: Pass A before PR creation and Pass B against the exact final PR HEAD. No further Codex requests, waiting, review-surface inspection, reviewed-SHA requirement, or review-attempt merge gate applies. Normal-lane process is unchanged. Scope, invariants, product-decision boundaries, actual field evidence, CI, frozen baseline, and experiment-only merges remain mandatory.

Normal and Astra review models differ; benchmark comparisons must account for this. Historical exception: before this update, #41 received one external finding already read and under correction. That evidence is retained below and is not relabeled as an Astra finding. The revised no-external-review model applies from this update forward; claiming the entire earlier history contained no external review would be inaccurate.

Each completed item records Pass A/Pass B findings, test-discovered defects, review-only defects, correction counts, and any later escaped defect. Final reporting uses **Astra review effectiveness** and identifies this workflow transition explicitly.


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
- Branch: astra/41-windows-platform; implementation result: see stage-1 record below.
- Tests: pending baseline run; no #41 tests exist yet.
- GitHub Codex: not requested; no reviewed implementation HEAD.
- Allowed Decisions made: none yet.
- Product Decisions: resolve Project/body discrepancy without importing normal-lane work.
- Field-test requirements: all 15 issue-specified real Windows integration criteria remain unfulfilled.
- Deviations: normal workflow review/wait/Project-mutation steps replaced only as explicitly authorized by the trial request.
- Final result: pending.

## #41 implementation and stage-1 audit

Branch: `astra/41-windows-platform`, from integration commit `0ccc6af`.
Product Owner explicitly authorized using the current #41 body despite the normal Project's Product Decision label.

Implemented bounded Host/client contracts, native SCM and local-group provisioning, virtual-account service lifetime, protected machine root, #40 runtime composition, query/start-only client activation, test-injected HKCU startup and Explorer integration, and atomic versioned CurrentUser-DPAPI client credentials with an injected generator. No production generator is supplied. SelfTest now targets Windows with authorized test-only references; the production reference graph/allowlist is unchanged.

Allowed decisions: stable service `PalworldServerManagerHost`, group `PalworldServerManagerUsers`; compact activation enum; version 1 encrypted JSON payload; caller-supplied test path/write seam; file-sharing lock serializes same-user clients; real logon helper in SelfTest; unique integration service/group/user names. The running Host never provisions itself. Existing registrations are not silently adopted. Groups and authoritative data are retained at service uninstall.

Self-review found and corrected an existing-state ACL edge in the new provisioner: securing a root alone does not remove explicit permissions from old child files/protected directories. The implementation rejects unsafe explicit child access and reparse paths before permitting the new service to start. A build found an ambiguous test-harness TimeoutException name; qualified it. These are Astra/test findings, not GitHub Codex findings.

| Invariant | Why affected | Evidence/check | Result |
|---|---|---|---|
| ARCH-001 | Legacy WPF boundary | No App/Core/Lan edits | PASS |
| ARCH-002 | Legacy LAN isolation | No new production references; existing architecture guards | PASS |
| HOST-001 | Machine runtime | #40 mutex held by service runtime, second runtime rejected | PASS local; SCM field pending |
| HOST-002 | Mutation executor | Service work Host-side; client shell is bounded local action only | PASS |
| PERSIST-001 | Writer lifetime | Acquire before DB open/migration; clear pool/dispose before mutex release | PASS local; service field pending |
| CLIENT-001 | Client topology | Core-independent client contracts/implementation; production guard unchanged | PASS |
| CLIENT-002 | Machine secrets | No client Host-key API | PASS |
| CLIENT-003 | Shared per-user identity | One per-user DPAPI file shared by both clients, no Host.Cli change | PASS storage; IPC deferred #42 |
| LOCAL-002 | Private-key holder | Generator/storage client-side only | PASS |
| LOCAL-003 | Group eligibility | No auto-membership, enrollment, reactivation, or grants | PASS code; real-token field pending |
| LOCAL-004 | Activation boundary | Query/start result is not authentication; no IPC/credential transmission implemented | PASS scope |
| OWNER-002 | Bootstrap authority | Handoff contract only; no bootstrap execution or Owner creation | PASS scope |
| SEC-001 | At-rest secrets | DPAPI CurrentUser over entire payload; atomic writes; no secret logging | PASS same-user; cross-user field pending |
| PLATFORM-001 | OS seams | Windows APIs confined to Windows implementations/composition and tests | PASS |
| PLATFORM-002 | Interactive actions | Explorer client-only with injected launcher and local-path validation | PASS |
| LINUX-001 | Windows-first gate | No Linux implementation | PASS |
| IDENT-001–004, LOCAL-001, OWNER-001 | Identity/recovery boundaries | No changes to HostIdentity/authentication/recovery | Unaffected |
| REMOTE-001–002, PAIR-001–004 | Remote trust/authority | No remote implementation or routes | Unaffected |
| AUTH-001–005, PROTO-001 | Grants/protocol | No grant or protocol implementation | Unaffected |
| OPS-001–004, RECOVERY-001, MIG-001 | Operations/migration | No business operations, recovery, migration changes | Unaffected |

Baseline validation: 161/161 self-tests. Final stage-1 local suite: 169/169, including all #39/#40 tests, after the ACL and cleanup audit corrections. Release build succeeds. Strict docs build succeeds. Explicit integration invocation fails at the privilege gate under this medium-integrity token before creating OS objects: **FIELD EVIDENCE REQUIRED / privileged integration pending mandatory PR CI**. This is not an integration PASS. CI now has a dedicated explicit integration job, as #41 requires; trigger branches remain unchanged. No remote run or Codex result yet.

Acceptance mapping: service runtime/account/start-type/DACL/root ACL/boot toggles/uninstall/path quoting are covered by provisioner plus the explicit SCM harness; non-admin eligibility and cross-user DPAPI are covered by actual-logon probes; local activation/credential lifecycle/quoting/runtime tests pass; #39/#40 checks remain in the full suite. All real Windows criteria require a successful integration run before Shadow Done.

Stale-reference search checked Host scaffold wording, package versions, production project references, service/account/DPAPI scopes, shell launch placement, and issue-closing phrases. No production topology change, Host-side shell, production signature selection, Linux, IPC, or frozen source edit was introduced.

## #41 external review round 1

PR #61 targets the experiment branch. Reviewed SHA `d2ecae8f6ed43c7448801710479ce7eaa0e7b4de`. All four review surfaces checked. Two requests were made after the initial five-minute window; GitHub Codex returned review `5122646739` and inline finding `3941740363` / thread `PRRT_kwDOT8td0c6fmFRi`.

Valid P1 finding credited to `@chatgpt-codex-connector[bot]`: protected descendants containing Administrator/SYSTEM-only ACLs or a service deny ACE passed the existing-state check but prevented service startup. Correction requires protected descendants to grant the service full, non-inherit-only access; deny ACLs fail closed instead of guessing group effective access. Related self-review also rejects unprivileged child owners, who can change DACLs despite lacking an explicit data-write ACE. A dedicated regression test covers protected administrator-only state, service/group denies, outsider ownership/access, and usable protected/inheritable layouts.

Original exact-head remote evidence: CI workflow run `33985777171` PASS (169/169 plus real service/multi-user integration and successful service/group/user/profile/file cleanup); Docs run `33985778847` PASS. These establish actual virtual-account identity, DACL, boot toggles, database/lock lifecycle, non-admin rights, cross-user DPAPI, and uninstall preservation. They are pre-correction evidence; fresh runs and a fresh invariant audit are required for the correction HEAD.

Full registry re-audit of the correction: same matrix above; changes affect HOST-001/PERSIST-001/SEC-001 provisioning access and preserve the three-identity Host-state boundary. No other authority creation/executor/credential path was added. Frozen source and production dependency graph remain unchanged. No new Product Decision. External re-review was subsequently removed by Product Owner authorization; correction proceeds through Astra Pass B.

## #41 Review Pass B under revised workflow

Fresh final-diff review examined all changed files against integration `0ccc6af`, including native handle/error ownership, SCM access delegation order, runtime resource cleanup, encrypted credential concurrency, path quoting, negative tests, actual CI evidence, and privileged helper cleanup. One additional Astra-only security finding: the provisioner delegated SERVICE_START before securing the data root, allowing an existing activation-group member to race installation. Corrected by completing Host-state protection before publishing the activation DACL. Default SCM permissions do not delegate start to that group. This is a Pass B finding, independent of the earlier external protected-descendant finding.

Pass A findings: explicit permissions on existing descendants could survive root-only protection; integration group cleanup needed proof of creation. Pass B findings: protected descendants' service usability/owner/deny checks (continuation of the pre-policy external finding plus related Astra owner audit), and activation-delegation ordering. Test-discovered defect: ambiguous TimeoutException type caused the first integration-harness compile failure. Correction iterations: initial Pass A corrections, post-PR ACL regression correction, and fresh Pass B provisioning-order correction. No post-merge escaped defect known (not merged yet).

The final corrected ACL and provisioning-order suite passed locally at 170/170; Release build had zero warnings/errors and strict docs passed. Provisioning-order change requires fresh final-head remote integration. Full applicable invariant matrix re-evaluated: no changes to IDs/scope; LOCAL-003 and SEC-001 now additionally require root protection before group activation rights. Review Pass B is pending final validation and exact-head recheck; no Codex re-review will be requested or inspected.



