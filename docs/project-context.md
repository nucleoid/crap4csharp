# Project context contract

Crap4CSharp has two deliberately different analysis contexts:

- **Project context** is an internal, versioned `project-context-v1` adapter used by the `check` workflow. It loads prepared MSBuild projects in an isolated process and captures Roslyn `Document` inputs, logical link paths, target framework, configuration/platform, language version, preprocessor symbols, evaluated imports, generated/test exclusions, and SDK/MSBuild/Roslyn identities.
- **Syntax-only context** is the existing command-line behavior. It discovers files and parses them with the tool's default parser. It remains labelled `syntaxOnly` and does not claim compiler-input, project, or target-framework fidelity.

Project loading never falls back to syntax-only analysis. A missing SDK, restore assets, project, target framework, reference pack, build host, source path, or generated-source capability is an operational context failure.

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
