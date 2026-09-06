# RFC 9382 dependency qualification brief

Trial reference: #44. Status: assessment authorized; preliminary evidence review complete; independent qualification and .NET integration evidence outstanding. Prepared 2026-09-06 for the isolated Astra lane of `shoemin/Palworld-Server-Manager`.

## Decision and scope

The Product Owner approved a focused qualification assessment while preserving symmetric SPAKE2 as specified by [RFC 9382](https://www.rfc-editor.org/rfc/rfc9382.html). The task is to establish a suitable reviewed implementation and supported integration, not to implement a new PAKE or substitute SPAKE2+, OPAQUE or a pre-RFC variant. The earlier [decision packet](https://github.com/shoemin/Palworld-Server-Manager/blob/87623b9cf7ddc4dd044c506e4ac685d7e02804e8/docs/experiments/astra-44-pairing-decision.md) remains historical evidence; its pending-direction status is superseded by the user's approval.

This brief is ready to send to a qualified reviewer for an initial scope/quote. No provider has been engaged, no quote or budget has been agreed, and no message has been sent. No production dependency, wrapper or pairing code was added. Astra's candidate assessment is not an independent cryptographic audit.

## Refreshed candidate evidence

The following primary sources were inspected on 2026-09-06. Pinned repository heads identify the inspected source snapshots; a registry release and a repository head are not assumed identical. The mutable published-documentation links resolved to the stated versions on the inspection date; use the pinned source links and recheck release mapping for qualification. No candidate currently passes the qualification gate.

| Candidate / exact inspected snapshot | Evidence | Qualification gap |
|---|---|---|
| `djx-y-z/pakery`, `4fa353417ddddfcaaf29f990404e1f48127167e3`; published `pakery-spake2` 0.2.1 | [Pinned README](https://github.com/djx-y-z/pakery/blob/4fa353417ddddfcaaf29f990404e1f48127167e3/README.md) names RFC 9382, generic suites and secret-handling precautions, but explicitly disclaims independent audit. [Published crate documentation](https://docs.rs/crate/pakery-spake2/latest) describes mutual confirmation. | No independent review established; exact release-to-source mapping, selected suite and Windows/.NET integration need qualification. Newly found in this refresh, not previously assessed or selected. |
| `backkem/spake2-go`, `ef71e299b11a7886db6e764b075001a6ac4c7aa1` | [Pinned README](https://github.com/backkem/spake2-go/blob/ef71e299b11a7886db6e764b075001a6ac4c7aa1/README.md) claims RFC-vector coverage and explicitly states it has not undergone formal security review. | Review, maintenance assessment and .NET/runtime integration are unestablished. Vector claims are not audit results. |
| `RustCrypto/PAKEs`, `a35ee6d5f833a3b38e81cf15c0542fb33c456668`; published `spake2` 0.4.0 | [Pinned README](https://github.com/RustCrypto/PAKEs/blob/a35ee6d5f833a3b38e81cf15c0542fb33c456668/spake2/README.md) and [published documentation](https://docs.rs/spake2/latest/spake2/index.html) explicitly disclaim independent audit. | Exact RFC transcript/suite compatibility, confirmation and .NET integration must be established; do not infer RFC equivalence from the crate name or audit of an underlying primitive. |
| BoringSSL and previously assessed alternatives | The earlier decision packet records draft/API/support incompatibilities and missing review evidence. | No new evidence in this refresh resolves those gaps. Not proposed as drop-in dependencies. |

Pakery's [security-testing document](https://github.com/djx-y-z/pakery/blob/4fa353417ddddfcaaf29f990404e1f48127167e3/SECURITY_TESTING.md) describes property/negative/fuzz/timing tests. These are upstream claims, not tests executed by Astra in this assessment. Its cross-implementation comparison described there is for OPAQUE, not SPAKE2. Do not present it as independent SPAKE2 interoperability or review evidence.

Recommendation: ask a specialist to triage Pakery and backkem first because they explicitly target the required RFC; permit the reviewer to identify a better maintained reviewed candidate. This prioritizes assessment only and does not select a production library, curve, wrapper architecture or runtime. RustCrypto remains an alternative only if the exact construction and review gaps are resolved. No claim is made that the search exhausts all implementations.

## Required assessment deliverables

| Deliverable | Required evidence / acceptance condition |
|---|---|
| Exact component identity | Repository commit, released package hash, release-to-source mapping, complete transitive dependency lockfile and build toolchain. Review conclusions name the exact versions and build options. |
| RFC construction | Clause-level mapping of group/suite, constants, scalar and element encoding, transcript lengths/order, identities/context, key derivation and explicit confirmation. Identify all deviations; a different PAKE/draft cannot pass silently. |
| Independent review | Named reviewer and qualifications, date, scope, methods, findings and fix verification. Confirm the review covers this PAKE implementation and integration rather than only its primitive dependencies. Supply an existing report if sufficient; otherwise scope the missing review. |
| Adversarial inputs | Invalid/noncanonical/identity points, truncated/oversized messages, wrong password/context/identity, reflected/swapped messages, forged/truncated confirmation, replay and repeated/out-of-order state calls. All refusal paths must fail closed without accepting a binding. |
| Secret and timing handling | Secret-dependent control flow/memory behavior in the actual selected build, CSPRNG and RNG failure, scalar generation, secret lifetime/zeroization limits and redaction. State test limitations; passing vectors or statistical tests alone is not a constant-time proof. |
| Confirmation/Host binding | Authentication is unavailable until required peer confirmation succeeds. Demonstrate that the accepted HostId and public credential are cryptographically bound to the authenticated exchange/channel so substitution cannot bind an attacker's credential. Review the consumer boundary even if the library exposes a raw key early. |
| Windows/.NET integration | Bounded .NET 8 Windows integration with pinned artifacts, deterministic deployment/loading, buffer ownership/length checks, error mapping, resource cleanup and concurrency. For native FFI, review ABI, panic/unwind boundaries, allocation/free and cancellation/disposal. Do not introduce a general helper service or client-held Host identity. |
| Maintenance and distribution | License/redistribution obligations, maintainer/release/security response evidence, supported Windows architectures, reproducible build instructions and update/re-review policy. State any platform or support gaps. |
| Repeatable qualification harness | Exact commands/results for RFC vectors and adversarial cases through the actual .NET boundary, not only direct library self-play. Separate test-only deterministic randomness from production configuration. Include correct and rejected confirmation/binding paths. |
| Disposition | Suitable at the named versions/configuration, changes required with specific retest criteria, or unsuitable with reasons. No unresolved security/integration finding is relabelled PASS. |

The reviewer should first provide a suitability/gap assessment and scoped estimate, then perform any commissioned audit. Do not spend a full integration effort on an incompatible construction. No fixed cost or timeline is assumed.

## Repository boundaries the assessment must preserve

Authoritative requirements remain the current #44 body, accepted architecture and full invariant registry. Host-to-Host traffic and the Host private credential remain Host-owned. Every ordinary client talks through its local Host. Pairing authenticates identity but grants no ordinary authority. `Created`, `PAKEAuthenticated` and `PeerBound` are not management-authorized states. The later production state machine must persist each Host's state independently, support idempotent reciprocal activation and pinned-mTLS recovery from durable `PeerBound`, and discard pre-binding ephemeral state after a crash.

The assessment may specify test seams but does not implement #45 authorization, #28 revocation, server operations or Linux production. Review portability constraints now; defer Linux implementation and actual Linux qualification until #55 releases LINUX-001. A qualified primitive still does not complete #44: Host binding, TLS/pinning, rotation, durable activation/crash/retry and actual multi-Host qualification require subsequent bounded implementation units and both Astra reviews.

Applicable registry families retain their existing boundaries: ARCH-001/002, HOST-001/002, PERSIST-001, CLIENT-001-003, IDENT-001-004, LOCAL-001-004, OWNER-001/002, REMOTE-001/002, PAIR-001-004, AUTH-001-005, PROTO-001, OPS-001-004, RECOVERY-001, SEC-001, MIG-001, PLATFORM-001/002 and LINUX-001. No authority, secret holder, persistence writer or runtime dependency changes in this document.

## Ready-to-send scope request

Subject: RFC 9382 SPAKE2 dependency and .NET integration qualification

We maintain an isolated development trial for Palworld Server Manager, a .NET 8 Windows machine-wide Host with ordinary local clients. The accepted design requires RFC 9382 symmetric SPAKE2 for short-lived pairing, explicit confirmation and authenticated HostId/public-credential binding. We need an independent suitability assessment of an existing implementation and its .NET integration, preserving that protocol.

Please assess the pinned candidates and qualification criteria in this brief, or propose an existing reviewed RFC 9382 implementation with evidence. Start with construction compatibility, existing review coverage and integration/maintenance gaps. Please quote the initial assessment separately from any implementation audit, wrapper review or remediation retest, and identify reviewer expertise, deliverables, availability, required inputs and exclusions. No production private keys or user data are required for initial scoping. Do not begin paid work until commercial terms are agreed.

## Contact routes and next input

Two verified potential providers, not engaged or endorsed as having audited these candidates: [NCC Group Cryptography Services](https://cryptoservices.github.io/) lists cryptographic assessments and protocol/design reviews, with a [contact route](https://www.nccgroup.com/contact-us/); [Cure53](https://cure53.de/) lists cryptographic audits and third-party component assessment. Availability, pricing and suitability for this exact assignment are unknown until they respond.

Recommendation for commissioning: send the scoped request to a selected provider for an estimate before committing a budget. Required external input is either an existing qualified component/report or a designated reviewer/provider and reply contact, followed by agreed scope/budget if paid work is needed. That input is not another approval of RFC 9382 or the assessment direction; those decisions are already authorized. This document is the concrete reviewable package for obtaining it.
