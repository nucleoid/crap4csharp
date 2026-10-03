# Project loading proof

Measured on Linux with the repository-pinned .NET SDK 10.0.103, MSBuild 18.0.11, Roslyn Workspaces 5.0.0, and Microsoft.Build.Locator 1.11.2.

## Fixture proof

The prepared `ContextSolution.slnx` fixture was restored before loading. The isolated adapter then loaded three production contexts:

- `Production` / `net10.0`
- `MultiTarget` / `net10.0`
- `MultiTarget` / `netstandard2.1`

It excluded the evaluated `IsTestProject=true` project, excluded generated workspace documents by default with explicit records, omitted a nearby invalid `Excluded.cs` that was not a `Compile` item, retained the explicit `Shared/Linked.cs` compile item as logical `Linked/Linked.cs`, and captured `FROM_DIRECTORY_BUILD_PROPS` plus TFM-specific symbols. Source/project bytes and timestamps were unchanged across loading. The bounded child invocation completed in approximately eight seconds on this host.

The package/build-host proof is part of the release validation: the current source package is installed into an isolated local tool path and the loader fixture is executed through the installed tool assets. Linux is proven by this run; Windows remains unknown until CI executes the same package gate.

## Stop/ship decision

The selected adapter passed the Linux fixture and package-asset gates, so no filesystem-scanning fallback was introduced. The proof establishes design-time inventory only. Actual compiler-input binding requires sanitized evidence from the one declared non-incremental build; `CompiledInputBindingComplete` remains false until that evidence validates. Reuse completeness remains independently false. Custom targets, workloads, analyzers, and generators that cannot provide authoritative binding fail as unsupported rather than being represented as complete.
