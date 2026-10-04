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
sourceSetHash   2fcf2db1d70b98d6d21703f0e4f2dc1653a69c815127ea1c4ffc08faac6b57ea
inputClosureHash 7d898082e4d73b0a846d0e36bb0c0e9f253383b6da17cb8927b0d08445dfd254
contextHash     bdf3277f889c3bd5c1d9b949d281e9a8ad551b9052f9fa2a67499747a681cc66
manifestHash    ccf42f44a8c89cefcb302316363bd2ca5d303e13eeadea6b48ce97f6bc0f9195
```

Each context records effective language version, source kind, preprocessor symbols, feature flags, logical path case,
and explicit report-root mappings. Replay uses only those saved logical facts; it does not inspect the host path policy,
current directory, original checkout, Git, MSBuild, or processes.

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

Coverage reports may union only within one context/build and coordinate representation. Different TFMs,
configuration/platform/RID, source/context identities, DLL/PDB identities, or line-vs-sequence-point formats do not
optimistically union. A manifest never upgrades unsupported generated-method mapping into known coverage.
Every accepted coverage artifact references a successful completed execution and its same-context build. The bundle
also retains hash-bound assembly, portable PDB, and test-result artifacts for that graph; booleans in the manifest do
not substitute for those bytes. Verification reads managed module metadata and MVID, matches the PE CodeView record
to the portable PDB identity, and derives execution counters from the captured TRX.

The captured scope artifact is JSON `{"version":1,"sources":[...]}`; the captured policy artifact is JSON
`{"version":1,"threshold":8,"allowMissingCoverage":false}`. Replay applies those values and rejects live overrides.
Baseline and exemption hashes fail closed until their captured evaluators are supported. A Git revision may also bind
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
