# Fresh post-task check

`check` composes tests, fresh coverage and trusted CRAP policy. This branch implements the supported fresh path; packaged acceptance, independent whole-change review and Windows/Linux integration are separate shipping gates. Legacy option-only inspection remains available, but cannot claim fresh compiler/test binding.

```bash
crap4csharp check --project tests/App.Tests/App.Tests.csproj \
  --policy quality/crap-policy.json --base origin/main \
  --artifact-output artifacts/post-task-run --format json
```

The caller supplies an available base; the tool does not fetch it. Policy, baseline and approved exemption bytes come from the same immutable merge-base. Local proposed changes are reported, not silently trusted. All declared production/test projects and target frameworks must exist in the selected dependency graph. Policy owns permitted overrides; CLI selectors cannot narrow required targets, weaken thresholds or forge exemptions. See [policy](policy.md).

For a non-Git workspace, explicitly select `--scope all`. Policy is then read locally, labelled `local-unreviewed`, with revision `none` and content/context/workspace fingerprints. It is not reviewed-base enforcement. Local membership permits new build files under independently validated evaluated intermediate, target and restore-assets directories, including cold custom intermediate layouts. Every entry captured before preparation remains immutable even beneath those directories: existing authored files are never retrospectively reclassified or dropped. A change to a pre-existing captured output is conservatively refused; this is not unrestricted warm custom-output support. Actual compiler inputs and generated documents remain independently phase-bound.

## Preparation and execution

1. Validate grammar, trust, scope and destinations before consumer execution.
2. Discover selected-SDK metadata, then explicitly restore the selected root when allowed, **before** loading Workspace. The loader never bootstraps restore or installs a collector.
3. Freeze the mapped dependency-first graph and authored inputs. Build each supported physical project/context once, nonincrementally, without restore or implicit reference rebuilding. Independently bind actual compiler inputs, intermediate/copied PE and portable PDB, reference outputs and graph dependency copies.
4. Execute each required supported VSTest context once, with no build/no restore and the installed XPlat collector. Retain original phase proof and recheck sources, generated documents, references and outputs before/after tests. Observed test-side compilation is refused.
5. Bind actual TRX outcomes and invocation coverage to the compiled graph, validate aggregate eligible coverage, revalidate final state, capture safe replay inputs and evaluate the resolved policy.

Successful fresh completed checks keep their prepared bundle and terminal provisional until the actual result writer. One complete writer proof independently reloads current runners and graph at both intermediate and final boundaries, compares saved evidence with the original once-executed observations, and checks trusted selection, revision and physical inputs. Only then may synchronous result serialization and the guarded bundle commit advertise completed, reusable evidence. Preparation alone supplies no such verdict. Failed tests retain their actual outcomes; ordinary captured-only and saved-reuse validation keep their separate paths. The same absolute command deadline covers preparation, proof and publication.

Cold preparation uses ordinary Workspace loading. After the declared once builds, independently current graph boundaries may use one design-time acquisition per node and a fresh Roslyn project reconstructed from the executed compiler arguments and document items. The entire evaluated graph selects its path before any design-time targets: mapped dependency globals keep ordinary loading for every node. A failed warm acquisition never retries ordinary loading after targets. Runner discovery remains ordinary; authored/generated, compiler/reference/output, required-test, coverage, policy, revision and final physical revalidation gates are unchanged. No graph or acceptance proof is reused across boundaries.

The tool remains .NET 10. Supported fixture versions are `Microsoft.NET.Test.Sdk` 18.0.1, `xunit.v3` 3.2.2, `xunit.runner.visualstudio` 3.1.5 and `coverlet.collector` 6.0.4. The net9/net10 consumer fixture uses separately selected compiler, test and coverage owners; executing its net9 tests also requires the .NET 9 runtime. This is not a promise of every runner/version. MTP, unknown collector hooks, emitted generator-only trees, ambiguous associations, duplicate scheduled mapped contexts and colliding output directories are conservatively refused. A successful compiler event or a warm DLL alone is not accepted test evidence. Binding proves supported evidence consistency, not universal runtime-load tracing, hostile-filesystem atomicity or malicious-actor attestation.

