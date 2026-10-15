# Post-task agent integration

Use a committed local tool manifest with the exact tested package version.
Restore it during setup through the approved local/private feed. Run the gate
after C# edits from the consumer workspace, using the reviewed base supplied by
the task or repository policy:

```powershell
dotnet tool run crap4csharp -- check --project Consumer.slnx --policy crap4csharp.policy.json --base $ReviewedBase --scope all --format json
```

The independent sample includes a copyable [AGENTS.md](../samples/LocalToolConsumer/AGENTS.md).
Adapt project/policy paths after review; preserve base selection, source protection,
required tests, unknown-coverage handling and exit semantics.

Treat stdout JSON and process exit as one observation. Accept only the current
invocation's completed result. Exit 2 requires acting on findings; exit 1 blocks
the task gate until the usage, test, coverage, provenance or execution failure is
resolved. Keep private diagnostics private. Retained artifacts require exact tool,
source, policy and scope identities; their existence alone is not a passing gate.

Improve code or tests in response to findings. Baseline and exemption changes are
reviewed policy changes, not automatic repairs. Do not rewrite them, relax the
threshold, suppress unknown coverage, or change the approved base merely to
obtain a pass. CRAP does not replace functional tests or independent review.

Delivery Forge integration must normalize the four actual outcomes: pass,
threshold violation, failing tests, and operational failure. Its receipt must
retain the pinned executable/package identity, exact base/head/tree/configuration,
command, scope, JSON/exit outcome and owned artifact locators. Missing, stale or
unproven evidence remains incomplete/error. Current reuse is allowed only through
the tool's explicit validated mode; replay alone does not prove a current gate.

Normalize the current process observation as follows. Validate the supported
result schema and exact pinned tool identity first, require the process exit to
equal `run.exitCode`, and retain the original result without rewriting reasons.

| Observation | Delivery Forge outcome |
|---|---|
| Exit 0, completed `check`, completed decision, verified current workspace provenance and all required checks satisfied | `PASS` |
| Exit 2 with the current threshold/policy violation findings | `FAIL` |
| Exit 1 with an actual failed required test outcome | `FAIL`; keep the test failure primary even if partial analysis also timed out |
| Missing/unknown/stale evidence, unsupported required capability, timeout, cancellation or interrupted required execution | `INCOMPLETE` |
| Invalid invocation/configuration, malformed JSON, schema/version mismatch or inconsistent process/JSON exits | `ERROR` |
| A specific CRAP check is legitimately `notApplicable`, with its reason and real required tests completed | `NOT_APPLICABLE` for that subgate; assess the remaining required gates independently |

An otherwise unclassified exit 1 is `ERROR`, never `PASS`. Do not normalize the
whole invocation as not applicable merely because changed-code scope is empty.
The machine-readable [reason map](delivery-forge-reason-map.json) defines exit-1
evidence and invocation classifications used by the representative consumer.
Classify reasons from checks whose status is not `pass`. A passing policy-trust
check is context, and must not turn missing evidence into an invocation error.
An actual test failure requires a numeric nonzero test exit code. A `failed`
outcome with `exitCode: null` records an interrupted launch and maps to
`INCOMPLETE`; it must never be treated as a completed failing test.
Keep policy trust (`policyTrust`) in the receipt: local-unreviewed onboarding does
not establish reviewed-base enforcement. Require the configured reviewed base
and its resolved revision when that is the task's gate contract.

For a retained manifest, use the exact locator returned in `run.artifacts`:

```powershell
dotnet tool run crap4csharp -- check --reuse-artifacts $Manifest --policy crap4csharp.policy.json --base $ReviewedBase --format json
dotnet tool run crap4csharp -- analyze --reuse-artifacts $Manifest --format json
```

The first command independently revalidates complete current evidence. The
second is historical replay and cannot satisfy a current post-task gate even
when its process exits 0. Do not add fresh execution selectors such as `--scope`
to current reuse. A refusal requires diagnosing the emitted reason or creating
new fresh evidence; do not force saved declarations to complete/reusable or
silently substitute a prior result file.

Record package SHA-256 and exact tool informational version separately from the
result schema version. Record resolved base/head/tree, policy/baseline identities,
the structured argument list, scope and source-change observations, current exit
and JSON hashes, and explicit retained artifact locators. Copy only the approved
receipt/artifact allowlist to a shared gate record. Raw private binlogs, consumer
code and environment/property dumps remain private. After recording needed
evidence, an explicitly authorized cleanup can use
`crap4csharp cleanup --private-diagnostics <exact-root> --older-than-days 7`.
Use the invocation's private-diagnostics locator, never a path derived from a
finding. The command refuses active, unsealed, young, linked or unmarked roots
and consumer directories. Default replay bundles inside that root are removed
with it; preserve needed replay evidence first. See [cleanup details](check.md#removing-retained-private-diagnostics).

The installed representative-consumer matrix exercises this normalization on
Windows and Linux with the same immutable package: success becomes PASS, a real
test failure and metric violation become FAIL, and a missing policy becomes
ERROR. It also checks historical replay, current reuse, stale-source refusal,
baseline creation and independent local-manifest onboarding. This local evidence
precedes the final committed candidate; the representative demonstration must
pass against the final integrated package, alongside hosted Windows/Linux
acceptance, before declaring the integration ready. Delivery Forge's own host
implementation remains separately authorized work. Package preparation does not
authorize publication to any external feed.
