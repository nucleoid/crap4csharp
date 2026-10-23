# Project context contract

Crap4CSharp has two deliberately different analysis contexts:

- **Project context** is an internal, versioned `project-context-v1` adapter used by the `check` workflow. It loads prepared MSBuild projects in an isolated process and captures Roslyn `Document` inputs, logical link paths, target framework, configuration/platform, language version, preprocessor symbols, evaluated imports, generated/test exclusions, and SDK/MSBuild/Roslyn identities.
- **Syntax-only context** is the existing command-line behavior. It discovers files and parses them with the tool's default parser. It remains labelled `syntaxOnly` and does not claim compiler-input, project, or target-framework fidelity.

Project loading never falls back to syntax-only analysis. A missing SDK, restore assets, project, target framework, reference pack, build host, source path, or generated-source capability is an operational context failure.

Fresh captured scored contexts require repository-owned compiler source paths, including when `--include-tests` makes a test context scored. External linked or NuGet-injected source is not silently rewritten as repository source; this layout currently fails closed. The public outcome-join fixtures use xUnit's standard `XunitRegisterBuiltInRunnerReporters=false` property before restore/build to omit its optional external reporter-registration source. They retain the actual test entry point, VSTest, Coverlet and authored test-callable checks. This fixture choice does not establish general external-source support or alter the physical compiler closure.

Fresh-check public callable `contextId` selectors use the complete repository-relative project role, target framework, configuration/platform and every mapped global property. Property names are case-insensitive; values remain exact, including path-valued custom globals. Relocating an otherwise identical checkout therefore preserves the selector when those globals agree. Distinct full project roles or mapped values remain distinct. No physical-prefix rewriting is performed.

Public test outcomes use that same selector, including failed or interrupted outcomes. Project-role spelling is preserved on every operating system, as it is for callable IDs; there is no Windows-only case folding. This requires the same evaluated repository-relative spelling. Differently cased solution or project-reference aliases are not declared equivalent by this selector. Physical path ownership keeps its separate operating-system path comparison policy.

This selector is separate from the private physical graph identity. Compiler/output ownership, root mappings, current context hashes, prior-build workspace/fingerprint comparisons and completed-test replay remain bound to the actual physical workspace. Matching public selectors do not authorize foreign-workspace replay. The new selector changes IDs from earlier candidates that exported physical graph IDs; regenerate evidence with the new pinned producer rather than rewriting historical reports or baseline keys. Packaged relocation acceptance remains required.

The result and manifest producer versions come from the built assembly's `AssemblyInformationalVersion`. Candidate packaging must pin that metadata to the actual complete source revision; the shared `0.1.0` development package version alone does not identify a selector implementation. Regenerate evidence produced with earlier physical or Windows-case-folded selectors; mismatched selectors fail closed at saved-currentness gates. There is no schema migration or automatic rewriting of historical context, observation or finding IDs; callable baseline keys and exemption matching retain their existing contracts.

## Preparation and execution boundary

The loader does not restore, build, add packages, run tests or write consumer source. It may construct an in-memory Roslyn workspace. Callers first restore the consumer's already-declared dependencies. The fresh-check orchestrator uses `ProjectBuildPreparation` for serialized restore and non-incremental `--no-restore -m:1` builds. MSBuild evaluation and design-time targets can execute imported logic, as described below.

`MSBuildWorkspace` design-time documents establish `DesignTimeComplete` only. `CompiledInputBindingComplete` stays false until sanitized compiler observations and output identities are supplied and `CompiledInputCapture.Validate` proves an exact context/source match. `ReuseRecipeComplete` is separate: design-time loading alone leaves it false; a supported completed-check provider must prove the full current recipe. Unknown, drifted, or incomplete evidence fails closed.

The selected consumer SDK is resolved by `dotnet` from the consumer target directory, so a consumer `global.json` participates. The child and its BuildHost receive the resolved host, `DOTNET_ROOT`, and `PATH`; MSBuild is registered from that exact SDK before any workspace type is used. Every selected project directory is rechecked and a nested SDK disagreement fails `context.sdkMismatch`. Every target framework retains its own effective global properties.

When dependency globals differ, mapped graph contexts use an invocation-owned source workspace. The selected SDK
supplies design-time compiler arguments with actual compilation and dependency builds disabled. A metadata reference
can become a source project reference only when its executed source-project metadata and output path identify one
authorized graph node. Reference aliases and embedded-interop flags are preserved. When multiple authorized
instances share a declared reference path and the SDK supplies no unique producer identity, discovery retains the
unresolved path and marks that context incomplete; it does not choose a source producer. Compilation and compiled-input
promotion refuse unresolved ownership before effects. Missing or mixed undeclared references refuse acquisition.
The loader snapshots all declared graph inputs before targets and checks bytes and source
membership after each acquisition. Globals that override the noncompiling controls refuse before any targets.
Ordinary contexts keep their separate workspaces; mapped source projects share a workspace while retaining each
node's effective framework, configuration, platform and custom globals.

Fresh execution freezes the initial SDK version, full MSBuild version, SDK/host paths and resolution route.
Later boundaries still resolve the SDK independently; restore, build and test launch only when that observation
matches the frozen selector. A changed selector refuses before launch. Binlog readers and retained current proofs
also retain the execution selector. This is selector consistency, not an atomic filesystem or environment sandbox.

## Identity and exclusions

Context identity is a length-prefixed canonical SHA-256 manifest over logical project identity, assembly, TFM, effective configuration/platform, language/source kind, sorted symbols, raw-byte content-addressed evaluated imports, included source physical/logical identities and hashes, exclusions and policy, plus adapter versions. Absolute temporary roots, timestamps, and Roslyn project IDs are excluded. Linked inputs retain separate physical and logical identities.