### Collector and mapped-document boundaries

Git scope compares physical compiler-input bytes without running Git clean
filters. An EOL-filtered project or configuration file can therefore widen
scope even when Git's canonical text appears unchanged. Changed directories
and gitlinks also make scope incomplete and widen evaluation; the tool does
not assume a nested repository is unrelated to C# compilation.

Every eligible authored compiler/PDB method must be represented by uniquely bound aggregate coverage. `ExcludeFromCodeCoverage` on an executable authored method does not itself change policy eligibility: if the collector omits that method, fresh check refuses with `coverage.aggregateUnproven`, even when the actual tests pass. It never invents zero coverage or silently makes the omitted body not applicable. Coverlet 6.0.4 emits empty metadata-token declarations; supported association uses actual module identity, unique method names and PDB sequence points, not purported report tokens.

Async/iterator/local-function callable coverage remains `coverage.unsupportedGeneratedMapping`; independently mapped ordinary methods retain their known scores, but required unknown coverage keeps the overall check at exit 1. Compiler-generated metadata alone never excludes an explicitly authored constructor or method. Synthesized record members and nested generated bodies are classified using actual PE/PDB evidence and Roslyn source ownership; no generated `MoveNext` method is guessed back to an authored callable. The positional-record fixture retains independently known scores while its authored primary constructor remains unknown (`coverage.noMatchingMethod`). The lambda fixture similarly retains an independently covered ordinary method while anonymous mapping and ambiguous parent ownership remain unknown (`coverage.unsupportedGeneratedMapping` / `coverage.ambiguousCallableOwnership`). Both runs remain incomplete, exit 1 and expose no reusable successful bundle despite passing tests. Unsupported aggregate inventories still refuse `coverage.aggregateUnproven`. The simple non-positional record fixture, without an authored constructor or property initializer, retains known coverage for its three authored ordinary methods. The record-property initializer fixture also retains those three known method scores, but its authored initializer remains applicable and unknown (`scope.unsupportedCallable`), keeping exit 1 and incomplete/nonreusable artifacts. Its semicolon-only generated accessors are proven separately from that authored initialization. These fixtures do not establish support for every record, nested-callable or initializer form.

Deterministic compilation without mapped PDB documents is supported. Explicit `PathMap`, `#line` aliases or SDK CI source-root remapping are refused with `compiler.outputsUnproven` when a PDB document cannot resolve to the independently enumerated physical compiler document with matching checksum. In the tested SDK/xUnit consumer, `ContinuousIntegrationBuild=true` introduces such remapping in the test-project PDB and is refused before tests. No suffix matching or user-supplied coverage path map upgrades this compiler evidence. These are conservative unsupported-evidence failures, not known metric violations or failed-test claims.

`--no-restore` requires prepared assets. `--no-build` additionally requires explicit prior compiler evidence:

```bash
crap4csharp check --project tests/App.Tests/App.Tests.csproj \
  --policy quality/crap-policy.json --base origin/main \
  --no-build --build-artifacts artifacts/prior-run/manifest.json \
  --artifact-output artifacts/new-test-run --format json
```

This revalidates the current complete supported compiler graph and skips restore/compilation, **not tests**. Sources, membership, configuration, reference bytes and actual outputs must still match. No implicit bin/obj search occurs. `--build-artifacts` without `--no-build`, or with `--reuse-artifacts`, is invalid.

## Scope and outcomes

Fresh execution supports current-worktree `all`, `worktree` and `base` scope. Base mode requires current HEAD; staged scope and `--source-state head` are refused before consumer execution. `--granularity method|file` selects changed regions/files under policy rules; incomplete scope widens conservatively. See [change scopes](change-scopes.md).

Strict policy evaluates all applicable observations, even when Git reports no edits. Incremental policy evaluates selected observations, including selected nested-family guards. After genuinely successful required tests and complete eligible coverage, an empty effective CRAP inventory is explicitly `notApplicable`; no-changed incremental scope reports `crap.noApplicableChangedCallables`. Coverage remains required for eligible compiled code regardless of selected CRAP scope. A source-derived semantically empty inventory may instead make coverage not applicable, while retaining real required tests. Empty scope never bypasses tests.

