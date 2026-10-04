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
candidate debt and retain exit `2`. Failed/skipped tests, unknown coverage, stale source/context/build output,
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

The current provider supports source-only compiler closures. A captured metadata-reference closure fails closed
until a provider can independently bind those bytes to the references resolved by the current project loader;
producer-supplied reference paths are not accepted as proof.

Stable named allowance identity is project + TFM + configuration/platform + canonical Roslyn callable + rule +
ruleset. Paths, line numbers, source hashes, and body fingerprints are observations, not named identity. Same-symbol
body replacement retains only the component ceilings and reports the fingerprint difference. Rename/signature/ref
kind/project/TFM changes create new identity. Anonymous identities include parent/kind/token fingerprint and reject
ambiguity; family guards use the stable outer entity and `crap.nestedFamilyRisk`.
