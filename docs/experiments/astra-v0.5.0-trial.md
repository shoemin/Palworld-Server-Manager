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
| #41 Windows platform | Complete in experiment; PR #61 | SHADOW DONE |
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

## #41 acceptance record

Implementation HEAD `4fa361a3399adbf036e12a1582a6808814bf8386` passed local 170/170 tests, zero-warning Release build, strict docs, remote CI `33986519538` (ordinary suite plus dedicated real Windows integration) and Docs `33986520567`. Both remote runs report this exact SHA. Integration passed actual virtual-account identity, exact DACL, boot toggles, DB/mutex lifecycle including crash, confirmed non-admin eligibility/denied rights, cross-user DPAPI, uninstall preservation, and full cleanup. No remaining mandatory #41 field evidence.

Pass B repeated against that exact HEAD: **PASS**, complete diff and all 18 changed files reviewed, every #41 criterion mapped to local or real CI evidence above, full invariant matrix PASS, negative frozen-source/production-topology/scope search clean, no Product Decision. The production service is Windows-only with no idle policy, no Owner/bootstrap execution, no production signature selection, no Host secret store, and no server operations. Compatibility with accepted #40 is covered by the retained suite. Three correction iterations are recorded above. No known escaped defect or regression remains. Historical external finding is not a completion gate under the revised authorization.

This acceptance-record update changes documentation only; code/tests remain byte-identical to validated `4fa361a`. Final PR metadata/base/HEAD and strict docs are rechecked before merge. Shadow result: accepted for integration merge; canonical #41 remains untouched. Post-merge commit identity and dependency refresh will be appended to the integration ledger.




## Integration checkpoint after #41

PR #61 merged at ffe78fee3fd6d197308a9807e965b123a086b5de. Final PR HEAD d787a7d403f3840ae319cd77d97461d2d9852334 changes only the ledger relative to validated implementation 4fa361a; strict Docs run 33986674874 passed on that final HEAD. Pass B rechecked that exact final diff and merge base. Shadow #41 DONE; normal issue and Project untouched. Dependency refresh: #42 and #27 now satisfy their declared implementation dependencies and need readiness/impact analysis; #43 is independently in progress; #34 remains an independent design candidate. Later security/server/UI/Linux gates remain closed.


## #43 independent readiness/preflight

Canonical issue **#43 — Protocol slice — Host contracts, ServerRef identity, and version/capability negotiation**, parent #20, milestone v0.5.0. Dependencies #33/#19/#39/#40 are accepted at the frozen baseline. The bounded objective, exclusions, allowed naming/package choices, testable criteria, validation, stop conditions and review protocol are explicit in its current body. **SHADOW READY** despite canonical Backlog.

#41 has passed local/remote validation on PR #61 but is awaiting its independent reviewer after two requests. #43 has no #41 dependency and does not prejudge any Windows decision. Its branch `astra/43-protocol` starts at integration `0ccc6af`, not the unmerged #41 branch.

Preflight: change Contracts schemas, OS-neutral identity/compatibility helpers, SelfTest and protocol documentation only. No production reference topology change. Contracts runtime remains protobuf-only; Grpc.Tools is private build-time tooling. gRPC services are defined in schemas; endpoint/stub hosting belongs in subsequent transport composition. No credentials, authority-creation path, persistence writer, or operation executor is implemented. Grant DTOs express accepted types; they do not issue/evaluate grants. No arbitrary shell, general filesystem RPC, TLS, pairing, permission evaluator, business operation, UI, or Linux implementation.

Applicable invariants: CLIENT-001, IDENT-001/002, PROTO-001, REMOTE-001, AUTH-001/005, PLATFORM-001; ARCH-001/002 and all untouched security/authority families remain negative-scope checks. Audit cross-section consequences: malformed/default protobuf identities, host-qualified equality/routing/grants, unknown authorization values, incompatible-major versus additive-minor negotiation, removed-field reservation. Allowed decisions: exact schema names/field numbers, initial protocol 1.0, Google.Protobuf 3.36.1 and private Grpc.Tools 2.83.0 (verified official package listings). No product decision identified. No field evidence required by this bounded contract-only issue. PR/review/tests/result pending.


## #43 Review Pass A

Integrated accepted #41 through experiment `9d0b8e0`; only the experiment lane was merged. Resolved overlapping SelfTest references/test registrations and combined both ledger histories without dropping either issue's evidence. Full combined suite: **174/174 PASS** (170 existing Windows/legacy checks plus four protocol groups). Standalone pre-integration suite had 165 checks; counts are not additive across reruns. Production dependency topology remains unchanged.

Pass A examined every changed schema/helper/test/project/doc and its diff against the current integration branch. Findings corrected: routing assertion initially compared reference inequality instead of value equality; grant collision test varied unrelated actor fields and was made a same-grant clone differing only by Host target; schema guard needed immutable history snapshots for every added revision, and reserved fields still must not authorize removal within the same major. Strengthened negative tests cover these boundaries. Tests then found that protoc descriptor sets contain inferred JSON names and a different source-relative filename from embedded C# descriptors. Used the actual project proto path and reflection-computed JSON names to compare semantically identical full descriptors without dropping explicit JSON-name checks. A subsequent compile caught a shadowed local test variable; renamed it.

| Invariant | Why affected | Evidence/check | Result |
|---|---|---|---|
| CLIENT-001 | Contract consumers | Contracts runtime uses protobuf only, no Core/Host/platform dependency | PASS |
| IDENT-001 | Stable Host identity | HostId UUID contains no credential fingerprint | PASS |
| IDENT-002 | Wire/domain/routing/grants | Both Host/profile required; same profile on different Hosts remains distinct; malformed/default input rejects | PASS |
| PROTO-001 | Negotiation/evolution | Explicit-major comparison, immutable known-capability intersection, unknown-enum deny, product string ignored, append-only schema history with negative mutations | PASS |
| REMOTE-001 | Route shape | Host-qualified target only; no client outbound connection or transport | PASS |
| AUTH-001/005 | Grant wire shape | Distinct Host/server capability types and exact Host target; syntactic validation only, no grant issuer/evaluator | PASS |
| PLATFORM-001 | Portability | No OS APIs in Contracts | PASS |
| ARCH-001/002 | Frozen legacy | No App/Core/Lan edits or reuse | PASS |
| HOST-001/002, PERSIST-001 | Authority/persistence | No runtime executor or persistence writer added; #41 remains unchanged | Unaffected |
| CLIENT-002/003, LOCAL-001–004, OWNER-001/002, SEC-001 | Identity/security | No credentials, bootstrap/authentication, or protected channel implementation | Unaffected |
| IDENT-003/004, PAIR-001–004, REMOTE-002, AUTH-002–004 | Trust/delegation | DTOs do not rotate, pair, issue grants, or evaluate provenance | Unaffected |
| OPS-001–004, RECOVERY-001, MIG-001, PLATFORM-002, LINUX-001 | Other gates | No operations/recovery/migration/shell/Linux implementation | Unaffected |

Acceptance mapping: Core independence → architecture guards; host-qualified references/routing/grants → identity collision group; explicit version+feature gate and informational product strings → negotiation group; unknown authorization enums → serialization/negative group; no field-number reuse → immutable descriptor-history comparison plus deliberate bad-schema mutations. Full build/self-test passed after corrections. Release/docs and stale-reference search are rerun before push. Pass A correction cycles: 3 (assertion/schema strengthening, descriptor normalization, compile correction). No Product Decision or field requirement; no known escaped defect. Pass B pending PR creation under the revised Astra-only review model.

## #43 Review Pass B

PR #62, initial HEAD `cdccadcb85f42e61983873c04bf1b0b4e5931ade`, passed remote CI `33986933232` (174/174 and retained Windows service/multi-user integration) and Docs `33986934817`. Fresh Pass B inspected the complete 10-file diff, identity equality/invalid inputs, grant-versus-authority distinction, mutable protobuf offer isolation, unknown enum values, schema history, package/runtime boundaries, field gates, and interaction with #41.

Pass B found a test-coverage hole: schema evolution walked top-level messages but would not protect nested types introduced by a later additive revision. Added recursive nested-message/enum checks, reservation preservation, and an explicit nested-field reuse rejection test. Local validation after this correction: **174/174 PASS**, zero-warning Release build, strict docs. Full invariant matrix re-audited with PROTO-001's evidence strengthened; no production source change or scope expansion. Correction cycle count now 4; Pass B repeated after correction is clean, pending applicable remote validation. No escaped/after-merge defect known. No external reviewer used for #43.

## #27 preflight decision packet

**SHADOW PRODUCT DECISION REQUIRED** — #27 Host secure storage, parent #20. Declared dependencies now satisfied through #41. Accepted #19 section 7 describes encrypted-blob storage as readable only by the specific service account, while sections 2c/5a require privileged offline Host.Cli bootstrap/recovery to use the secure store. The exact ACL treatment of that exceptional caller is not explicit, and #27's Allowed Decisions name blob layout/key naming without authorizing a security-boundary change.

