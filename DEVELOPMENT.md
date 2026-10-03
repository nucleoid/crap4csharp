# Development log

## 2026-10-03 — Versioned callable rules

- Preserved `ordinary-methods-v1` byte-for-byte result compatibility and added opt-in `callables-v1` inventory, exact ownership, family risk, semantic matching, and fail-closed generated mappings.
- Added pure `analyze --syntax-only`, exact local exemption inspection, and additive callable JSON fields. Full `check` orchestration remains owned by issue #10.
- Added bounded PE/portable-PDB state-machine validation with real async/iterator positive fixtures and identity/checksum/malformed negative fixtures.
- Expanded the real Coverlet 6.0.4 sample across Debug/Release, OpenCover/Cobertura, accessors, generics, state machines, local functions, and lambdas. Unsupported generated shapes remain visible rather than guessed.

## 2026-09-16T21:00:00Z — Initial implementation

- Created a .NET 10 solution with a packable tool, focused analysis library, xUnit tests, and a Coverlet-enabled fixture.
- Implemented Roslyn syntax-tree discovery and ordinary-method cyclomatic complexity.
- Implemented safe OpenCover and Cobertura XML readers with sequence-point matching and explicit ambiguity handling.
- Added default and explicit discovery, Git porcelain `-z` changed-file parsing, isolated test coverage execution, deterministic reporting, and the 0/1/2 gate contract.
- Added clean-room attribution and documented method, exclusion, matching, and operational policies.

## 2026-09-16T21:34:08Z — Final acceptance verification

- SDK inventory: .NET SDKs `8.0.418`, `10.0.100-rc.2`, and `10.0.103` installed; the repository pins stable `10.0.103`.
- Bounded restore/build: `dotnet restore ... --disable-parallel` and Release build with `-m:1` succeeded with 0 warnings and 0 errors.
- Unit/integration suite: 16 passed, 0 failed, 0 skipped. Coverage includes exact-threshold math, overload ambiguity, nested generics, sequence-point union, assembly ambiguity, zero-point N/A, malformed source/report handling, changed paths, excludes, no source writes, and missing-coverage policy.
- Packaging: created `Crap4CSharp.Tool.0.1.0.nupkg`, installed into a temporary `--tool-path`, and ran installed `--help`; help created no files other than captured test output.
- Real Coverlet E2E: the installed tool ran the fixture's tests into a fresh unique temporary results directory, matched both methods, and exited `0` at threshold `100`.
- Deterministic CLI gates: CC 8 at 100% coverage exited `0` at threshold `8`; CC 9 at 100% exited `2`.
- Operational CLI checks: all-N/A exited `1`, explicit `--allow-missing-coverage` exited `0`, a non-C# explicit input exited `1`, and `--changed` outside Git exited `1`.
- No global tool installation, public NuGet publication, or push was performed.

## 2026-09-16 — Acceptance hardening

- Made any `N/A` method an operational failure by default and added the explicit `--allow-missing-coverage` opt-out; known scores still gate under the opt-out.
- Changed multi-report aggregation to a visited-state union of distinct sequence-point coordinates, excluding hidden points and leaving zero-point methods unknown.
- Tightened matching to exact normalized source path, namespace/nested/generic type, method name, compatible arity, unique source span, and non-conflicting module identity. Generated state-machine `MoveNext` methods remain unknown.
- Made Roslyn parse errors and existing explicit non-source inputs operational failures. Wrapped malformed XML as a diagnosed coverage-data failure and preserved Git command diagnostics.
- Expanded tests to 18 passing cases covering formulas and strict gates, N/A behavior, duplicate/overlapping reports, hidden/empty points, ambiguity, overloads/generics/state machines, malformed inputs, Git porcelain, explicit-coverage isolation, and source immutability.
- Final bounded restore/build/test succeeded with 0 warnings and 0 errors and 18/18 tests passing. Packaging and local tool installation succeeded. Installed-tool help exited `0` without writing its empty working directory (the repository-local SDK requires command-local `DOTNET_ROOT=/home/fuego/.dotnet` for apphost discovery in this environment).
- Real Coverlet fixture E2E passed 1/1 fixture test, matched both source methods, exited `0` at threshold `100`, and left the analyzed source SHA-256 unchanged. Reusing the report twice exited `0` at exact threshold `4`; threshold `3.99` exited `2`; explicit-report runs created or deleted no result directories.