The full workspace compile inventory is observed before policy filtering. Test projects (from evaluated `IsTestProject`) and generated documents are disclosed as exclusions unless explicitly included. Generated inclusion requests compilation-backed syntax trees; unresolved analyzer/generator references, load failures, and generator diagnostics are operational failures. Directory names alone do not classify projects as tests. SolutionPersistence enumerates `.slnx` entries and mappings; classic MSBuild solution APIs do the same for `.sln`, and non-C# or configuration-disabled projects become explicit exclusions.

## Trust and mutation

MSBuild evaluation and design-time loading can execute imported logic. This is appropriate only for repositories the operator trusts; it is not a sandbox for hostile projects. The parent snapshots authored C#, project, solution, props, targets, configuration, response, and JSON inputs before loading; the child additionally snapshots evaluated imports and compile items around workspace loading. Either side fails `context.inputsMutated` if observed inputs change. It does not revert files. Normal prepared `obj`/`bin` outputs are outside that authored-input comparison.

The protocol uses task-owned request/response files so loader logs cannot corrupt structured output. The child process is bounded and process-tree cancellation is inherited from the common runner.

Captured `analyze` and trusted reused `check` consume this context through a hash-bound manifest. The supported
current-revalidation recipe reloads the declared project/TFM/configuration and inspects current PE/PDB outputs; it
does not itself restore, build, or test. The fresh-check orchestrator separately executes the required build/test plan and produces bundles only after their applicable proof gates.

## Required graph Workspace preparation (internal)

`LoadGraphAsync` retains each dependency-first graph node and all of its global properties, including custom
`AdditionalProperties`, not only root configuration/TFM. Ordinary nodes use their own workspace; mapped nodes
can share an invocation-owned workspace with their explicit project-reference associations. Its private association
retains graph ID, physical project path, design-time context and evaluated test-project classification.
Required test contexts remain present even when production inventory excludes tests. Loading requires existing
restore assets and never implicitly restores or compiles. These associations do not establish actual Csc inputs,
test execution, final output binding or reusable evidence; the fresh-check workflow proves those separately.

Mapped global properties distinguish build contexts and can travel through private
worker requests and MSBuild arguments. Do not put credentials in them; use the
consumer's approved private credential mechanism. The guard refuses conventional
credential names, including mapped password, token and connection-key variants.
It cannot determine whether an arbitrary property value is secret. Deterministic
identity hashes are not encryption, and private diagnostics must remain private.

Optional compiler recipe capture has finite independent observation/prior limits. Mandatory capture retains its existing read and stored-byte ceilings. Optional artifacts are admitted only after mandatory data and the mandatory prior graph are accounted for. A compact unsupported artifact is used when it fits; otherwise the optional recipe and its associated artifacts are absent, as with older bundles. Absence supplies no compiler, coverage, freshness or reusable-execution authority. Known malformed task/argument evidence remains an error before any capacity omission. These bounds do not guarantee availability for every workload or extend the invocation deadline.

Explicit `--no-build` and saved verification recheck every supported retained compiler-image identity against the current trusted SDK role pool. Unsupported retained profiles preserve only their original observation and fixed public unsupported DATA, with no exported compiler-image or recipe authority; their structural integrity is still validated before restoration. Drift refuses that prior evidence; it never launches a replacement compiler or restamps the retained original command binding. Use an ordinary fresh check when a cold build is wanted. Unchanged supported warm recipes retain their original binding and bytes.

Multi-alias references and exactly joined generated-output paths outside the intermediate assembly directory are unsupported optional profiles. Every observed reference path, alias member, embed flag and generated-output argument must still match its captured task input; missing, extra, duplicate or foreign members remain errors. Fresh optional capture records SDK-image byte drift as `recipe.compilerImagesChanged` without exporting those images. Saved supported recipe verification keeps exact byte equality and refuses drift. Neither route rebuilds or restamps an earlier Csc.

Fresh completed promotion carries only invocation-private optional recipe loss state tied to the same original compiler observations and retained physical proof. The terminal writer reconstructs permitted size/SDK-image/absence demotions from those originals and matches exact unsupported bytes or absence, rejecting stray recipe/image artifacts. Mandatory source, output, graph, runner and native/physical proof is unchanged; saved-only verification has no such private selection and remains strict. This state is not serialized and grants no new receipt, freshness or body-origin authority.

Supported command observation includes the exact Linux-selected SDK `Roslyn/bincore/csc` apphost, labelled `selected-sdk-csc-unix-apphost-arguments-match`. Its finite regular launcher bytes join the opening and closing selected SDK pool; launcher addition/removal or byte drift yields unsupported optional DATA. The `dotnet exec csc.dll` profile retains its eight-image pool when the Unix apphost is absent. If a launcher is present, it belongs to the current selected SDK pool even when that command uses host-exec; a reparse, unreadable or oversized launcher makes the optional recipe `recipe.compilerImageUnavailable`. Mandatory compiler capture is unchanged. This conservative availability limit does not authorize following the launcher or dropping its present identity. A Unix apphost binding requires the ninth `csc-launcher` identity; a transient launcher missing from both snapshots cannot produce supported input DATA. Relative, foreign or reparse launchers are not admitted. These observations do not attest a loaded image, executed task or currentness to a downstream consumer.

Linux bundles captured before the Unix apphost profile was added may retain an unsupported binding with only eight SDK identities even when that SDK already contained `csc`. Restoring those records with the new nine-role pool deliberately refuses; supported manifest major/minor versions do not promise identical optional SDK profiles. Regenerate such evidence with an ordinary fresh check. No `--no-build` fallback recompiles or restamps the prior observation. Older consumers also refuse the new Unix tag, so use the updated Forge decoder before or together with producer output that needs this profile.
