# Isolated SPAKE2 assessment harness

Trial reference: #44. This is a disposable candidate assessment tool, excluded from the product solution and packaging. It uses **deterministic, public test scalars**. Never deploy this DLL into a Host or use its test-only dependency features for real pairing.

Run on Windows x64 with .NET 8, Rust 1.98.1 (`x86_64-pc-windows-msvc`) and the MSVC x64 build tools. The committed Cargo lockfile pins the dependency graph; the three Pakery packages come from commit `4fa353417ddddfcaaf29f990404e1f48127167e3`, not the registry release. Do not silently update the lockfile or source revision during qualification.

```powershell
$env:RUSTUP_TOOLCHAIN = '1.98.1'
.\tools\experiments\spake2-qualification\run.ps1
```

The script accepts `-CargoPath` for an isolated toolchain and `-OutputDirectory` for artifacts. It preserves the prior process `CARGO_TARGET_DIR`; no machine environment or installed product changes. It requires dependencies from GitHub/crates.io when not already cached. The test projects are not product project references.

The .NET executable loads an explicit absolute DLL path, calls the C ABI and frees the module after all concurrent calls finish. Ten behavioral fixtures check mutual confirmation, mismatches, malformed inputs, reflections, replay and an identity shared point. Three additional fixtures deliberately **reproduce hazards** in the raw API; success means the observation reproduced, not that the candidate is safe without a wrapper. There are 128 parallel stack-local exchanges and unknown-case checks. The exported function takes and returns integers only and catches Rust panics.

This proves an optimized native build and basic .NET loading/calling/unloading on the tested platform. It does **not** qualify a production session ABI, shared-session concurrency, cancellation/disposal, hostile pointer handling, RNG-failure handling, authenticated Host binding, deployment, other architectures, constant-time behavior or crash recovery. Those require the next bounded wrapper/integration unit.

The upstream RFC-vector run is separate, against a source checkout of the same commit:

```powershell
cargo test --locked --release -p pakery-tests --features p256 --test spake2_p256_vectors
```

Run that command from the pinned upstream checkout; do not infer its execution from this harness. The full assessment and dispositions are in `docs/experiments/astra-44-qualification-brief.md`.
