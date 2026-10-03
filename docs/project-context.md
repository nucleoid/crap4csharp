# Project context contract

Crap4CSharp has two deliberately different analysis contexts:

- **Project context** is an internal, versioned `project-context-v1` adapter used by the forthcoming `check` workflow. It loads prepared MSBuild projects in an isolated process and captures Roslyn `Document` inputs, logical link paths, target framework, configuration/platform, language version, preprocessor symbols, evaluated imports, generated/test exclusions, and SDK/MSBuild/Roslyn identities.
- **Syntax-only context** is the existing command-line behavior. It discovers files and parses them with the tool's default parser. It remains labelled `syntaxOnly` and does not claim compiler-input, project, or target-framework fidelity.

Project loading never falls back to syntax-only analysis. A missing SDK, restore assets, project, target framework, reference pack, build host, source path, or generated-source capability is an operational context failure.

## Preparation and execution boundary

The loader does not restore, build, add packages, run tests, or call Roslyn's mutating workspace APIs. Callers must first restore the consumer's already-declared dependencies. `ProjectBuildPreparation` exposes serialized restore and non-incremental `--no-restore -m:1` build operations for the eventual check orchestrator; it does not itself constitute that workflow.

`MSBuildWorkspace` design-time documents establish `DesignTimeComplete` only. `CompiledInputBindingComplete` stays false until sanitized compiler observations and output identities are supplied and `CompiledInputCapture.Validate` proves an exact context/source match. `ReuseRecipeComplete` is separate and remains false in this slice. Unknown, drifted, or incomplete evidence fails closed.

The selected consumer SDK is resolved by `dotnet` from the consumer target directory, so a consumer `global.json` participates. The child and its BuildHost receive the resolved host, `DOTNET_ROOT`, and `PATH`; MSBuild is registered from that exact SDK before any workspace type is used. Every selected project directory is rechecked and a nested SDK disagreement fails `context.sdkMismatch`. Each target framework is loaded in a separate workspace with its own global properties.

## Identity and exclusions

Context identity is a length-prefixed canonical SHA-256 manifest over logical project identity, assembly, TFM, effective configuration/platform, language/source kind, sorted symbols, raw-byte content-addressed evaluated imports, included source physical/logical identities and hashes, exclusions and policy, plus adapter versions. Absolute temporary roots, timestamps, and Roslyn project IDs are excluded. Linked inputs retain separate physical and logical identities.

The full workspace compile inventory is observed before policy filtering. Test projects (from evaluated `IsTestProject`) and generated documents are disclosed as exclusions unless explicitly included. Generated inclusion requests compilation-backed syntax trees; unresolved analyzer/generator references, load failures, and generator diagnostics are operational failures. Directory names alone do not classify projects as tests. SolutionPersistence enumerates `.slnx` entries and mappings; classic MSBuild solution APIs do the same for `.sln`, and non-C# or configuration-disabled projects become explicit exclusions.

## Trust and mutation

MSBuild evaluation and design-time loading can execute imported logic. This is appropriate only for repositories the operator trusts; it is not a sandbox for hostile projects. The parent snapshots authored C#, project, solution, props, targets, configuration, response, and JSON inputs before loading; the child additionally snapshots evaluated imports and compile items around workspace loading. Either side fails `context.inputsMutated` if observed inputs change. It does not revert files. Normal prepared `obj`/`bin` outputs are outside that authored-input comparison.

The protocol uses task-owned request/response files so loader logs cannot corrupt structured output. The child process is bounded and process-tree cancellation is inherited from the common runner.

Public `check`, captured `analyze`, policy constraints, test execution, artifact replay, and coverage-to-context manifests are owned by later roadmap slices and are not advertised here.
