# Crap4CSharp

> **Attribution:** This is an independently authored C# implementation inspired by the observable behaviour and documentation of Robert C. Martin's [`unclebob/crap4java`](https://github.com/unclebob/crap4java) at commit `69b561209f130ece728f19b0001e90df5a117c3a`. That repository's README credits `crap4clj`. The referenced repository does not state a license, so no source, tests, or prose from it were copied or translated. See [ATTRIBUTION.md](ATTRIBUTION.md).

Crap4CSharp is a .NET tool that calculates the Change Risk Anti-Patterns (CRAP) metric for concrete C# methods:

```text
CRAP = CC² × (1 − coverage)³ + CC
```

Coverage is a fraction from 0 through 1. A method violates the gate only when its known score is **strictly greater than** the threshold (default `8`). Unknown coverage is displayed as `N/A`. By default, **any** analyzed method with `N/A` coverage is an operational failure (exit `1`), preventing incomplete coverage from producing a false-green quality gate. Pass `--allow-missing-coverage` to opt into allowing `N/A`; known scores still gate normally.

## Install locally

```bash
dotnet pack src/Crap4CSharp.Tool/Crap4CSharp.Tool.csproj -c Release -o artifacts
dotnet tool install --tool-path .tools --add-source artifacts Crap4CSharp.Tool
.tools/crap4csharp --help
```

No global install, source-tree modification, or package injection is performed by the tool.

## Usage

```bash
# Default: discover production .cs files and run test coverage
crap4csharp

# Analyze explicit files/directories using an existing report (repeatable)
crap4csharp --coverage TestResults/coverage.opencover.xml src
crap4csharp --coverage linux.xml --coverage windows.xml src

# Analyze changed and untracked C# files
crap4csharp --changed --coverage coverage.xml

# Gate at a different threshold
crap4csharp --threshold 15 --project MySolution.slnx

# Explicitly permit N/A methods while still gating known scores
crap4csharp --allow-missing-coverage --coverage coverage.xml src
```

With no `--coverage`, the tool creates a unique directory beneath the OS temporary directory and runs:

```text
dotnet test <discovered-or-specified-target> --collect:"XPlat Code Coverage" ... Format=opencover
```

The project must already reference a compatible collector such as `coverlet.collector`. Crap4CSharp never installs one or changes a project. A failed test run remains exit code `1`, even if a partial report can be displayed. Tool-owned result directories are retained and printed for diagnosis; unrelated test results are never deleted.

Solutions (`.sln` or `.slnx`) are preferred when exactly one exists in the working directory, then a single `.csproj`. Use `--project` when discovery would be ambiguous. Providing any `--coverage` report skips tests and neither creates nor deletes a test-results directory. Missing, malformed, or unsupported reports fail operationally. OpenCover and Cobertura/Coverlet XML are supported; multiple reports merge the union of distinct eligible sequence points. Repeated points are counted once and are visited if any report records a visit.

## Analysis policy

- Roslyn syntax trees are used; source is never analyzed with regular expressions.
- Included: concrete ordinary methods, including block-bodied and expression-bodied, async, generic, overloaded, and methods on nested types.
- Excluded: constructors, destructors, properties, accessors, operators, local functions, lambdas, and anonymous methods. Decisions inside excluded nested functions do not increase their containing method's complexity.
- Cyclomatic complexity starts at 1 and increments for conditionals, loops, catches, switch cases/arms, `?:`, `&&`, `||`, and `??`.
- Coverage uses method sequence points, not a type-level or file-level percentage. Hidden points are excluded; a method with zero eligible points is `N/A`. Point identity includes its line and available column/end/offset coordinates so duplicate reports do not inflate the denominator.
- Report methods are matched conservatively by exact normalized source path, namespace/nested/generic type identity, method name, compatible arity, and a unique source span. There is no nearest-name fallback. Ambiguous overloads and conflicting module/assembly candidates for the same source method remain `N/A`.
- Compiler-generated async/iterator state-machine `MoveNext` methods are not reassigned to their source methods, so async or iterator source methods may remain `N/A` when a report exposes only state-machine coverage. This is intentionally conservative.
- Default recursive discovery excludes `.git`, `bin`, `obj`, `packages`, `TestResults`, `node_modules`, `test`, `tests`, and `*.Test(s)` directories, plus common generated names (`*.g.cs`, `*.generated.cs`, `*.designer.cs`, assembly info, and global usings). Explicit eligible `.cs` files are accepted even if they are under an excluded directory; explicit directories retain exclusions. An existing explicit input that is not an eligible `.cs` source file fails instead of becoming an empty success.
- C# syntax errors are operational failures; malformed syntax trees are never scored.
- `--changed` consumes NUL-delimited Git porcelain v1, including spaces, untracked files, renames/copies, and deletions. Deleted files are ignored.

Output is ordered by numeric CRAP score descending, followed by unknown (`N/A`) entries. Ties are deterministic by path and line.

## Exit codes

- `0`: analysis succeeded and no score exceeded the threshold
- `1`: usage, discovery, test, missing/ambiguous coverage (unless opted out), or other operational failure
- `2`: one or more known scores strictly exceeded the threshold

## Development

```bash
dotnet restore Crap4CSharp.slnx --disable-parallel
dotnet build Crap4CSharp.slnx -c Release --no-restore -m:1
dotnet test Crap4CSharp.slnx -c Release --no-build -m:1
dotnet pack src/Crap4CSharp.Tool/Crap4CSharp.Tool.csproj -c Release --no-build -m:1 -o artifacts
```

The sample under `samples/Fixture` provides an actual Coverlet collector integration target.

## License

The independently authored code in this repository is licensed under the [MIT License](LICENSE). This license statement applies only to this repository's original work, not to the unlicensed behavioral reference described above.
