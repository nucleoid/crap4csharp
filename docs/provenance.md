# Artifact provenance v1

Crap4CSharp separates immutable evaluation from filesystem and process execution. `EvaluationEngine.Evaluate`
accepts captured source and coverage bytes, explicit parse options, policy, and a provenance observation. It does
not read files, inspect Git, load projects, run builds or tests, consult the clock, or write output.

`analyze --reuse-artifacts <manifest>` replays a completed bundle without accessing its original project roots.
All bundle locators are relative, bounded, containment-checked, and hash/length verified before evaluation. Replay
reports `captured`: it proves internal consistency of the retained evidence, not that the current checkout matches.
An invalid or incompatible explicit manifest is an operational failure and never falls back to bare XML.

The v1 integrity payload uses domain-separated, length-prefixed UTF-8 values and ordinal set ordering. It excludes
only `manifestHash` itself; source-set, input-closure and context hashes are recomputed before the manifest hash is
accepted. The published test vector is:

```text
sourceSetHash   352c257a192f9041fefd856032835e63731386e369e327be772c2580a375f928
inputClosureHash cde09b11c2e1f8023c5fac5f8468a48a42e2fae1fbcc253972ae49a24bb6f80b
contextHash     7ac8c7682d111361ff8bed3ed526c5b24c71b564f633d81e7ea603bcd7eee3ab
manifestHash    792fe91028cd56a123d252b7bb040076ea58a422d8095ee022b670e6a97f601b
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
