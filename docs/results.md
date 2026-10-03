# Versioned result documents

Crap4CSharp can render its legacy evaluation as one UTF-8 JSON document with `--format json` and can atomically persist the same document with `--output <path>`. Human output remains the default. JSON stdout is exactly one document followed by a newline; progress and child diagnostics use stderr. `--output` always writes JSON, even when console output is human.

## Versioning and determinism

The top-level `schemaVersion` is currently `1.0`. Readers must reject unsupported major versions and should ignore unknown additive fields within a supported major version. The v1 schema accepts `1.x` minor versions. `toolVersion` comes from the executing assembly's informational version, including build metadata when present. `complexityRulesetVersion` is `ordinary-methods-v1`, which names the existing ordinary-method syntax walker; this contract does not broaden callable analysis.

`evaluation` is normalized evidence. With identical source bytes, coverage observations, policy, and logical paths, it is stable across invocations and cultures. `run` is deliberately volatile and contains the invocation ID, timestamps, elapsed duration, child commands, diagnostic/artifact locations, cancellation details, actual terminal status, and exit code.

Explicit coverage inputs retain their normalized workspace-relative paths. Coverage produced in an invocation-owned temporary directory uses a content-addressed logical path under `<generated>/coverage/` in `evaluation.artifacts`; its physical temporary location appears only in `run.artifacts`.

Legacy syntax-only contexts use `analysisMode: "syntaxOnly"` and null project, target framework, configuration, source-set identity, and external-root identity. The tool's own `net10.0` target is not presented as the analyzed consumer's target.

## Checks, decisions, and exits

Check statuses are `pass`, `fail`, `operationalError`, `cancelled`, `skipped`, `notApplicable`. Every check has a stable reason and a `required` flag. The terminal `decision.completed` and `decision.policyDecision` (`pass`, `fail`, `notApplicable`, `unknown`) are separate from runtime status.

Precedence is fixed:

1. A required operational error, cancellation, or incomplete required check exits `1`.
2. Otherwise a completed threshold violation exits `2`.
3. Otherwise the invocation exits `0`.

No eligible ordinary methods, or no methods with a known CRAP score under the explicit missing-coverage opt-out, is `notApplicable`, not `pass`, and preserves the legacy exit `0`. Failed test execution remains operational failure/exit `1` even when usable coverage also proves threshold findings.

## Metrics, findings, and coverage

`metrics` inventories passing, failing, and unknown methods. `findings` contains policy violations in stable ordinal order by context, normalized `/`-separated path, method identity, span, and code. `entityKey` hashes the legacy syntax context, normalized path, complexity ruleset, finding code, and an internal canonical syntax signature. That signature includes namespace, containing-type generic arity, explicit-interface qualification, method generic arity, and parameter modifiers/types so legal overloads remain distinct. It deliberately excludes content identities and line positions, and it does not claim future project/TFM or semantic-symbol identity. The displayed `methodIdentity` remains the backward-compatible human identity and is not the key. `id` is the observation ID and additionally binds the current source span. Both hashes exclude scores, messages, timestamps, and absolute temporary paths. JSON writes finite raw doubles without display rounding. Missing coverage and CRAP are `null`, never `0` or NaN.

Coverage reason codes are:

- `coverage.noMatchingMethod`
- `coverage.ambiguousMethod`
- `coverage.conflictingModule`
- `coverage.noEligiblePoints`
- `coverage.unsupportedGeneratedMapping`
- `coverage.unavailable`

Specific reasons are emitted only from observed evidence. Unknown coverage remains conservative. Human output marks a violation hidden by two-decimal display rounding with `>` and prints the round-trip raw score and threshold.

## Output safety

The explicit destination is written to a unique sibling temporary file and atomically replaced only after the final document is complete. Existing output remains untouched if persistence fails. Destinations that alias source, explicitly supplied or automatically discovered project/solution, or coverage inputs are rejected before any write; project/build input extensions are protected even when referenced indirectly by a solution. Byte equality alone is not treated as aliasing because atomic replacement does not write through an unrelated or hard-linked inode. An absent destination can safely receive an early parse/discovery error document. If an existing destination cannot be proven safe before discovery completes, stdout includes `output.notWritten`, stderr warns that the destination may be stale, and the old file is preserved. The file is finalized before terminal stdout, so it records the pre-stdout outcome; if terminal output subsequently fails, the process returns `1` and diagnoses that failure on stderr where possible, but the already-atomic file is not rewritten. Help takes precedence and performs no discovery or output write. A graceful cancellation emits a cancelled result where possible; forced process or OS termination cannot guarantee delivery.

The authoritative machine contract is [result-schema-v1.json](result-schema-v1.json).
