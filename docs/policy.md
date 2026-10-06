# Repository policy and reviewed baselines

Repository policy is an opt-in, versioned contract for `callables-v1`. A strict policy evaluates every applicable
callable. An incremental policy evaluates selected changed/new callables and permits existing debt only when all
three raw components remain within the reviewed baseline: CRAP must not rise, complexity must not rise, and eligible
point coverage must not fall. Equality passes. Improvements in one component never offset regression in another.

Policy JSON is strict: duplicate or unknown keys, unsupported checks, non-finite/negative thresholds, absolute or
escaping paths, wildcard missing-coverage defaults, and unsupported rulesets fail with exit `1`. The only v1 required
checks are `tests`, `coverage`, and `crap`. A strict policy may omit `baseline`; incremental policy requires it.
Baseline and exemption paths are relative to the policy directory and remain inside the repository.

## Trust model

With a CI-owned `--base`, enforcement resolves the merge base and reads the policy, baseline, and every exemption
blob from that same immutable Git tree. Current-branch edits are reported as proposed differences but do not alter
enforcement. CLI selectors cannot remove required projects, tests, TFMs, coverage, or add exclusions/exemptions
unless the trusted policy explicitly allows that exact class of override. A local policy is labelled
`local-unreviewed`; a checksum, review string, or exact callable ID supplied by the branch is not approval.

Captured `analyze` uses the scope and threshold from the hash-bound policy artifact stored in the bundle and reports
capture-consistent metrics; it does not apply reviewed baseline allowances or exemptions. It performs no Git,
project, filesystem, or process acquisition. Reviewed baseline/exemption enforcement belongs to trusted `check`.

Trusted enforcement is explicit:

```bash
crap4csharp check --reuse-artifacts artifacts/crap-run/manifest.json \
  --policy quality/crap-policy.json --base origin/main --format json
```

The command resolves the merge base once, reads every enforcement overlay from that tree, independently observes
the trusted scope, revalidates current project/source/PE/PDB evidence, and reports branch versions only as
proposed differences. Authored source entries must carry hash-bound repository-relative paths; project-relative
compiler names remain separate so linked files cannot be confused with similarly named files. Base-trusted checks
also require captured Git HEAD/worktree-state evidence; revision `none` is local-only. The command consumes an
existing bundle; fresh restore/build/test/coverage orchestration is separate.

## Candidate workflow

First adoption uses a strict policy without a baseline. Capture a complete successful build/test/coverage run, then:

```bash
crap4csharp baseline create \
  --policy samples/policy/strict.json \
  --reuse-artifacts artifacts/crap-run/manifest.json \
  --output quality/crap-baseline.candidate.json
```

The command writes only the explicit candidate destination. Existing destinations require `--overwrite`; normal
analysis/checking never creates, trims, transfers, or rewrites a baseline. Known above-threshold metrics are eligible
candidate debt and retain exit `2`. Known complexity violations with unknown coverage cannot receive a numeric
allowance: the summary's `omittedKnownViolations` lists each such entity and reason. An approved unsupported mapping
can permit a candidate without fabricated coverage, but never excuses complexity above threshold; touching that
entity in incremental mode still fails until refactored or supported coverage is available.
Failed/skipped tests, unexempted unknown coverage, stale source/context/build output,
unsupported revalidation recipes, incompatible policy/ruleset/schema, or tampered bundle bytes fail `1` and write no
candidate. Candidate metadata is deterministic and contains no timestamp. Generation is not approval: review the
candidate, move it to the policy's baseline path, and commit both before enabling incremental CI.

The candidate binds a policy compatibility hash rather than the full presentation hash. Strict adoption and
incremental enforcement may differ only in mode, scope, and baseline location. Threshold, projects, test targets,
TFMs, configuration, required checks, exclusions, ruleset, and the exact bytes of every exemption remain bound;
changing any of them invalidates the baseline.

`baseline update` follows the same rules and additionally requires a valid existing baseline referenced by the
strict update policy. Deleted entries are reported and ignored during evaluation; checks never auto-trim them.

## Evidence capability

Historical manifest-v1 bundles remain capture-consistent but non-revalidatable. Baseline generation supports only a
hash-bound `sdk-project-context-output-v1` recipe. It reloads the declared SDK project/TFM/configuration, compares the
complete evaluated source membership and context identity, and independently inspects the named current PE/PDB
against the captured tested build. New imports/sources/generated inputs, authored drift, stale outputs, missing test
success, or a bare self-asserted `reuseRecipeComplete` flag fail closed. A successful current check may still be
non-reusable for future replay; reusable and currently verified are separate capabilities.

`check --reuse-artifacts` and `baseline create|update` refuse missing, historical, unknown-provider,
or incomplete current-revalidation recipes with `provenance.revalidationRecipeUnsupported` before
Git acquisition or project loading. Such bundles can still be inspected with offline `analyze`;
recapture supported current evidence instead of upgrading a saved completeness flag.

The current provider supports source-only compiler closures. A captured metadata-reference closure fails closed
until a provider can independently bind those bytes to the references resolved by the current project loader;
producer-supplied reference paths are not accepted as proof. Current source-only revalidation does not independently
reload test-project sources or resolve a producer's `TestProject` string to its actual test DLL/PDB. CI must produce
its own bundle from the declared successful test run; do not accept a branch-supplied bundle as signed test evidence.
The fresh orchestration producer must bind the declared test targets; hashes establish consistency, not approval.

