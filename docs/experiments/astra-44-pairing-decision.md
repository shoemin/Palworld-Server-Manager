# #44 pairing dependency decision

Status: **SHADOW PRODUCT DECISION REQUIRED**, 2026-09-06. No pairing implementation or dependency has been added. The previous approval for the Windows Schannel derived cache does not resolve this separate pairing decision.

The accepted architecture requires symmetric SPAKE2 **RFC 9382**, explicit key confirmation, then authenticated HostId/public-credential binding. #44 permits choosing a reviewed implementation, but explicitly stops if no suitable maintained implementation is available for the supported .NET/runtime targets. Its exception does not authorize an unreviewed cryptographic port or substitution of SPAKE2+/OPAQUE.

## Evidence examined

| Candidate | Primary evidence and assessment |
|---|---|
| Managed .NET package | NuGet search API query `q=spake&prerelease=true&take=100` returned zero results; GitHub repository search `spake2 language:C#` also returned zero. Bouncy Castle C#'s current tree contains J-PAKE, not SPAKE2. These are bounded search findings, not proof that no implementation exists anywhere. |
| RustCrypto spake2 | [Current upstream README](https://github.com/RustCrypto/PAKEs/blob/master/spake2/README.md) and [published crate documentation](https://docs.rs/spake2/latest/spake2/) explicitly state there has been no independent security/correctness audit. The repository is maintained, but a Rust/.NET integration and the required reviewed RFC construction have not been qualified here. Maintenance alone does not establish suitability. |
| BoringSSL SPAKE2 | [Public API](https://github.com/google/boringssl/blob/main/include/openssl/curve25519.h) references draft-02. [Implementation](https://github.com/google/boringssl/blob/main/crypto/curve25519/spake25519.cc) hashes the unreduced password digest into its final transcript; that is not an established drop-in implementation of [RFC 9382's transcript/key schedule](https://www.rfc-editor.org/rfc/rfc9382.html). Its [support policy](https://github.com/google/boringssl/blob/main/README.md) discourages third-party dependency and provides no stable API/ABI guarantee. Native embedding would require explicit compatibility and maintenance qualification; its SPAKE2+ support is a different construction. |
| backkem/spake2-go | [Upstream README](https://github.com/backkem/spake2-go) reports RFC-vector coverage but explicitly lacks formal security review/audit and recommends expert review before security-critical use. No reviewed .NET integration is provided. |
| Other inspected implementations | [python-spake2](https://github.com/warner/python-spake2) explicitly lacks constant-time protection. [gospake2](https://github.com/CzarJoti/gospake2) supplies a Go implementation and warns about its default password hashing configuration; the inspected materials did not establish independent review or a supported .NET integration. Neither was selected. |

Passing a vector, a package existing, or another application using a related draft does not by itself satisfy this repository's reviewed implementation requirement. This assessment identifies an unresolved suitability gap; it does not declare all candidates insecure or claim an exhaustive proof of absence.

## Decision requested

1. **Recommended: preserve RFC 9382 and hold #44 implementation until a suitable dependency and its security/integration qualification are supplied or commissioned.** This retains accepted semantics and avoids treating Astra's implementation review as a cryptographic audit. It blocks #44 and its dependents; independent #52 shell work can continue.
2. Authorize a separate product/architecture investigation into an explicitly specified alternative construction or native variant, including review and maintenance requirements. This may broaden viable libraries but changes the accepted baseline and requires a concrete revised contract before implementation. It is not permission to silently adopt SPAKE2+, OPAQUE or BoringSSL's draft variant.

The Product Owner may also supply a specific reviewed RFC 9382 implementation for bounded evaluation. No external expert has been contacted and no procurement or third-party review has been arranged.

## Preserved state and gates

The last accepted integration checkpoint is `3955b7221e756e7c18edf97d9a1d3b864d20d648`, with #42 complete through PR #83. This research lives on `astra/44-pairing-preflight`; no implementation PR is opened and #44 is not marked done. Source/tests/product graph are unchanged; the last accepted 217 ordinary tests and actual Windows integration remain evidence for #42 only. Research docs receive strict documentation validation. Review of the decision packet checks source/claim alignment, distinction between maintenance and review, and no substitution of a different PAKE.

All existing authority, persistence, Host-private-key, peer-state, protocol, Windows-before-Linux and release gates remain unchanged. #52's accepted shell/design contract does not depend on #44, so its bounded implementation can continue without choosing a pairing construction.