Decision requested: (A, recommended) explicitly grant the dedicated service plus elevated Administrators/SYSTEM access, matching #41's Host-root identities and #19's stated privileged-machine-owner limitation; or (B) retain a service-only DACL and separately specify the exceptional privileged access mechanism used by offline recovery. A avoids inventing impersonation/ACL-takeover behavior and still excludes ordinary users/clients; B makes direct file access narrower but requires a defined privileged recovery path. Only #27's Windows store/security integration is blocked; #43 and bounded UI design can continue without prejudging this choice. No #27 implementation has begun and no canonical issue/Project has been mutated. Product Owner input requested; unanswered is not approval.

## #43 acceptance record

Final implementation and reviewed PR HEAD `22f21020c400a5e8ec61e049c63cec0587531df5` passed local 174/174 self-tests, zero-warning Release build, strict documentation build, remote CI `33987200947` (including retained real Windows integration) and Docs `33987202883`. Both remote runs completed successfully at that exact SHA. Repeated Pass B is clean after the recursive schema-guard correction; the full invariant matrix and scope/contradiction search remain clean. No mandatory field evidence or Product Decision applies to this contract-only slice. Four correction cycles; no known escaped defect or regression. No external reviewer used for #43.

This acceptance record is documentation-only. Code and tests remain identical to the validated SHA. PR #62 targets only `experiment/astra-v0.5.0`; merge identity and refreshed dependency status will be recorded after merge. Canonical #43 and its Project state remain unchanged.
## Integration checkpoint after #43

Shadow #43 DONE. PR #62 merged at `ee8e558a12cad12b8108a511cadccf613730677a`; final PR HEAD `16233206433dca4d5243ee4fc1c157851d066f1e` differed from tested `22f2102` only by its acceptance record. Strict Docs `33987906177` passed on final HEAD. Canonical planning remained untouched. Dependency refresh: #34 is independently eligible; #27 awaits its explicit ACL Product Decision; #42 needs bounded preflight and secure-channel dependencies; later #44+ and production UI/Linux gates remain closed.
## #34 readiness and preflight

#34 — UI design slice: design system, application shell, and unified server rail; parent #18, v0.5.0. Dependencies #33/#19 accepted at the frozen baseline. Current canonical issue body reread: bounded design-only scope, explicit exclusions and six testable criteria; shadow authorization supplies autonomous two-pass review. SHADOW READY on branch `astra/34-shell-design`, from integration `41fc7bf`.

Expected changes: only `docs/design/astra-34/` vector prototypes, token data, deterministic generator and design specification, plus this ledger. No production project/dependency change, authority creation, credential consumer, persistence writer or operation executor. Applicable IDs: ARCH-001/002, HOST-001/002, CLIENT-001/002/003, IDENT-001/002, REMOTE-001/002, PAIR-001/002, AUTH-001/003/005, PROTO-001, OPS-001–004, PLATFORM-002, SEC-001. Remaining registry families audited as negative scope. Recheck all routes/labels for exact Host context, permission-filtered inventory, trust-versus-authority distinction, stale/error states, and no client-owned mutation. No detailed settings, permissions, Activity flows, production Avalonia or Linux.

Allowed decisions: 4-unit spacing scale, semantic palette, type hierarchy, project-owned vector icons/angular decoration, responsive dimensions/focus/motion guidance. Validation plan: generated 1600×900 and 2100×900 boards, 800×900 collapsed board, component/state board and token alternatives; inspect rendered boards for clipping/identity/context; programmatic SVG/XML, palette contrast, fixture identity uniqueness and reproducibility checks; strict docs; two distinct full-diff/invariant reviews. Accepted canonical #18 textual direction has been read. Canonical image browser capture repeatedly times out; no successful visual comparison is claimed. If still unavailable at acceptance, record the exact visual-evidence limitation, not a fabricated PASS.
## #34 Review Pass A

All design sources and seven rendered SVG boards inspected, including the canonical #18 image (successfully viewed at natural size using the in-app browser; the earlier Edge capture limitation is resolved). Six acceptance criteria map respectively to duplicate-name/ID-prefix fixtures and full identity expansion; 1600×900 plus 2100×900 boards; 800×900 collapsed and keyboard-identity boards; shared token/generator components; identical-layout Dark/Light palette boards; explicit Host-only authority and filtered-inventory specification. No production source is changed.

Review-only findings: neutral control borders initially measured 2.70:1 on the flagship surface despite passing text checks; strengthened the border token and expanded checks to all surfaces. Collapsed identity behavior initially existed only in prose; added a separate focused identity board with both full UUIDs. Several core dimensions were literal in the generator; bound core chrome/rail/drawer/control/corner/title dimensions to their shared token values. One correction iteration covers these related foundation/evidence changes. No automated-test defect before review, no escaped defect known.

Validation: seven deterministic SVG/XML/reproducibility checks PASS; text contrast >=4.5:1 and interactive boundary/focus contrast >=3:1 for all three palettes PASS; exact fixture Host/profile pairs distinct PASS; strict MkDocs PASS. Visual review found no clipped labels or overlapping controls in the demonstrated layouts; distinct local/remote target context remains visible. Static SVGs do not establish live keyboard/screen-reader behavior or final cross-surface acceptance; those remain #38/production validation, not required evidence waived from #34.

| Invariant | Why affected | Evidence/check | Result |
|---|---|---|---|
| ARCH-001/002, CLIENT-001 | Design-only boundary | Only docs/design and ledger; no frozen/source/dependency edits | PASS |
| HOST-001/002, PERSIST-001 | Shell command placement | Host alone owns inventory/mutations; no executable process/filesystem/store path | PASS |
| CLIENT-002/003, LOCAL-001–004, OWNER-001/002, SEC-001 | Client authority and secrets | No keys, bootstrap, enrollment, recovery, or privilege shortcut; synthetic public IDs only | PASS / unaffected mechanisms |
| IDENT-001/002 | Collision-safe presentation | Stable semantic UUIDs; duplicate Host/server names and prefixes; exact pair selection and full details | PASS |
| IDENT-003/004 | Rotation/recovery | No credential fingerprint substituted for HostId; no recovery design | Unaffected |
| REMOTE-001/002, PAIR-001/002 | Remote navigation | Local-Host route; only authorized inventory; trust never entitlement; no PeerBound ordinary management | PASS |
| PAIR-003/004 | Trust transactions/replacement | No pairing/replacement flow | Unaffected |
| AUTH-001–005 | Authority creation | No grants, presets, scopes or delegation engine; full target retains authoritative Host | PASS / unaffected mechanisms |
| PROTO-001 | Capability presentation | Unsupported actions retain reason; no app-version inference | PASS |
| OPS-001–004, RECOVERY-001 | Activity/lifecycle summaries | No stale write or client-owned operation; closing drawer never releases/cancels work; detailed flow deferred | PASS / unaffected mechanisms |
| MIG-001 | Add/Import entry | Placement only; no copy/move/merge semantics | Unaffected |
| PLATFORM-001/002, LINUX-001 | OS boundary | Static project-owned vectors, no shell execution/Avalonia/Linux production | PASS |

Negative/contradictory-reference review checked remote routing, grants/trust, full identity, stale status, Files/Logs versus bounded parity, #35–#38 ownership, client mutation, private keys, import targeting, theme reuse, and final-acceptance claims. No Product Decision or mandatory field gap identified within this bounded design slice. Pass A clean; next action is PR creation into the experiment branch, then fresh Pass B.
## #34 Review Pass B and acceptance

PR #63 final design HEAD `9aba886d7f8ed23ce9c54a31483292cc5e92e0c7`. Fresh post-PR review covered the complete 11-file diff, every vector/source/token/document, all six criteria, collision/alias behavior, unavailable/remote actions, token contrast, layout/focus evidence, retained #41/#43 boundaries and all invariant families above. No additional defect found. No Product Decision and no mandatory unperformed #34 field criterion; static evidence is explicitly not live accessibility or #38 final design acceptance. No external reviewer used.

Local generator/reproducibility/contrast/identity checks and strict docs PASS. Remote Docs `33988354529` PASS on the exact design SHA. No production source/tests or CI workflow changed, so the existing 174-test/real-Windows evidence remains intact and is not relabeled as a new run. Pass A and Pass B clean. One correction iteration, review-only findings as recorded; no known escaped defect or regression. This acceptance update is ledger-only; final strict docs and PR HEAD/base are rechecked before integration merge.
## #34 integration checkpoint and escaped fixture defect