- **0:** complete admitted execution/evidence and no policy violation. Overall policy success and individual CRAP applicability are separate observations.
- **1:** test, usage, unsupported/missing/ambiguous evidence, drift, timeout/cancellation or other operational failure. Bound usable findings from completed failing tests may remain visible, filtered through the original trusted policy exclusions, strict/incremental selection, baseline compatibility and exact approved exemption matching. Metrics remain diagnostic observations; the diagnostic policy calculation does not replace the failed-test decision, completion/provenance stay invalid, and no successful manifest is published. Drift and unbound partial evidence cannot supply stale metrics.
- **2:** complete execution/evidence with a known policy violation. Approved exemptions cannot waive a known violation.

`--format json` emits one terminal document. `--output` atomically writes result JSON independently of the bundle destination; input/consumer aliases are rejected. Child logs are diagnostic artifacts, not stdout JSON. `--timeout-seconds` is a finite fresh whole-run bound (default 300, range 1..86400); legacy mode bounds each command. Cancellation requests child-tree termination and bounded draining. Forced termination cannot promise terminal JSON.

## Retention, replay and reuse

SDK selection uses the installed host's native resolver in an owned worker for the verified 10.0.103 layout. It re-observes each selection and checks the host, SDK and MSBuild identity; no selection is cached across proof boundaries. Other layouts and custom resolver settings use the independent CLI probes before consumer targets run.

`--artifact-output` must name a **new** safe directory, published through owned sibling staging. Without it, the invocation-owned temporary replay bundle is retained. Locate its entry manifest in `run.artifacts`; user-directed cleanup is explicit. Once private execution storage is allocated, every graceful terminal (success, failed tests, build/capture error, cancellation or timeout) lists its exact root as `run.artifacts` kind `private-diagnostics` and in `run.diagnosticLocations`; human output also names it. Preallocation failures have no such directory. These diagnostics remain explicitly incomplete/nonreusable even when the separately listed replay manifest is complete. Raw binlogs and logs may contain secrets: keep the private root local, do not upload it as a replay bundle, and remove only that invocation-owned directory when its evidence is no longer needed. Retention lasts until user-directed cleanup; no automatic expiry is implied, and forced termination cannot guarantee a locator. On Unix the root is owner-only (0700); on Windows it inherits the user's temporary-directory permissions, not a claimed custom ACL. Raw secret-bearing binlogs, environment/property bodies and private diagnostic logs are not exported in safe replay bundles.

```bash
crap4csharp analyze --reuse-artifacts artifacts/post-task-run/manifest.json --format json
```

### Removing retained private diagnostics

After recording the evidence you need, explicitly name the private root returned
by that invocation:

```bash
crap4csharp cleanup --private-diagnostics /absolute/temp/crap4csharp-fresh-private-EXACT --older-than-days 7
```

The default age is seven days. Cleanup requires a completed lifecycle marker,
exclusive access to it, and an age of at least the requested number of days for
the root, marker and recorded completion. The root must be a direct child of
the current user's applicable temporary directory with the tool-private prefix.
Linked paths or contents, malformed markers, active or unsealed runs, and
consumer directories are refused with exit 1 before removal. Successful removal
returns 0. Cleanup accepts one exact root; it never scans for old runs or expands
wildcards. On Unix the root must also remain owner-only.

Default retention has no expiry. Removing a root also removes any default replay
bundle inside it; a separately exported bundle remains at its explicit destination.
Legacy unmarked roots and runs terminated before sealing are ineligible for this
command. These checks protect ordinary lifecycle and path mistakes; they do not
attest against hostile concurrent filesystem changes by the same user.

Captured replay needs no original checkout or project/test execution and reports historical `captured` provenance. Supported successful fresh evidence reports current `verified` binding but remains **nonreusable** when a complete future membership/environment recipe is absent. Explicit prior compiler reuse with fresh tests is distinct from `check --reuse-artifacts`, which would reuse tests as well. Current-test reuse and baseline candidate generation require their supported complete current-revalidation recipe; a fresh captured-only/nonreusable bundle is refused, never silently upgraded or retested. See [provenance](provenance.md) and [policy](policy.md).
