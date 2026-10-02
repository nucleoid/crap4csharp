# Versioned result documents

Crap4CSharp can render its legacy evaluation as one UTF-8 JSON document with `--format json` and can atomically persist the same document with `--output <path>`. Human output remains the default. JSON stdout is exactly one document followed by a newline; progress and child diagnostics use stderr. `--output` always writes JSON, even when console output is human.

## Versioning and determinism

The top-level `schemaVersion` is currently `1.0`. Readers must reject unsupported major versions and should ignore unknown additive fields within a supported major version. The v1 schema accepts `1.x` minor versions. `complexityRulesetVersion` is `ordinary-methods-v1`, which names the existing ordinary-method syntax walker; this contract does not broaden callable analysis.

`evaluation` is normalized evidence. With identical source bytes, coverage observations, policy, and logical paths, it is stable across invocations and cultures. `run` is deliberately volatile and contains the invocation ID, timestamps, elapsed duration, child commands, diagnostic/artifact locations, cancellation details, actual terminal status, and exit code.

Legacy syntax-only contexts use `analysisMode: "syntaxOnly"` and null project, target framework, configuration, source-set identity, and external-root identity. The tool's own `net10.0` target is not presented as the analyzed consumer's target.

## Checks, decisions, and exits

Check statuses are `pass`, `fail`, `operationalError`, `cancelled`, `skipped`, `notApplicable`. Every check has a stable reason and a `required` flag. The terminal `decision.completed` and `decision.policyDecision` (`pass`, `fail`, `notApplicable`, `unknown`) are separate from runtime status.

Precedence is fixed:

1. A required operational error, cancellation, or incomplete required check exits `1`.
2. Otherwise a completed threshold violation exits `2`.
3. Otherwise the invocation exits `0`.

No eligible ordinary methods is `notApplicable`, not `pass`, and preserves the legacy exit `0`. Failed test execution remains operational failure/exit `1` even when usable coverage also proves threshold findings.

## Metrics, findings, and coverage

`metrics` inventories passing, failing, and unknown methods. `findings` contains policy violations in stable ordinal order by context, normalized `/`-separated path, method identity, span, and code. A finding ID hashes those identity fields; it excludes scores, messages, timestamps, and absolute temporary paths. JSON writes finite raw doubles without display rounding. Missing coverage and CRAP are `null`, never `0` or NaN.

Coverage reason codes are:

- `coverage.noMatchingMethod`
- `coverage.ambiguousMethod`
- `coverage.conflictingModule`
- `coverage.noEligiblePoints`
- `coverage.unsupportedGeneratedMapping`
- `coverage.unavailable`

Specific reasons are emitted only from observed evidence. Unknown coverage remains conservative. Human output marks a violation hidden by two-decimal display rounding with `>` and prints the round-trip raw score and threshold.

## Output safety

The explicit destination is written to a unique sibling temporary file and atomically replaced only after the final document is complete. Existing output remains untouched if persistence fails. Destinations that alias source, explicitly supplied or automatically discovered project/solution, or coverage inputs are rejected before any write. If an early parse/discovery failure prevents alias safety from being established, stdout includes `output.notWritten`, stderr warns that an existing destination may be stale, and the old file is preserved. Help takes precedence and performs no discovery or output write. A graceful cancellation emits a cancelled result where possible; forced process or OS termination cannot guarantee delivery.

The authoritative machine contract is [result-schema-v1.json](result-schema-v1.json).