PR #63 merged at `9e2b551f5cf769209fdc63205c795948f2cf4b47`; final ledger-only HEAD `8f5a41467e777ec9a96e1ceda9eed64b7aa9c1d4` passed strict Docs `33988406217`. Design #34 was accepted; #35–#37 dependencies are now satisfied, subject to preflight. #27 remains Product Decision and implementation security gates remain closed.

Before starting #35, cross-checking accepted backup parity in `docs/guide/backups.md` exposed an escaped #34 fixture defect: the active backup targeted R1/Main Server while its header showed Running. Backups require a stopped server. Both reviews missed that semantic combination. Bounded follow-up `astra/34-status-fixture-fix` is rooted at the #34 merge; no production behavior is changed. Correct the active backup target to the stopped local server, replace the speculative lock-waiting example with a completed historical remote backup, and make active counts one. This also avoids pre-designing #38 lock/queue semantics. No new product decision or field evidence; full invariant audit remains the #34 matrix, with HOST-002/OPS-002 evidence corrected. Escaped-defect count for #34: 1; follow-up correction cycle: 1 (total 2).

Follow-up Pass A: reviewed generator and every regenerated board against current integration, cross-checked stopped-backup constraint and historical-versus-active status, searched all Activity/count/target copies. Same seven-board structural/contrast/reproducibility checks and strict docs required; no source or normal-lane change. No further issue identified.

Follow-up Pass B on PR #64 at `c12b1a726f5a8227c10c480f4648dcb598373c3d`: clean. Fresh full eight-file diff confirms the shared drawer changes propagate to all themes/wide layouts and narrow active counts; unrelated component/identity data is unchanged. Read-only geometry, exact targets, stopped-backup condition and completed-history distinction are consistent. Repeated full #34 invariant matrix PASS. Local design/strict-doc checks PASS; remote Docs `33988512961` PASS at that exact SHA. No additional finding. This paragraph changes only the ledger; final docs/HEAD/base verification precedes merge.
## #34 correction integration / #35 readiness

The bounded #34 fixture correction PR #64 merged at `bd947811ea14ee7901cecb674aed074a9486d131`. Final ledger-only HEAD `6a1759b715dd05170f66da5bb47b849f6227715c` passed Docs `33988558414`. Shadow #34 DONE with the escaped defect corrected; no known remaining defect. Canonical planning unchanged.

#35 — UI design slice: server workspace and Add/Import flows, parent #18, v0.5.0. Current issue body reread; dependencies #33/#19/#34 satisfied. SHADOW READY; branch `astra/35-workspace-design` from exact integration `bd947811ea14ee7901cecb674aed074a9486d131`. Scope: fleet/overview/players/metrics/backup-history and guided create/import/destination selection only. Cross-checked accepted backup/package/observability requirements in baseline guides and canonical #49/#50. No player moderation, generic REST/filesystem UI, resumable transfer or new management capability.

Preflight: new docs/design/astra-35 artifacts/spec/generator plus ledger only; reuse #34 semantic components. No production references/authority creators/private-key holders/persistence writers/executors. Applicable HOST-001/002, IDENT-001/002, CLIENT-001–003, REMOTE-001/002, PAIR-001/002, AUTH-001/003/005, PROTO-001, OPS-001–004, RECOVERY-001, SEC-001, MIG-001, PLATFORM-002; remaining full-registry families negative-scope audit. Recheck stale/live data, exact targets, permission-filtered resource visibility versus unavailable actions, stopped backup/restore, pre-restore backup, package integrity/runtime exclusion and Host-routed remote creation. No #27 ACL decision is prejudged.

Allowed choices: dense tables/chart hierarchy, single-step guided panels, secondary navigation and restore-point selection. Validation: reproducible vector boards for each authorized surface/flow, rendered visual review, #34 token checks retained, explicit criterion/state/authority mapping, strict docs and remote Docs, two-pass complete review. Detailed settings, trust/permission editor, Activity/transfer/conflict/recovery system remain #36–#38. No Product Decision identified in this preflight.
## #35 Review Pass A

Complete new-artifact review against integration `bd94781`: generator, specification and all eight rendered boards. Acceptance mapping: unified local/remote mental model → paired local/remote Overview plus exact Host rail/header; guided add/import/create → separate sequential storyboard panels with retained destination; backup history → stopped snapshot and scoped restore-point review/safety backup; useful Players/Metrics → semantic roster and unit/tick/time/gap charts; unavailable actions → explicit Not authorized/Remote unavailable/offline examples; no client authority → full target/Host-routed operations and bounded input specification.

Review-only corrections: All Servers incorrectly retained R1 selection; degraded Metrics retained a live-looking roster count in the rail; added independent local Overview evidence; replaced unlabeled schematic chart ranges with numeric ticks and integer player-count steps; added a visibly unauthorized backup action; kept stopped-state Restart unavailable; corrected inherited remote copy on the local board; added the selected opaque restore-point reference to distinguish same-time history entries; moved metric units clear of axes. Three source-edit correction iterations during Pass A. Automated structural/contrast checks found no defect before review. No known escaped #35 defect. The previously escaped #34 fixture issue is recorded separately and was corrected before this branch.

Local eight-board XML/reproducibility and shared palette checks PASS; retained #34 seven-board check PASS; strict MkDocs PASS. All boards visually inspected; updated identity/freshness compositions and local/remote layout rereviewed. Static charts/data remain explicitly synthetic; no live service/UI/accessibility/field run claimed.

| Invariant | Why affected | Evidence/check | Result |
|---|---|---|---|
| ARCH-001/002, CLIENT-001, PLATFORM-001 | Layering | New docs/design only; shared design primitives, no production reference/source change | PASS |
| HOST-001/002, PERSIST-001 | Read/mutation authority | Host inventory, observations, create/import/backup/restore; no client executor or authoritative writer | PASS |
| CLIENT-002/003, LOCAL-001–004, OWNER-001/002, SEC-001 | Security boundary | No machine credentials, secret data, bootstrap, enrollment or privileged shortcut; advanced player IDs not credentials | PASS / unaffected mechanisms |
| IDENT-001/002 | Every scoped surface | Exact Host/profile pair, local/remote headers/rail, collision examples, Host-target creation before profile exists | PASS |
| IDENT-003/004, PAIR-003/004 | Trust recovery/transactions | No trust/replacement/rotation behavior designed | Unaffected |
| REMOTE-001/002, PAIR-001/002 | Remote data/destinations | Local-Host route, independent permissions, Active usable trust, filtered inventory, no inferred entitlement | PASS |
| AUTH-001–005 | UI authority | Exact Host CreateServer and server targets; no grants, presets or creator-Owner shortcut | PASS / unaffected mechanisms |
| PROTO-001 | Unavailable actions/streams | Feature-specific negotiated support, no app-version inference or generic fallback | PASS |
| OPS-001–004, RECOVERY-001 | Submission/restore lifecycle | Revalidate state/target/lock; stopped backup/restore; safety backup failure stops restore; no offline queue/client unlock; #38 owns detailed recovery | PASS |
| MIG-001 | Import boundary | New profile, source preserved; not v0.4 migration or store merging | PASS / unaffected migration |
| PLATFORM-002, LINUX-001 | Filesystem/platform | Bounded package handoff/local-folder seam only; no remote browser/UNC or Linux production | PASS |

Negative/contradictory search covered direct filesystem/REST, hidden inventory, trust-as-permission, implicit target, raw backup files, resume/queue, revision/lock bypass, stale samples, #36–#38 scope and final field claims. No unresolved Product Decision or mandatory #35 field evidence. Pass A clean; open PR then fresh Pass B.
## #35 Review Pass B

PR #65 initially at `eb83a2c8bc30ca6d5d1dcf9ce28e82e03f276047`. Fresh complete 11-file review found one additional fixture contradiction: the Metrics board declared REST unavailable while Safe Stop/Restart still appeared usable. Corrected those controls to unavailable with REST-specific reasons; running process status remains independently visible. Rendered the correction and final restore-reference/axis details. No additional issue on repeat review. This is a Pass B-only finding; correction count now 4. No automated-test defect or escaped #35 defect known.

Repeated full invariant matrix PASS: HOST-002/PROTO-001 unavailable-action evidence strengthened, OPS/identity/data-ownership/security/platform boundaries unchanged. No new product semantics, generic endpoint, field substitution or #27 decision. Eight-board and retained seven-board checks PASS; strict docs rerun before commit. Pass A and repeated Pass B clean for final corrected diff; remote Docs and exact committed HEAD/base verification remain the merge gate. No external reviewer used. Final SHA/run/merge evidence will be appended at the integration checkpoint after those checks succeed.
## #35 integration checkpoint

