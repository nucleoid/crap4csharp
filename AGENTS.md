# AGENTS.md

## Scope

This repository contains a .NET 10 global/local tool and its focused core library.

## Commands

Always make the repository-local SDK available for commands in this environment:

```bash
PATH=/home/fuego/.dotnet:$PATH dotnet restore Crap4CSharp.slnx --disable-parallel
PATH=/home/fuego/.dotnet:$PATH dotnet build Crap4CSharp.slnx -c Release --no-restore -m:1
PATH=/home/fuego/.dotnet:$PATH dotnet test Crap4CSharp.slnx -c Release --no-build -m:1
```

## Guardrails

- Target `net10.0`.
- Keep syntax analysis in `Crap4CSharp.Core`; the tool project should remain orchestration/UI only.
- Use Roslyn nodes, not regex, to analyze C#.
- Unknown or ambiguous coverage must remain unknown; do not manufacture zero coverage. Any N/A is an operational failure by default; `--allow-missing-coverage` is the explicit opt-out while known scores continue to gate.
- Merge multiple reports as a union of distinct eligible sequence points, never summed denominators. Keep file/type/name/arity/module matching conservative; do not map generated `MoveNext` methods back by guesswork.
- Preserve exit codes: 0 success, 1 usage/operations, 2 threshold violation.
- Do not install packages into analyzed projects, write analyzed source files, or delete result directories not created by the current invocation.
- Keep the clean-room attribution prominent. Do not copy or translate the unlicensed reference implementation.
- Run the unit suite, package/install smoke test, deterministic fixture gates, and real Coverlet E2E before release.