Stable named allowance identity is project + TFM + configuration/platform + canonical Roslyn callable + rule +
ruleset. Paths, line numbers, source hashes, and body fingerprints are observations, not named identity. Same-symbol
body replacement retains only the component ceilings and reports the fingerprint difference. Rename/signature/ref
kind/project/TFM changes create new identity. Anonymous identities include parent/kind/token fingerprint and reject
ambiguity; family guards use the stable outer entity and `crap.nestedFamilyRisk`.

## Narrow exemptions and safe destinations

Reviewed exemptions match stable callable identity, current target framework, body checksum, ruleset and the exact
unsupported reason; captured content-context IDs are diagnostic, not stable approval keys. Duplicate anonymous
bodies additionally require the reviewed `memberCount` and every member's matching body/family acknowledgement.
Named collisions cannot use an anonymous exemption. Exemptions never waive a known complexity above threshold.

### Approving a new or edited unsupported callable

Version 1 has no CI-authorized external approval-source option. A branch cannot approve its own new exemption:
base-trusted checks use only merge-base bytes. A changed body checksum makes the old exemption stale and an
unsupported selected callable exits `1` (unless complexity already proves a violation, which exits `2`).
Use two reviewed phases: first approve the exact future stable callable/body/reason and family acknowledgements
in the base tree, regenerating a candidate with `baseline create` under the changed exemption binding and reviewing
its numeric entries; then rebase the implementation onto that approved commit and capture fresh evidence.
`baseline update` cannot migrate an old incompatible exemption binding. Do not use `--base HEAD` to make a branch
approve itself. If approving a future body separately is unsuitable, keep the gate failing and refactor or obtain
supported coverage instead.

Git-tracked generated-looking files (including `Resources.Designer.cs`) remain authored. A collector's
`GeneratedCode` exclusion can therefore leave required coverage unknown. Record reviewed policy exclusions or
use a policy-authorized generated-source selector; an `obj/` name or attribute alone cannot exempt authored code.

Generated compiler documents are classified using their physical evaluated `IntermediateOutputPath` or
`BaseIntermediateOutputPath`, never `Link` or an `obj/` spelling. Git-tracked compiler files and target-added sources
outside those outputs remain authored. Current revalidation independently repeats this classification, including
custom artifacts layouts; a producer's generated flag is not proof. Generator trees are separately identified.

Check output refuses an existing destination unless it is a prior result document; baseline replacement requires
an existing valid candidate plus `--overwrite`. Explicit source, project, import, artifact and policy input aliases
remain forbidden even when their bytes resemble a result/candidate. This refusal also runs before loader failures,
so an unsuccessful command cannot replace a consumer input with diagnostics.

Candidate `sourceIdentity` hashes repository-relative source paths/content and project/TFM/configuration/platform. It does not depend on an absolute checkout root or diagnostic capture context IDs; the separate recorded revision and evidence hashes still describe the originating capture. Base-scope enforcement also requires the independently resolved scope merge-base to equal the immutable revision used for trusted policy acquisition.

### Ambiguous collector ownership in candidate generation

Baseline creation and trusted strict checking use the same complete observation inventory and family dependencies.
Unapproved unknown ambiguity refuses generation (exit 1, no candidate); known complexity above the threshold remains
a violation (exit 2) but cannot receive a numeric allowance. `skippedAmbiguousEntries` lists each ambiguous group
with its actual kind, entity key, rule, repository path, maximum complexity, and coverage reason, on both success and
operational failure. `omittedKnownViolations` includes known complexity debt before ambiguous entries are omitted.

Real Coverlet OpenCover can project duplicate identical lambda points into their named parent, leaving that parent
ambiguous too. An exact anonymous `memberCount` exemption does **not** waive the named parent. Refactor the duplicate
bodies or provide supported exclusive point evidence; do not approve a named-parent anonymous exemption. The
exact-point integration control is an adapter test, not proof that Coverlet emits exclusive coordinates.

## Required evidence and conservative path support

Version 1 always enforces successful declared tests, compatible provenance, complete required coverage (or exact
approved unsupported exemptions), and numeric policy ceilings. `requiredChecks` records required-check presentation
flags and participates in baseline compatibility; removing a name is not an opt-out and never disables enforcement.
Trusted `check` and candidate generation require all declared tests passed, with no failures or skips, even for empty
method scope. Incomplete execution reports `provenance.testExecutionIncomplete` and cannot produce a trusted green.

Current generated-input classification and existing-output checks conservatively reject reparse points anywhere
in the physical ancestor chain. Use a regular physical checkout/output path; ancestor-symlink layouts (including
macOS `/var/folders` aliases and symlinked home directories) are not currently verified supported. These guards
have not been relaxed to make an unverified platform green. Linux and Windows exact-head CI remain merge gates.

Trusted check recalculates replay report thresholds, including `families[].isViolation`, using the approved policy,
not the threshold captured by a branch. Family findings carry aggregate complexity/coverage/CRAP and the root
callable's source span. Offline `analyze` continues to report the captured policy without live trust acquisition.
Trusted Git acquisition and current revalidation use the invocation's `--timeout-seconds` budget per command;
cancellation terminates acquisition, and timed-out Git output draining cannot wait indefinitely on inherited pipes.

Git-scope source matching uses exact repository spelling. A case-only mismatch between a current physical Compile path and a changed Git path (including changed-source exclusions) fails `policy.sourcePathCaseMismatch`, rather than silently omitting that callable. Align explicit Compile paths with Git spelling. Case-colliding changed sources are conservatively refused even on a case-sensitive filesystem; no fuzzy identity transfer is performed.