Shadow #35 DONE. PR #65 final reviewed HEAD `f32418161225c74b24e30354de3270a8935a251b` passed local design/strict-doc checks and remote Docs `33989127338`; exact PR metadata/base and committed correction rechecked. Merged at `320e7760ade771d49a3e791657247d5f69f283ac`. Four correction iterations, no known escaped #35 defect. All source remains unchanged from #41/#43, whose 174-test evidence is retained without claiming a new run. Canonical planning untouched. Dependency refresh: #36/#37 design preflights eligible; #38 still waits for both. Host secure storage #27 remains an unanswered Product Decision, and production/security/Linux gates remain closed.
## #36 readiness / preflight

#36 — UI design slice: semantic Server Settings, parent #18, v0.5.0. Dependencies #33/#19/#34 satisfied; accepted #35 navigation also available. SHADOW READY on `astra/36-settings-design` from integration `799320f11d75e7d9b746219bda23aab3dbb53468`. Current canonical body already read in this turn. Scope is settings design/prototypes only: categories/search, semantic control families, modifications/save/discard/navigation/validation, unknown preservation and metadata-availability states.

Sources checked at frozen baseline: Core `PalworldSettingSchema`, `SettingModels`, `PalworldSettingsService`, settings-editor guide, architecture section 12, canonical #47. The schema establishes meanings/categories and some enum choices/units; defaults are installation-derived and optional, and it does not establish general numeric bounds/restart metadata. Therefore no numeric limits/defaults/restart rules will be guessed. Conditional control templates may consume future Host metadata but will not bind invented ranges or path-setting semantics to a real Palworld key. Concrete examples use known boolean, enum, text/password and multiplier meanings, with explicitly synthetic configured values; unknown metadata disables the corresponding reset/range/restart claim. Saving still requires stopped state under accepted parity.

Expected changes: docs/design/astra-36 and ledger; reuse accepted design primitives. No Core parser/catalog, production Avalonia, client references, credential store, permission UI or actual write. Applicable ARCH-001/002, HOST-002, PERSIST-001, CLIENT-001/002/003, IDENT-001/002, REMOTE-001/002, PROTO-001, OPS-001–004, SEC-001, PLATFORM-001/002, MIG-001; all other registry families checked as negative scope. Recheck every input/reset/preset/raw/secret/save path for authority, stale revisions, unknown preservation and secrets. Optional presets are not introduced by this slice.

Allowed choices: category/search/control composition and modified-state language only. Validation: 16:9/21:9/narrow settings boards, semantic-control gallery, validation/unsaved-navigation/raw/secret states, rendered review, deterministic XML/token checks, strict docs, remote Docs and two distinct invariant audits. #38 owns final cross-surface acceptance; no live UI evidence will be claimed from SVG. No Product Decision identified: unknown metadata remains unknown rather than selecting product semantics.
## #36 Review Pass A

Complete source/specification/seven-board review against integration `799320f`. Acceptance mapping: semantic normal UI → mixed typed editor and twelve-family gallery; domain meaning rather than storage type → known baseline examples and explicitly unbound templates; unknown preservation → Advanced/raw specification and panel; unsaved/validation/restart clarity → two-change editor, stopped/running/invalid/navigation panels and unknown-metadata labels; responsive/keyboard/reduce-motion → 1600×900, 2100×900, 800×900 plus expanded narrow row and inherited focus/motion contracts.

Review-only findings corrected in one iteration: the first gallery described semantic families but drew too many as identical textboxes; changed segmented boolean, integer stepper, multiline, compound choices and unit-bearing compositions. Global Alerts entry had been omitted from settings chrome; restored it. Narrow reset/revert existed only in prose with no affordance; added Details controls and an expanded-row board showing unavailable Reset and actual Revert. No automated-test failure before review; no known escaped defect.

All seven SVGs rendered and inspected; shared contrast and reproducibility/XML checks PASS, strict docs PASS. Unknown numeric bounds/defaults/restart rules remain unselected. Path/bounded-number templates bind no invented real setting. Synthetic configured values are never represented as defaults. No actual secret or live UI/accessibility/field behavior is claimed.

| Invariant | Why affected | Evidence/check | Result |
|---|---|---|---|
| ARCH-001/002, CLIENT-001, PLATFORM-001 | Layering | New design docs only; no parser/catalog/production dependency changes | PASS |
| HOST-001/002, PERSIST-001 | Save/reset/raw effects | All saves are Host-authorized scoped writes; reset/revert are draft-only, no client INI/file path | PASS |
| CLIENT-002/003, LOCAL-001–004, OWNER-001/002 | Client security | No Host identity credential/bootstrap/enrollment/Owner shortcut; same authenticated local route | PASS / unaffected mechanisms |
| IDENT-001/002, REMOTE-001/002 | Target continuity | Stable full server/Host target, local-Host remote routing, dual authorization retained | PASS |
| IDENT-003/004, PAIR-001–004, AUTH-001–005 | Trust/grants | No trust, recovery, permission or preset mechanism introduced; unavailable authorization never bypassed | Unaffected / PASS boundary |
| PROTO-001 | Schema compatibility | Unknown metadata remains unknown; unsupported schema cannot trigger direct-file fallback | PASS |
| OPS-001–004, RECOVERY-001 | Saving concurrency | Exact revision rejection; stopped-save parity; no forced overwrite/automatic stop/offline queue; Host owns locks/effects | PASS |
| SEC-001 | Every secret presentation path | Unchanged/replace/clear explicit; redaction before normal/raw/error/search/comparison; no logs or disk drafts | PASS |
| MIG-001 | Unknown/config preservation | No migration/normalization/store merge; unrelated unknown entries/comments preserved | PASS / unaffected migration |
| PLATFORM-002, LINUX-001 | Platform scope | No generic file browser, Host shell, Avalonia or Linux production | PASS |

Stale/contradictory search covered guessed ranges/defaults/restart, effective/configured conflation, hidden raw-value rewrite, secret placeholder serialization, running saves, revision bypass, missing chrome entries and #37/#38 scope. No Product Decision is resolved by this design and #27 remains open. No mandatory unperformed #36 field criterion; Pass A clean, PR then fresh Pass B.
## #36 Review Pass B

PR #66 initially at `d37c19229a7c4900f9926c59965c814a41c2bd6f`. Fresh full ten-file review checked all seven boards against their specification and baseline metadata, each acceptance criterion, all secret/raw/reset/save paths, stopped-save/revision requirements, keyboard composition, prior design navigation and the complete invariant matrix. Found a Pass B-only evidence defect: the bounded-slider pattern omitted its promised numeric-entry alternative. Added the entry field. Also clarified softly wrapped description text cannot imply stored newlines unless the Host definition allows them. No setting range/default/restart semantics added.

Rendered the final gallery and repeated design checks; no further defect on repeat review. Full matrix PASS with keyboard/known-metadata evidence strengthened; authority/security/unknown-preservation boundaries unchanged. Two correction iterations total. No automated-test defect or known escaped #36 defect. Local seven-board plus retained #34/#35 checks and strict docs pass; remote Docs and exact final committed HEAD/base verification remain before merge. No external reviewer or field-evidence substitution. Integration checkpoint will record the final SHA and successful run.
## #36 integration checkpoint / #37 readiness

Shadow #36 DONE. PR #66 final HEAD `4d37fe7d48910a96c7c844566d858871dd81d35b` passed local design checks and remote Docs `33989699852`; exact metadata/base and correction checked before merge at `f4ae27931102d267919930784d5e69c56dfe38bc`. Two correction iterations, no known escaped #36 defect. Canonical planning untouched. #37 is now the remaining prerequisite before #38 final design work; #27 remains Product Decision.
## #37 readiness / preflight

#37 — UI design slice: Manager Settings, trust, sharing and permissions, parent #18, v0.5.0. Current canonical body reread; dependencies #33/#19/#34 accepted. SHADOW READY on `astra/37-security-design`, from `c5a4010c612ad17cec15d39bf577e530b5466a67`. One coherent design-only security/Manager Settings slice; no implementation, generic OS administration or cloud/relay scope.

Read accepted architecture sections 2/2a/2b, 4b, 5/5a/5b and 8, with current full registry already available. Expected files: docs/design/astra-37 prototypes/spec/generator and ledger. No production topology, credential holders, writers or executors change. Applicable all CLIENT/HOST/IDENT/LOCAL/OWNER/REMOTE/PAIR/AUTH/PROTO/SEC/PERSIST families, plus ARCH, OPS, RECOVERY, PLATFORM, LINUX negative boundaries. Authority-creation audit includes structural Owner bootstrap/root grants, ordinary delegated grants, role-preset expansion, Owner-only DefaultGrantTemplate, creator convenience, Owner adoption/re-home and credential replacement. None may become an alternate grant-issuance path through UX.

Design distinctions to preserve: exact Host/server scopes (no AllServers grant); LocalPrincipal versus RemoteManager actor; independent CanDelegate/CanDelegateOnwardDelegation; single-parent provenance forest; ManagePermissions is inspection, not grant issuance or defaults editing; defaults applied only at Active and not retroactively; same-HostId unproven replacement needs Owner approval; local revocation is immediate regardless of peer acknowledgment. Privileged offline recovery is guidance only, never an ordinary/remote Owner replacement button. Host boot configuration remains privileged setup while UI sign-in is per-user; no new privileged helper chosen. #27 ACL choice remains untouched.

