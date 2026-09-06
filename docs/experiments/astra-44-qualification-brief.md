# RFC 9382 dependency assessment

Trial reference: #44. Updated 2026-09-06. **Direct technical assessment completed; Pakery P-256 is the preferred candidate for a guarded Windows/.NET wrapper qualification. No production dependency is approved by this assessment alone, and full #44 is not complete.**

## Corrected decision

The current #44 body permits selecting a suitable maintained reviewed SPAKE2 implementation. It does not require a paid or independent audit. The Product Owner accepted direct internal assessment after Astra acknowledged that its prior external-review-only gate was an added constraint. This document supersedes the prior provider/reply-contact request. No outside input is required to continue the bounded technical work. No provider was contacted, payment committed or audit claimed.

RFC 9382 and the accepted architecture remain unchanged. The earlier decision packet and PR89 package remain historical records. This assessment uses source inspection, actual Windows tests and explicit remaining integration criteria. Astra's review is not an independent cryptographic audit or proof of security.

## Candidate disposition

| Candidate / pinned source | Observed evidence | Disposition |
|---|---|---|
| [Pakery](https://github.com/djx-y-z/pakery/tree/4fa353417ddddfcaaf29f990404e1f48127167e3), `4fa353417ddddfcaaf29f990404e1f48127167e3`, P-256/SHA-256/HKDF/HMAC | Source construction matches the inspected RFC suite; all 17 upstream P-256 tests passed locally in debug and release. Added negative tests and a .NET/native feasibility harness executed. Three raw API hazards reproduced below. | Preferred candidate for the next bounded wrapper qualification. Guarded integration still required; no unconditional production PASS. |
| [backkem/spake2-go](https://github.com/backkem/spake2-go/tree/ef71e299b11a7886db6e764b075001a6ac4c7aa1), `ef71e299b11a7886db6e764b075001a6ac4c7aa1` | Source targets RFC 9382 and gates `SharedKey` on confirmation. Its `go.mod` uses Kyber `v4.0.0-pre2` and 2019 indirect crypto/sys versions. Password conversion calls Kyber scalar `SetBytes`; the pinned [scalar implementation](https://github.com/dedis/kyber/blob/v4.0.0-pre2/group/mod/int.go) uses `math/big`. Repository metadata showed last push 2025-05-29. | Deprioritized: secret-scalar timing/representation and dependency maintenance need substantive review/remediation before integration. Age alone does not establish a vulnerability or abandonment. No Go build, timing measurement or exploit claim. |
| [RustCrypto/PAKEs](https://github.com/RustCrypto/PAKEs/tree/a35ee6d5f833a3b38e81cf15c0542fb33c456668/spake2), `a35ee6d5f833a3b38e81cf15c0542fb33c456668` | `spake2/src/ed25519.rs::hash_ab` constructs six fixed 32-byte fields, including hashes of password and identities, and hashes the result. `finish` returns a 32-byte key; it does not implement the RFC transcript/key-confirmation schedule. | Unsuitable as a drop-in RFC 9382 dependency at this snapshot. This is a concrete construction mismatch, not an audit-based rejection. No port or protocol replacement authorized here. |

Other earlier candidates were not requalified in this unit. The search is bounded, not an exhaustive claim about every implementation.

## Pakery identity and maintenance evidence

The pinned source commit is dated 2026-07-21. GitHub metadata inspected on 2026-09-06 showed an unarchived repository created 2026-03-07, pushed 2026-09-05, with tags v0.1.0, v0.2.0 and v0.2.1. This supports recent maintenance activity but establishes no support SLA or long-term maintainer capacity. The source declares `MIT OR Apache-2.0`; any eventual distribution must preserve applicable license notices.

The downloaded `pakery-spake2` 0.2.1 crate contains `.cargo_vcs_info.json` mapping it to `4d038eed2bdb355f8994256f1846f0ece65cffc4`, matching its tag. Package SHA-256: `dd7f0429761fc9f4bd3512a567e71d6ec2af68903aa65ba66b68de6a7d58e7cd`. The reviewed source is two commits ahead; GitHub comparison lists changes in mutation/dependency tooling, `Cargo.lock`, and core constant-time instrumentation. The SPAKE2 transcript source itself compares equal. The assessment harness deliberately pins the reviewed Git commit for all three Pakery crates; it does not assert complete release equivalence.

The committed assessment `Cargo.lock` fixes the graph, including `p256` 0.13.2, `elliptic-curve` 0.13.8, `primeorder` 0.13.6, `hkdf` 0.12.4, `hmac` 0.12.1 and `sha2` 0.10.9. Every future dependency, compiler, feature or suite change needs an impact review and affected qualification rerun. No commercial maintenance or third-party PAKE audit was established.

## Construction assessment

Against [RFC 9382](https://www.rfc-editor.org/rfc/rfc9382.html), the inspected P-256 path has:

| Requirement | Source mapping / result |
|---|---|
| Fixed suite and points | `pakery-crypto/src/suites.rs` selects P-256/SHA-256/HKDF/HMAC; `spake2_constants_p256.rs` matches the specified M/N. P-256 cofactor is one. |
| Element/scalar encoding | `p256_group.rs` emits 65-byte uncompressed SEC1 and 32-byte big-endian scalars. It also accepts compressed inputs; the eventual wire wrapper must enforce one 65-byte format. |
| Transcript | `encoding.rs` appends six fields in the specified order, each with an eight-byte little-endian length. |
| Derivation/confirmation | `transcript.rs` splits the digest into 16-byte Ke/Ka, derives directional confirmation keys and MACs the transcript. Confirmation comparison uses `subtle::ConstantTimeEq`. |
| Point validation | P-256 decoder checks curve membership; both party finish paths reject an identity shared point. |
| Role assignment | The assessment uses initiator A and responder B. Roles can follow the connection initiator/listener; this does not substitute an augmented PAKE. |

All 42 hex constants in the four upstream test vectors were separately compared with the downloaded RFC text after whitespace normalization. The test identities and four complete-vector calls were inspected. This checks the test oracle, not merely the library's self-agreement.

The eventual application profile still must define password-to-scalar derivation, session context and input limits. HostId and long-term public credential binding must follow the architecture's explicit authenticated message **after** successful key confirmation; an RFC vector pass does not provide that binding.

## Executed evidence and concrete hazards

Environment: Windows x64, .NET SDK 8.0.417, Rust 1.98.1 (`48a229ceaefd4985c50990b14116b6d856af0985`, LLVM 22.1.8), `x86_64-pc-windows-msvc`, installed MSVC tools. Rust was provisioned under ignored assessment storage with the official installer and its published SHA-256 checked, without modifying the machine PATH or product installation.

- **17/17 upstream P-256 tests passed in both debug and release**, including all four RFC vectors and upstream wrong-password/invalid-point cases. These are 17 distinct tests, not 34 separate cases.
- **10 added behavioral fixtures reproduced**: correct mutual confirmation, wrong password, changed identity/context, malformed points in both roles, malformed/forged confirmations, reflected confirmation, replay into a fresh exchange, identity shared point, and reflected first message.
- **Actual .NET/native calls passed**, including 128 parallel independent fixture calls, invalid case rejection and module unloading after completion. The DLL was built with release optimization. No real Host key, live server or persistence was involved.
- **Three hazards were reproduced deliberately**, rather than relabelled as security PASS:

| Finding | Reproduction | Required wrapper behavior |
|---|---|---|
| Q44-1: unconfirmed key exposure | `Spake2Output.session_key` and `into_session_key()` expose 16 bytes before peer confirmation. | Keep native output private. Expose binding-key operations only from a confirmed state. No raw pre-confirmation key crosses into ordinary client code. |
| Q44-2: verification after clearing | Calling public `Zeroize::zeroize()` on a live output empties its expected MAC; `verify_peer_confirmation([])` then returns success. | Never verify a cleared object. Disposal must remove/drop it; require a live pending-confirmation state and exactly 32 received MAC bytes before verification. Test stale/disposed handles. |
| Q44-3: failed verification is reusable | The same output accepts a valid MAC after a failed verification because verification borrows it immutably. | The wrapper must make a failed exchange terminal and dispose its secrets. Account for failure once at the Host's code-attempt boundary. |

These are raw API integration hazards with reproducible conditions. They are not demonstrated remote vulnerabilities in this product, which does not yet use the candidate. They can be addressed by a narrow state/length/lifetime boundary without changing the PAKE construction. Do not patch or port the cryptographic arithmetic as a convenience.

The harness is versioned under `tools/experiments/spake2-qualification` with pinned dependencies and commands in its README. It contains public deterministic fixtures and is excluded from the product solution and packaging. Its integer-only C ABI establishes basic build/loading feasibility, **not** a production session ABI or deployment qualification. The shadow-only CI job runs these same fixtures; the ledger records exact CI heads/results.

## Secret/timing limitations

The inspected state and transcript buffers use zeroizing wrappers; finish consumes party state. Hash/KDF intermediates and shared-secret ownership were inspected. This is not a guarantee that every compiler-generated stack/register copy is erased: the P-256 wide-scalar conversion has ordinary temporary values, and primitive hash internals have their own lifetime behavior. No secrets may be logged by the eventual wrapper or diagnostic/error paths.

P-256 arithmetic delegates to RustCrypto primitives, and confirmation uses a constant-time comparison. Production randomness uses the caller's cryptographic RNG and rejection sampling. The random-scalar comment says nonzero, but the implementation accepts zero; finish rejects an identity shared point, so no successful zero-ephemeral session was established by that source path. A random-source failure policy and zero-scalar behavior still require wrapper tests.

No Windows machine-code timing audit, formal proof, native fuzzing campaign, independent SPAKE2 interoperability run or RNG-failure experiment was performed. Upstream ctgrind/dudect/Miri claims are not our results; OPAQUE differential tests are not SPAKE2 evidence. The assessment supports proceeding to controlled integration tests and does not certify constant-time behavior on every supported runtime or architecture.

## Next bounded unit and completion gates

Proceed with **#44a: guarded P-256 SPAKE2 wrapper qualification** under the already authorized library/integration decision. This unit should keep the native component Host-side and outside clients, with no remote listener or durable trust yet. Its acceptance criteria are:

1. Immutable suite/revision/build selection, test-only randomness excluded from production features, explicit code-to-scalar profile and bounded context/message inputs.
2. Safe native session ownership, panic/error boundary, bounded buffer copies and precise allocation/free responsibilities. Failed, disposed, cancelled and stale/reused sessions cannot return a binding key or accept a confirmation. Cover all three findings above through the actual managed session API.
3. Production CSPRNG and failure handling, concurrent use/disposal tests, and confirmed-secret operations that implement the architecture's explicit authenticated HostId/public-credential binding. Substituting either identity or public credential must fail before any peer pin can be accepted.
4. Pinned Windows artifacts and deterministic loading, license notices and supported-architecture evidence. Build, negative/integration tests, the full invariant audit and both Astra review passes must pass before accepting the wrapper.

After that, decompose #44 into Host-owned TLS/pinning, staged credential rotation, durable PeerBound/activation/crash recovery, and actual multi-Host qualification. No Active/default-grant/management authority is created by qualification; #45 and #28 retain their boundaries. #44's maintained-implementation stop condition applies if concrete unresolved construction/integration findings prevent qualification. An optional outside reviewer is one way to investigate a difficult finding, not an automatic prerequisite.

## Invariant and scope audit

All forty registry entries were considered. ARCH-001/002 and CLIENT-001-003: no WPF/Lan or client product dependencies; the test-only executable is not a product client. HOST-001/002, PERSIST-001, LOCAL-001-004 and OWNER-001/002: no Host instance, principal, state writer or bootstrap/recovery changes. IDENT-001-004, REMOTE-001/002, PAIR-001-004, AUTH-001-005 and PROTO-001: qualification grants no authority, binds no production identity and changes no protocol state. OPS-001-004, RECOVERY-001 and MIG-001: no operations or migration. SEC-001: public fixtures only; no product secret output. PLATFORM-001/002 and LINUX-001: Windows assessment only, no new production platform behavior or Linux implementation.

Normal-lane code/reviews, canonical Issues/Project and release state were untouched. Full #44 remains incomplete while actionable internal work is available; it is no longer waiting for reviewer/provider/contact input.
