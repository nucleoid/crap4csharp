# Artifact provenance v1

Crap4CSharp separates immutable evaluation from filesystem and process execution. `EvaluationEngine.Evaluate`
accepts captured source and coverage bytes, explicit parse options, policy, and a provenance observation. It does
not read files, inspect Git, load projects, run builds or tests, consult the clock, or write output.

`analyze --reuse-artifacts <manifest>` replays a completed bundle without accessing its original project roots.
All bundle locators are relative, bounded, containment-checked, and hash/length verified before evaluation. Replay
reports `captured`: it proves internal consistency of the retained evidence, not that the current checkout matches.
An invalid or incompatible explicit manifest is an operational failure and never falls back to bare XML.

The v1 integrity payload uses domain-separated tuples whose UTF-8 fields carry an explicit null/non-null tag and
byte length, plus ordinal set ordering. This makes null distinct from empty text and prevents delimiter collisions.
It excludes only `manifestHash` itself; source-set, input-closure and context hashes are recomputed before the
manifest hash is accepted. The published test vector is:

```text
sourceSetHash   864f42d22774118aa30c2be59188cf321335366c5322a78565ba3e39b7d71a70
inputClosureHash e281b69e9bae9ddabafdfad7e60ded5039e610cbf82aa373bf6c4c64bc730e21
contextHash     27e8ee19bc0a8943eb757235bf53b9c1aee69b42eb5b5310c3cef54c946e20b9
manifestHash    149010ba02d88ba95e606ec5375e6fd55d02f660eb1cd77b4429a144ad6ce730
```

Each context records effective language version, source kind, preprocessor symbols, feature flags, logical path case,
and explicit report-root mappings. Replay uses only those saved logical facts; it does not inspect the host path policy,
current directory, original checkout, Git, MSBuild, or processes. PDB documents and coverage report paths must resolve
through those mappings (or already be exact logical paths); replay does not guess identities from filename suffixes.

## States

- `unverified` / `none`: syntax-only or legacy inspection without provenance evidence.
- `captured` / `captureConsistency`: the supplied completed bundle is internally consistent.
- `verified` / `currentWorkspaceMatch`: a caller supplied a newly captured matching workspace/context snapshot.
- `invalid` / `none`: malformed, incomplete, stale, incompatible, or changed evidence.

Fresh actual compiler/module/test binding and future reuse capability are separate. A fresh run may be verified but
`reusable:false` when its actual tested inputs are known but its future validation recipe is incomplete. Reuse must
then fail or perform an authoritative context recapture; a saved file list is not proof that no new compile/import
input appeared.

## Capture phases

1. Capture authored inputs and selected evaluated contexts before execution.
2. Record the declared nonincremental build, actual compiler/generated inputs, tested module and test evidence.
3. Capture final generated/compiler/module identities and reject authored-input drift. Expected generated outputs
   may appear or replace stale pre-build outputs; they are validated as transitions, not required to preexist.

Current-evidence recapture is stricter than the planned fresh-build transition: it rejects changes to any
preexisting observed compiler/configuration input during Workspace loading, including generated editorconfig
files under a custom intermediate directory. The custom-layout integration fixture uses this repository's
`global.json` SDK selection (10.0.1xx, latest patch). An unpinned consumer selecting SDK 10.0.401 was observed to
rewrite its existing `App.GeneratedMSBuildEditorConfig.editorconfig` during loading; that layout currently fails
`context.inputsMutated`, not verified green. SDK feature bands and custom layouts outside the proven fixture
are not a blanket support claim; do not waive the mutation guard to accept them.

Coverage reports may union only within one context/build and coordinate representation. Different TFMs,
configuration/platform/RID, source/context identities, DLL/PDB identities, or line-vs-sequence-point formats do not
optimistically union. A manifest never upgrades unsupported generated-method mapping into known coverage.
Every accepted coverage artifact references a successful completed execution and its same-context build. The bundle
also retains hash-bound assembly, portable PDB, and test-result artifacts for that graph; booleans in the manifest do
not substitute for those bytes. Verification reads managed module metadata and MVID, matches the PE CodeView record
to the portable PDB identity, requires the complete SHA-256 PDB document inventory and compiler language/symbol
options to match the captured context, and checks reported coverage points against the matching PDB method sequence
points. Sequence-point formats retain exact coordinates when the report supplies them. Line-only formats such as
Cobertura, and Coverlet OpenCover's synthetic `1..2` columns, establish only that every reported line is contained
in a sequence-point span owned by the matching PDB method and that the complete non-boundary statement-line set is
present for directly represented methods. When a method consists entirely of one-column points, all of those point
lines are required instead. Constructor PDB methods may also contain lowered member initializers;
their lines may be omitted from the constructor only when the same report assigns those lines to another method.
State-machine `MoveNext` methods contain compiler control points that Coverlet does not project; v1 validates those
generated line observations by containment but does not claim a complete authored-line denominator from the lowered
method shape. Line formats cannot distinguish same-line coverage from an older
build. The separately hash-bound source, assembly and PDB still bind the evaluated code, but line evidence must not
be described as byte-level coverage provenance. Execution outcome and counters come from the captured TRX; at least
one test must pass and the TRX test storage module must match the module identity derived from the execution's
hash-bound test assembly and portable PDB artifacts. v1 deliberately fails closed for PDB documents that do not
carry SHA-256 checksums.

The captured scope artifact is JSON `{"version":1,"sources":[...]}` using repository-relative source paths; the
manifest separately retains each project's compiler logical path and a hash-bound `repositoryPath`. Trusted checks
require that repository path for every authored source so nested projects and linked files intersect Git scope
without filename or project-directory guesses. Older manifests without it remain capture-replay inputs only. The captured policy artifact is either the
legacy threshold shape or a versioned repository policy. Replay applies those values and rejects live overrides.
Captured baseline/exemption artifacts remain hash-bound inputs; trusted `check` obtains approved baseline and
exemption bytes independently from one immutable Git tree. A Git revision may also bind
`stateHash`, derived from porcelain status (including untracked files), recursive submodule status, and the staged
binary diff; current-workspace verification must reproduce it.

## Assurance limits

SHA-256 identities bind the reported bytes, logical identities, context and artifacts. They are consistency evidence,
not signatures: they do not attest authorship, resist an actor who can replace all evidence, prove no transient
edit-and-revert occurred between observations, or sandbox arbitrary MSBuild/custom task behavior. Unsupported dynamic
inputs fail closed. No secrets, raw environment dumps, or reconstructed shell command strings belong in a bundle.

Bundles are immutable directories. Explicit output destinations must be new; publication occurs through a unique
sibling staging directory followed by an atomic directory move. Incomplete staging is never a completed reusable
manifest. File contents are flushed before publication. Atomic visibility and power-loss durability beyond that flush
depend on the destination filesystem; v1 does not claim a portable directory-fsync guarantee. Bundle cleanup is
explicit and user-owned.