Allowed decisions: internal Manager Settings navigation, role-card/graph/confirmation layout. No preset membership or factory-default grant list is invented: role cards preview Host-defined entries, Custom shows explicit synthetic grants, factory template is identified as Host-provided least privilege. Validation: scoped Manager Settings, trust/defaults/custom-grants/provenance/revocation/update-diagnostics boards, static rendering/semantic audit, deterministic XML/shared token checks, strict/remote Docs, full two-pass review. No Product Decision found in this bounded presentation plan.
## #37 Review Pass A

Reviewed every source/spec/board and complete proposed change against integration c5a4010. Nine rendered boards cover all seven acceptance criteria: trust versus grants; typed exact targets; root/derived forest and independent onward flags; structural Owner boundaries; future-only defaults; subtree preview; unsupported action with reason. Canonical scope maps General/Appearance/trust/defaults/grants/provenance/revocation/recovery/update-history to concrete boards. No production files change.

Two review-only correction iterations: restored the permanent server rail when Manager Settings initially replaced it; added a concrete unsupported history-export control because prose alone was weak unavailable-capability evidence. All nine initial renders inspected; final changes checked in generator/SVG. Deterministic XML and shared contrast checks for #34–#37 PASS, strict docs PASS. No automated-test defect or known escaped #37 defect. No live keyboard, security or field test is claimed by static art.

| Invariant IDs | Why affected / evidence | Result |
|---|---|---|
| ARCH-001/002, CLIENT-001, PLATFORM-001 | Docs-only diff; frozen WPF and dependency topology untouched | PASS |
| HOST-001/002, PERSIST-001 | Authoritative Host context; no UI writer; offline recovery retains same lock | PASS |
| CLIENT-002/003, LOCAL-001–004, OWNER-001/002 | No private Host key/client shortcut, first-connection Owner, group enrollment or ordinary Owner reset; privileged preparation/intended-user completion | PASS |
| IDENT-001/002, REMOTE-001/002 | Full Host and server identities, typed actors and local-to-remote dual authorization | PASS |
| IDENT-003/004, PAIR-001–004 | Rotation versus loss; PeerBound no management; Owner-gated known identity; immediate unilateral revocation, no distributed transaction | PASS |
| AUTH-001–005 | Exact type/target/source and both independent flags; preset/default paths obey issuance; Owner-only nonretroactive templates; forest cascade leaves independent roots | PASS |
| PROTO-001 | Negotiated support; unknown/unsupported denied, no version-string authority or raw-file fallback | PASS |
| OPS-001–004, RECOVERY-001 | Recheck stale graph before commit; Host owns effects; no new lock/recovery kind or blind retry | PASS / unchanged mechanisms |
| SEC-001 | No actual secrets; redacted history; #27 storage policy explicitly unresolved | PASS boundary |
| MIG-001, PLATFORM-002, LINUX-001 | No migration, generic OS UI, service shell or Linux production | Unaffected / PASS boundary |

Stale-reference audit examined trust-as-authority, ManagePermissions-as-issuer, default retroactivity, derived rights escalation, HostId changes, global-server scopes, own-Owner remote authority, peer-ack revocation, guessed preset membership, recovery and #27 ACL assumptions. Final design preserves accepted semantics; Pass A clean. PR then independent Pass B required.
## #37 Review Pass B

PR #67 at 30d7f85931d5797ffd00dee3a2bfc2578405fab3 reviewed afresh: full twelve-file diff, all acceptance criteria, all invariant rows above, source and generated boards, negative issuance/recovery paths and sibling design consistency. Found a consequence-preview omission: trust revocation explained dependent grants generically without naming the concrete affected grant in this scenario. Added G103, zero descendants and unaffected G101/G102/G201, with current-graph recheck. This is the third correction iteration total; no automated-test defect or known escaped defect.

Rechecked both revocation columns against the forest: subtree revocation G102 affects G102/G103; peer R1 revocation affects only G103 here. All other acceptance/invariant checks remain PASS; no Owner, preset, default or #27 policy changes. Generator/reproducibility and strict docs pass. Pass B clean after correction review; final-head remote Docs and exact metadata/base check remain before merge. No external review used.
## #37 integration checkpoint

SHADOW DONE. PR #67 final HEAD 09e26ac1d4a4a05231f148043fec90de52a807ed; remote Docs 33990370967 PASS; metadata/base and final correction renders verified. Merge 31658163b3daeeb6b6dbd67b2f07d118600351ed. Three correction iterations, no known escaped #37 defect. No canonical mutation. #38 prerequisites now accepted in shadow.
## #38 readiness / preflight

SHADOW READY on astra/38-final-design from 91d1b092af71177b26610aec1e95b45144ea9a33. Canonical #38/#18 reread; #33/#19 and shadow #34–#37 accepted. Design-only final slice, no Avalonia or service implementation. Read accepted architecture §§9–10 and baseline transfer parity, cross-checked canonical #49/#50; legacy LAN navigation/bearer semantics are superseded by accepted #19/A1-U, not copied.

Expected docs/design/astra-38 specification, deterministic boards and a local interactive design prototype to exercise focus/reflow, plus ledger. Scope: Activity, Send/incoming review, conflict/busy/recovery, stale/offline/degraded/permission/protocol/failure, same-token three themes and final responsive/keyboard consistency. New operation types, resumable transfer, offline queues, destructive continuation and new authority are prohibited. Exact per-kind recovery/lock mappings remain implementation contracts; presentation uses declared Host outcomes rather than guessing them. All registry families reviewed; OPS-001–004/RECOVERY-001/IDENT-002/REMOTE-001–002/PROTO-001 are central; security/Owner/store mechanisms remain untouched. No production dependency/credential/writer/executor changes.

Validation plan: concrete static failure/transfer boards plus interactive local presentation exercise at normal/narrow/enlarged text, all three palettes/shared token checks, full cross-surface acceptance map, local strict docs, two independent reviews and final-head remote Docs. Browser prototype is synthetic, not production accessibility/service/field evidence. #27 remains a required product decision; #42 preflight reveals practical secure-store dependency despite its declared list omitting #27.
## #38 Review Pass A

Reviewed all fourteen changed files (spec, HTML, generator, ten SVGs, ledger), full proposed integration diff, canonical #18/#38 acceptance and full registry. Cross-surface map in README binds every critical accepted surface to boards/interaction rules, including bounded log detail rather than a new Files/transport tab. Ten boards rendered/inspected; retained #34–#37 and new generator/XML/shared contrast checks PASS; strict docs PASS. No C# production change or new C# test-count claim.

Review corrections across four iterations: (1) scenario transitions reset exact target rather than retaining an unrelated Host header, and focus uses the active palette accent; (2) narrowed rail labels without losing accessible names, retained decided-offer state on reopening, corrected Send source rail state/destination availability and separated Activity filter spacing; (3) fixed offline-server Overview reverting to a stopped/live example, added concrete bounded log evidence and Add/Import accessible name; (4) added actual incoming Close/Escape with focus return and no accept/reject side effect. These are review/browser-exercise discoveries, not automated C# test defects. No escaped #38 defect known.

Browser evidence (local synthetic HTML): Activity opens with Close focused; Escape returns to Activity, and separately to Alerts when Alerts opened it. Tab leaves the nonmodal panel. Keep draft via Enter returns to Reload trigger; Review focuses the explicit latest-value selector. Closing incoming review leaves it undecided; rejection survives navigating away/back with Accept disabled. Offline R1 then Overview stays timestamped/non-live with Start disabled. Narrow 640×900 plus 200% text renders both grant flags and full identity with 88-unit rail; document scroll width equals 640. Light conflict and Dark grant layouts inspected at enlarged text. 1600×900 all three themes preserve workspace width 1318 / rail 280 / scroll width 1600; 2100×900 Light transfer preserves rail 280 / scroll width 2100. Theme switches use one DOM and actual shared JSON. Viewport override reset. No real Host, OS preference, production screen reader or field test was simulated as passing.

| Invariant IDs | Why affected / concrete audit evidence | Result |
|---|---|---|
| ARCH-001/002, CLIENT-001, PLATFORM-001 | Docs-only diff; unchanged executable/dependency topology | PASS |
| HOST-001/002, PERSIST-001 | All effects/status/recovery Host-owned; HTML only synthetic memory and local token fetch | PASS |
| CLIENT-002/003, LOCAL-001–004, OWNER-001/002 | No machine key, bootstrap/enrollment/group/Owner shortcut; protected local channel and intended-user guidance retained | PASS |
| IDENT-001/002, REMOTE-001/002 | Exact target/actor boundaries, separate local/destination authorization; no UI remote-key transport | PASS |
| IDENT-003/004, PAIR-001–004, AUTH-001–005 | No new trust/issuance/rotation/default rules; two grant flags/targets preserved; offer acceptance grants no standing authority | PASS / unchanged mechanisms |
| PROTO-001 | Unsupported/unknown/incompatible distinct from denial/offline; no version-string or generic fallback | PASS |
| OPS-001 | Original/draft/current, rejected revision, explicit review/reload, repeated-conflict path, no overwrite/merge | PASS |
| OPS-002/003/004 | Host operation lifetime, target versus lock scope, Host/server hierarchy, safe reads, no UI close release | PASS |
| RECOVERY-001 | Declared disposition; retained manual-review lock; no blind resume/force unlock or resumable transfer feature | PASS |
| SEC-001 | Synthetic data only; secret comparison clearing/redaction; no tokens/log/disk draft, #27 unmodified | PASS boundary |
| MIG-001, PLATFORM-002, LINUX-001 | No migration writes; bounded client-only folder/log actions; no Linux production | PASS / unchanged mechanisms |

Acceptance map: nonblocking lifetime=Activity/HTML; stale non-live=states/failures/offline-navigation exercise; conflict/lock/recovery=three distinct boards and scoped actions; scoped receipt=incoming/HTML; alternate hierarchy=identical Activity geometry/shared DOM; all critical surfaces=README #34–#38 map; implementation handoff=explicit responsive/focus/availability/operation rules, backend policy kept in accepted contracts. Search covered hidden authority, offline queues, live-world copying, partial import, blindly resumed transfer/update, secret raw/comparison paths, palette forks and stale contradictory targets. All gates for design scope satisfied locally; Pass A clean. PR creation then fresh Pass B and exact-head Docs remain.
## #38 Review Pass B

PR #68 at 9b159676529644103cd0c682ae38ac6e0c17dbd3 reviewed afresh, all fourteen files and complete acceptance/invariant matrix, generated/source coherence, interaction handlers and cross-surface negative paths. Found one target-clarity defect: incoming receipt's HTML header still showed an existing local server/profile, implying a server destination instead of receipt on a Host before separate import. Corrected Host-only incoming/recovery headers and central target rendering so returning to Overview restores full ServerRef. Browser verified incoming Host-only -> Close -> local ServerRef, and Host recovery -> Overview remains remote offline/non-live with full ServerRef. Fifth correction iteration total.

Fresh acceptance audit: no copy of running world, automatic partial adoption, implicit permission, stale-live fallback, queue, forced overwrite, scope inference, or blind recovery. Static board and HTML effects remain presentation-only. All invariant rows from Pass A PASS after exact-target correction; no source/private-store/production boundary changed. Local strict docs PASS and retained deterministic checks remain valid. Pass B clean; no automated-test defect or known escaped #38 defect. Final committed HEAD/base/remote Docs must pass before merge.
## #38 integration checkpoint / design gate

SHADOW DONE. PR #68 final HEAD 4ea861e4ae7bfc8fef96c0e63fd02b4e2b26e224; remote Docs 33991037280 PASS on that exact commit; base/metadata and final correction verified. Merge 562c67a95af51847b4ed796ae6956f167fb63587. Five correction iterations, no known escaped #38 defect. #34–#38 are now accepted in shadow, satisfying the #18 design container gate only. Canonical issue/Project states remain untouched. Production Windows parity has not passed.

## #42 preflight / dependency stop

Current canonical #42 body reread. Declared #33/#19/#40/#41 prerequisites are accepted, but the complete normative registry adds LOCAL-003/004, OWNER-002 and IDENT-004 obligations beyond the issue's older invariant list. Local IPC must authenticate the Host using its machine credential and protected public trust descriptor before any principal challenge/bootstrap/enrollment secret or RPC crosses it. Bootstrap/offline recovery must establish/use the secure credential reference under the same exclusive writer lock; ordinary clients must never obtain that machine private key. #43 contracts are accepted, but #27's secure Host store is still Product Decision.

Consequently #42 is SHADOW BLOCKED by the practical #27 dependency. A plaintext/development credential store, self-signed trust-anyway channel, identity-only named-pipe challenge or implementing the unresolved ACL/access policy inside #42 would bypass the accepted security boundary. No production #42 branch/PR or partial implementation is claimed. Required eventual evidence remains real Windows multi-user named-pipe/TLS/authentication/bootstrap/recovery tests plus full suite/invariant audit. No transport algorithm/library was selected prematurely.

## Stopping checkpoint — remaining authorized work

All currently independent bounded design/protocol/platform work is exhausted. #27 retains the unanswered Product Decision packet above. #42 and downstream security/server/UI work are dependency blocked; #55 is not eligible and still needs real Windows field/parity evidence. Linux #21 remains hard-gated by #55; packaging #22 and qualification/docs #23–#25 are not reached, with future oversized work to be decomposed only when ready. #26 observation and #29/#30/#31 remain unscheduled; no invented cause or new capability. No real release/tag/package/main mutation occurred.

Completed trial work: #41, #43, #34–#38 (seven bounded issues), with one separate escaped-fixture correction PR for #34. Eight experimental PRs #61–#68 total. Full suite grows from baseline 161 to final validated 174; no summing reruns. Last production CI 33987200947 contains actual Windows integration. Design package has 41 current SVG boards plus one local synthetic HTML prototype. This checkpoint is documentation only; no code changes since accepted #43. #34 has one known escaped design defect, fixed in PR #64; no known remaining escaped defect. Review correction iterations: #41 3, #43 4, #34 including follow-up 2, #35 4, #36 2, #37 3, #38 5 (23 total under recorded per-item counting).

The revised Astra trial used no external GitHub Codex reviewer; Astra was responsible for implementation and technical review under two-pass self-review. Historical exception remains explicit: one #41 external finding was received before that workflow change. It is not erased or attributed to Astra. Comparison must stratify the pre-change #41 finding from the revised lane.
## #27 Product Decision approved / resumed preflight

The Product Owner explicitly approved the recommended decision: encrypted Host credential blobs permit the dedicated service plus elevated Administrators/SYSTEM. Ordinary users, activation-group members and ordinary clients remain excluded. This resolves the earlier service-only wording versus offline recovery access ambiguity in this isolated branch; canonical Issue/Project are unchanged. The earlier stopping report is a historical checkpoint, not current completion state.

SHADOW READY at d3cf320b247ced1025c89a90d6ccb9d210a91b42 on astra/27-secure-store. Current #27 body and complete invariant registry reread. Changes expected: Platform.Contracts secure-store/redacted-secret contract, Platform.Windows DPAPI/ACL implementation, SelfTest reusable store suite and real virtual-service/nonadmin integration, architecture wording and bounded documentation. No executable reference topology changes; no pairing/Owner issuance/recovery implementation/migration/Linux/WPF changes. Host/privileged offline callers own store effects under their existing same-machine lease; the platform store does not acquire a second competing Host lease or impersonate clients. Current Host initialization is #42 and no private keys are added to SQLite here.

Credential holders: dedicated Host service and authorized exceptional elevated offline caller only; per-user principal keys stay in the independent client store. Validate security of root/store/blob before use; reject permissive, missing mandatory identities, reparse or unprivileged-owned state rather than silently repairing it. Atomic encrypted-file replacement, key-bound DPAPI entropy, idempotent missing delete, bounded blobs, cancellation and redacted exception behavior require negative evidence. Secret-marked values must redact default formatting/JSON, with deliberate byte extraction for cryptographic consumers. No blanket claim that arbitrary caller-logged byte arrays are automatically redacted.

Primary invariants SEC-001, IDENT-001, CLIENT-001/002/003, LOCAL-002, PERSIST-001, HOST-001/002, PLATFORM-001 and ARCH-001/002; all other registry families audited for unchanged authority/transport/operations boundaries. Validate full build/suite plus lifecycle/restart/tamper/path/ACL/cancellation/redaction and real service-to-elevated-caller persistence, nonadmin denial, and retained #41 integration. Microsoft's DPAPI documentation confirms machine context is not a per-user security boundary; ACLs are mandatory. No live secret, remote credential or real release involved.
## #27 pre-PR validation candidate

Local Release build: zero warnings/errors. Full suite: 178/178 PASS, including four new secure-store groups. Strict docs PASS. Review of the initial tests found evidence gaps: moved the reusable contract body into a Windows-API-free file, made real elevated offline store tests explicitly hold the machine lease, and added actual symlink rejection plus update-style executable replacement before service restart. These are one pre-PR correction iteration; no automated defect found so far. Actual privileged Windows CI remains required before Pass A can be declared clean; local medium-token execution is not substituted for it.

## #27 second pre-PR correction

Candidate 85f670cf0ba953fe5ab746e9deeccd40c05ae635 passed actual Windows CI 33995319774 (service and multi-user integration plus full build/self-test) and Docs 33995321511. Review then found orphan encrypted temporary writes were not associated with a key and could survive later retirement. Added per-root in-process serialization and exact-key temporary retirement on retry/delete; file creation now installs its protected ACL atomically, avoiding a crash between inherited creation and explicit protection. The first crash-fixture test failed because copying a file then applying an unmodified ACL object did not protect the new file; corrected the fixture to create with the exact ACL. This is one automated test/fixture failure within correction iteration 2, not evidence of a released defect. Full local suite now 178/178, Release build zero warnings/errors, strict docs PASS. Updated candidate requires fresh real Windows CI before Pass A closure.

## #27 Review Pass A

Reviewed the entire ten-file diff against d3cf320b247ced1025c89a90d6ccb9d210a91b42, every changed file, current canonical #27 and the complete normative registry. Acceptance map: private material stays in encrypted files, never database DTOs (ciphertext/tamper/database checks); ordinary clients retain no Host-side project dependency and two actual non-elevated users are denied file read/write/delete; marked logging/audit/JSON payloads redact; real virtual-service -> elevated offline lease holder -> restarted/replaced service reads persist; threat limitations explicitly cover privileged ownership, snapshots and memory; plaintext legacy and corrupt/wrong-key material fail closed for later migration; portable contract tests contain no Windows/GUI dependency. No new rotation, recovery, enrollment, grants, RPC or migration implementation is smuggled into the store.

| Invariant IDs | Concrete evidence / applicability | Result |
|---|---|---|
| ARCH-001/002, CLIENT-001 | Ten-file boundary; no WPF/Lan/client reference changes; topology tests pass | PASS |
| HOST-001/002, PERSIST-001 | Trusted Host-side composition only; real service lease and stopped/elevated offline lease; no online CLI path or competing lease | PASS |
| CLIENT-002/003, LOCAL-002 | Separate Host and per-user stores, protected blob ACL, real two-user denial; no client principal private keys in Host store | PASS |
| IDENT-001/003/004 | Opaque stable key refs; atomic replacement/idempotent exact-key retirement supports later orchestrators; no HostId, current-ref, staged rotation or recovery state changed | PASS boundary |
| SEC-001 | Machine DPAPI plus approved service/BA/SY ACL and owner validation, atomic protected creation, key-bound entropy, tamper/legacy/reparse denial, redaction tests and internal buffer clearing | PASS |
| LOCAL-001/003/004, OWNER-001/002 | No bootstrap/enrollment/Owner or local-channel authority introduced; approved recovery ACL is not RPC authorization | PASS unchanged |
| IDENT-002, REMOTE-001/002, PAIR-001–004, AUTH-001–005, PROTO-001 | No transport, peer/grant/state/negotiation changes; retained full suite passes | PASS unchanged |
| OPS-001–004, RECOVERY-001 | Store is a bounded primitive, no durable operation bypass; failed replacement retains current blob; orphan encrypted writes discarded only for exact-key retry/delete under serialized writer | PASS boundary |
| MIG-001, PLATFORM-001/002, LINUX-001 | No migration execution, GUI or Linux implementation; OS behavior confined to Windows seam, cross-platform contract independent | PASS |

Stale/reference search covered service-only ACL claims, client access, secure-store consumers, legacy plaintext fallback, unqualified authority and store layout. Architecture §7 is changed only for the explicit Product Owner ACL decision; historical blocked checkpoint remains labelled historical. Two pre-PR correction iterations are recorded above. One failed test fixture was corrected; no released/escaped #27 defect known. No unavailable real field behavior is labelled PASS: reboot, release-package qualification and Linux are outside this store slice, while actual privileged service/multi-user behavior was executed.
`475b092f6826a3b0c9db8c42cba752e554ba7266`: CI 33995667758 PASS (both jobs including real service/multi-user), Docs 33995668602 PASS. All required local and remotely executable validation passed; Pass A clean.

## #27 Review Pass B correction

PR #69 at 1bb28fd08b2d9565056660d5657b6ec0209da659 reviewed afresh against the exact experiment base: all ten files, acceptance map, whole invariant registry and rollback/cleanup/threat claims. Found a negative-evidence flaw unique to Pass B: restoring the original, unmodified ACL object need not write its DACL, so a later missing-SYSTEM rejection could pass because the earlier outsider ACE was still present. Fixed explicit DACL restoration and required a successful baseline read between each independent fault. The first correction attempted to write the audit-security section and failed under the real medium token; limited restoration to the DACL, requiring no extra privilege. Also normalized a trailing root separator so two trusted store instances share their gate, and added concurrent writers/retrieval/retirement coverage (179 test groups total).

One existing synthetic force-stop wiring test also failed once (expected one tracker call, observed zero) during that run; it uses a ten-second real helper process. No causal link to this store delta is established, and no legacy production fix is claimed. This observation is retained rather than hidden. Fresh full local and Windows CI results are required below. This is correction iteration 3 for #27; no escaped production defect known.
Corrected local full suite: 179/179 PASS, including the unchanged force-stop wiring test. Release build zero warnings/errors, strict docs PASS. All Pass A invariant rows re-audited against these changes: PASS; DACL fixture repair restores independent negative evidence, root canonicalization changes no authority. Await exact candidate remote validation.

## #27 Review Pass B final audit

Re-reviewed the complete final code/test diff at 0ff6eafcca90a9a0bb3d00b7e3852c1c28f46d32, including exact DACL restoration, independent success assertions and normalized-root concurrent writers. Acceptance and all invariant families from Pass A remain PASS. No outstanding substantive finding; Pass B clean after its one correction cycle (three total for #27). Corrected suite is 179/179 locally; remote CI 33995908279 has passed both substantive build/self-test and real privileged Windows integration steps, with final job teardown still pending at this recording. Docs 33995909971 PASS at that code SHA. Final job conclusion, exact PR HEAD/base and docs-only final HEAD remain explicit merge gates. No external review used.

## #27 integration checkpoint

SHADOW DONE. PR #69 final HEAD e6d005ba38e7dc641411a57082c1f5ed48ff4e77; final implementation 0ff6eafcca90a9a0bb3d00b7e3852c1c28f46d32 passed CI 33995908279 including actual privileged Windows integration; final Docs 33996000718 PASS. Exact HEAD/base verified before merge 18cb97fb61937b4a624573880c599315cacad9b3. Three correction iterations, Pass B found the independent-ACL-evidence flaw, one unchanged transient force-stop observation retained; no known escaped #27 defect. Full suite 179/179. The approved policy is implemented only in the experiment, with canonical planning unchanged.

## #42 refreshed preflight / bounded trial decomposition

#27 now removes the practical secure-store blocker. Current canonical #42 and architecture §2b/2c/3a/3b reread. Declared #33/#19/#40/#41 and practical #27/#43 dependencies are accepted. #42 is too large for one reviewable change once the complete registry's LOCAL-003/004, OWNER-002 and IDENT-004 obligations are included; it remains IN PROGRESS until all following trial-only units and full issue acceptance pass. No duplicate canonical issues are created.

1. **42a: selected Windows local-transport preflight spike.** Prove .NET 8 Kestrel named-pipe TLS, dedicated-group connect ACL, no TCP listener, certificate rejection before sensitive request delivery, and whether IConnectionNamedPipeFeature exposes exact native SID across actual users. Test/tooling only, no production authority. Acceptance is actual Windows evidence and a documented selection/limitation report. Same two-pass review and experiment PR required.
2. **42b: production authenticated local-channel and machine-publication foundation.** Platform/client seams, restricted public descriptor, existing machine identity credential via #27, protected TLS transport, startup reconciliation and explicit absent-versus-untrusted outcomes. No authority granted merely by channel access; no ordinary RPC until initialized.
3. **42c: principal authentication and enrollment/bootstrap.** Per-user key generation/store binding, connection-bound nonce/signature, transactional exact principal mapping, privileged intended-user initial ticket/handoff, Owner-only additional enrollment/removal, retry/expiry/attempt limits/ABA invalidation and minimal authorization hooks. Private client keys never enter Host state.
4. **42d: bounded offline recovery and full composition.** Owner rotate/re-home, machine credential loss/compromise reconciliation/retirement, exact-one-Owner/audit/exclusivity, executable wiring and complete real multi-user service integration. Peer/grant invalidation must preserve accepted schema semantics without introducing pairing or general delegation engines.

Primary applicable IDs across #42: ARCH-001/002, HOST-001/002, PERSIST-001, CLIENT-001–003, IDENT-001/003/004, LOCAL-001–004, OWNER-001/002, SEC-001, PLATFORM-001; all remaining families audited for unchanged boundaries per unit. No Linux, remote listener/pairing, server operations, feature screens or generic Administrator RPC. Stop for Product Decision only if the stack cannot meet accepted ACL/auth behavior or another decision exceeds allowed scope. Required #19 native-identity spike is first; Microsoft documents .NET 8 Kestrel named pipes and TLS configuration, and the installed .NET 8 reference exposes IConnectionNamedPipeFeature. Availability alone is not evidence of reliable multi-user behavior.

## #42a initial spike findings

Implemented the test-only selected-stack spike and multi-user integration extension. Automated local test found Schannel rejects EphemeralKeySet; captured diagnostic is in the trial-local evidence folder, with no production key used. DefaultKeySet synthetic loading succeeds, but creates a native CNG container, now explicitly tracked/deleted/verified absent after probe disposal. This is not silently selected as production storage. Local full suite 180/180 before the cleanup assertion; focused spike including cleanup passes. One implementation correction cycle covers the failed ephemeral-key assumption and honest native-key lifecycle. Actual two-user CI remains required; no issue #42 completion or principal authorization claimed.

## #42a Review Pass A

Reviewed all five changed files against integration 18cb97fb61937b4a624573880c599315cacad9b3: full spike, mode routing, real-user integration, preflight/decomposition/evidence. Local Release zero warnings/errors, full 180/180 suite and strict docs PASS. CI 33996302179 PASS at dbcbe8ca31d1939f1ed50bd280c8cc043306cbc9: real nonmember denial, two fresh nonadmin logons with distinct native SIDs, TLS wrong-pin requests absent from handler, explicit native-key cleanup plus retained service/store tests. Docs 33996303365 PASS. One correction cycle; failed ephemeral-key assumption found by execution; explicit native-container tracking/cleanup and scoped evidence wording found during review. No escaped defect known.

| Invariant IDs | Concrete applicability / evidence | Result |
|---|---|---|
| ARCH-001/002, CLIENT-001, PLATFORM-001 | Test/docs-only delta; production executable dependency graph and WPF/Lan unchanged | PASS |
| LOCAL-004, CLIENT-002, SEC-001 | Real TLS before request, wrong-pin failure classified as authentication, no secret delivery, synthetic-only certificate; new native persistence finding explicitly reserved for production decision | PASS for spike |
| LOCAL-001/002/003, OWNER-001/002, CLIENT-003 | Native SID observed but grants/enrollment/principal bootstrap not implemented or inferred; no Host private key issued to a client | PASS unchanged |
| HOST-001/002, PERSIST-001, IDENT-001/003/004 | Unique test endpoint/credential only; no product database, HostId, current reference, rotation or recovery change; existing machine lease integration retained | PASS unchanged |
| IDENT-002, REMOTE-001/002, PAIR-001–004, AUTH-001–005, PROTO-001 | No product RPC/protocol/authority or remote listener; HTTP/2 probe is explicitly not logical operation implementation | PASS unchanged |
| OPS-001–004, RECOVERY-001, MIG-001, PLATFORM-002, LINUX-001 | No server operations, migration, shell action or Linux production; bounded native-key/server/test-identity cleanup | PASS unchanged |

Acceptance map: named-pipe TLS=local and real CI; ACL=actual nonmember denied before membership; native identity=two exact OS SIDs at server; untrusted endpoint=no handler calls after wrong pin; no TCP=only ListenNamedPipe configured and observed pipe address; cleanup=key absence assertion plus suite cleanup. No virtual-service-hosted Kestrel or production native-cache protection is claimed. Stale/reference search rejects describing DefaultKeySet as ephemeral/memory-only or group eligibility as authority. Pass A clean for 42a. Production #42b remains at the documented native-cache Product Decision, not silently resolved by this spike.

## #42a Review Pass B

PR #70 at 8f08fd560290a28eaf527a68b0bae9cebd9c8fe0 reviewed afresh: complete five-file diff, all acceptance/invariant rows, native identity lifetime, actual-user helper exit/result checks, TLS failure typing, handler counts, exact-container deletion and documentation limits. No further substantive finding. Pass B clean with one total correction cycle. The successful CI evidence belongs to the unchanged test implementation at dbcbe8ca31d1939f1ed50bd280c8cc043306cbc9; later changes are only docs/ledger. Every Pass A invariant row remains PASS for the test-only unit. No external review used, no escaped defect known. Production #42 is not complete and its native-key-cache decision remains unresolved; the isolated preflight unit itself has met its acceptance criteria. Exact final HEAD/base and final Docs remain merge gates.

## #42a integration checkpoint / second stopping condition

42a SHADOW DONE as a bounded preflight unit only. PR #70 final HEAD a19cfaa741aae22452cd864b0e8d3148878ab005; implementation dbcbe8ca31d1939f1ed50bd280c8cc043306cbc9; CI 33996302179 and final Docs 33996515288 PASS. Exact HEAD/base verified before merge 6934f50e49c1092788957b09e98a1ad358789446. Both review passes clean; one correction cycle. Full suite 180/180, including retained #27; no known escaped 42a defect.

#42b is SHADOW PRODUCT DECISION REQUIRED: the working Windows TLS path needs a separately persisted native private-key container, beyond the explicitly approved encrypted-blob ACL decision. The concrete recommendation and required protection/cleanup/authority constraints are recorded in developer/v0.5-local-ipc-preflight.md. No production cache, runtime upgrade, custom TLS, new machine identity or plaintext fallback has been introduced. #42c/d depend on completing that authenticated production channel and remain SHADOW BLOCKED. #42 as a whole is not complete.

Refreshed milestone issue metadata read-only. Newly complete canonical child issues are #27, #34–#38, #41, #43 (eight), plus 42a partial preflight and the separate #34 escaped-fixture correction. Ten experimental PRs #61–#70 merged total. Review correction cycles: earlier 23 + #27 three + 42a one = 27. Known escaped defect remains the #34 design fixture, corrected in PR #64; no known remaining escaped defect. Baseline 161 -> current 180 self-tests, never summing reruns.

Remaining state: #18 design container SHADOW DONE; inherited #17/#19/#33/#39/#40 unchanged accepted inputs. #42 PRODUCT DECISION (42a done, 42b pending, 42c/d blocked); #28/#44–#54 SHADOW BLOCKED on the remaining local-auth dependency chain. #55 FIELD EVIDENCE REQUIRED and not yet dependency-ready. #20 and #16 containers SHADOW BLOCKED. #21–#25 NOT REACHED behind the Windows parity hard gate and later packaging/qualification sequence. #26/#29/#30/#31 unscheduled, NOT REACHED. No currently independent authorized implementation item bypasses this stop. Canonical Issue/Project, normal-lane code/reviews, main/tags/releases/packages untouched.

## Native TLS cache decision approved / #42b1 preflight

The Product Owner explicitly approved the documented native-key-cache recommendation. It is now authorized for this isolated trial: a Windows Schannel container is a derived cache of the same DPAPI-backed machine credential, restricted to service/elevated Administrators/SYSTEM, never authoritative over SQLite or a new identity, with tracked startup reconciliation and retirement. The previous stopping report remains historical.

Split 42b into reviewable sequential units: **42b1 native cache** (this branch) implements the approved Platform seam and validates actual provider/file ACLs, key equivalence, missing-cache reconstruction, unsafe-cache rejection, restart/retirement and Schannel use under real service/nonadmin identities. **42b2 authenticated channel/public descriptor** follows using the accepted cache; 42c/d remain as already planned. No full #42 completion is claimed by either foundation alone.

Branch astra/42-native-tls-cache starts at af398cafd7759ff5c572da854a73e5c0ad164f9a. Current canonical #42 reread, live Project Backlog read-only; shadow eligibility follows the trial authorization. Primary IDs SEC-001, CLIENT-001/002, IDENT-001/003/004, HOST-001/002, PERSIST-001, PLATFORM-001, LOCAL-004; complete registry audited for untouched authority/Owner/peer/operation boundaries. Expected changes: Host-side contract/Windows implementation, reusable and privileged integration tests, architecture/implementation docs and ledger. No client dependency on Host platform, no ordinary RPC, no credential minting/rotation authorization, no Linux/WPF/runtime upgrade. Exact native API/layout are implementation details within this newly approved policy; production callers must hold the existing Host/offline lease.

## #42b1 initial native-provider validation

Initial local build passes. The first probe exposed that Windows PFX import export-policy flags need explicit volatile plaintext-export permission on the in-memory source before PKCS#8 import; that source remains ephemeral, while the persisted cache's export policy is zero. Machine-key finalization then correctly refused this local medium token (NTE_PERM 0x80090010). Native machine-cache tests therefore run only in the explicit privileged Windows integration entry point; they are not skipped and labelled PASS in the ordinary local suite. Real CI evidence is pending. Native API definitions were checked against installed Windows SDK ncrypt.h and Microsoft documentation; provider ACL is set before finalization, with no overwrite flag.
