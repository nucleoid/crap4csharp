# Crap4CSharp

> **Attribution:** This is an independently authored C# implementation inspired by the observable behaviour and documentation of Robert C. Martin's [`unclebob/crap4java`](https://github.com/unclebob/crap4java) at commit `69b561209f130ece728f19b0001e90df5a117c3a`. That repository's README credits `crap4clj`. The referenced repository does not state a license, so no source, tests, or prose from it were copied or translated. See [ATTRIBUTION.md](ATTRIBUTION.md).

Crap4CSharp is a .NET tool that calculates the Change Risk Anti-Patterns (CRAP) metric for C# executable code:

```text
CRAP = CC² × (1 − coverage)³ + CC
```

Coverage is a fraction from 0 through 1. A method violates the gate only when its known score is **strictly greater than** the threshold (default `8`). Unknown coverage is displayed as `N/A`. By default, **any** analyzed method with `N/A` coverage is an operational failure (exit `1`), preventing incomplete coverage from producing a false-green quality gate. Legacy `ordinary-methods-v1` option-only/analyze invocations may pass `--allow-missing-coverage` to inspect known scores despite `N/A`; `callables-v1` rejects that broad opt-out.

## Install locally

Prerequisite: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet pack src/Crap4CSharp.Tool/Crap4CSharp.Tool.csproj -c Release -o artifacts
dotnet tool install --tool-path .tools --add-source artifacts Crap4CSharp.Tool
.tools/crap4csharp --help
```

No global install, source-tree modification, or package injection is performed by the tool.

The option-only command remains the exact **`ordinary-methods-v1` legacy dialect**. The opt-in
`analyze --syntax-only` command uses **`callables-v1`** by default and performs no build, restore,
test, Git, or other child process. Full project-aware `check` orchestration remains deferred.

## Usage

```bash
# Default: discover production .cs files and run test coverage
crap4csharp

# Analyze explicit files/directories using an existing report (repeatable)
crap4csharp --coverage TestResults/coverage.opencover.xml src
crap4csharp --coverage linux.xml --coverage windows.xml src

# Translate a report produced on another host/container (two operands per map)
crap4csharp --coverage-path-map 'C:\agent\repo\src' "$PWD/src" --coverage windows.xml src
crap4csharp --coverage-path-map /agent/repo/src "$PWD/src" --coverage container.xml src

# Analyze changed and untracked C# files using the legacy whole-file mode
crap4csharp --changed --coverage coverage.xml

# Gate at a different threshold
crap4csharp --threshold 15 --project MySolution.slnx

# Bound each Git or dotnet child command to 10 minutes
crap4csharp --timeout-seconds 600 --project MySolution.slnx

# Explicitly permit N/A methods while still gating known scores
crap4csharp --allow-missing-coverage --coverage coverage.xml src

# Emit one versioned machine-readable result document
crap4csharp --format json --coverage coverage.xml src

# Atomically persist the same JSON contract while retaining human console output
crap4csharp --output artifacts/crap-result.json --coverage coverage.xml src

# Inventory all authored callable regions without launching any process
crap4csharp analyze --syntax-only --coverage coverage.opencover.xml src

# Inspect the exact legacy dialect through the pure analyze adapter
crap4csharp analyze --syntax-only --ruleset ordinary-methods-v1 --coverage coverage.xml src

# Load exact local exemptions for inspection (never trusted approval)
crap4csharp analyze --syntax-only --callable-exemptions exemptions.json --coverage coverage.xml src
```

With no `--coverage`, the tool creates a unique directory beneath the OS temporary directory and runs:

```text
dotnet test <discovered-or-specified-target> --collect:"XPlat Code Coverage" ... Format=opencover
```

The project must already reference a compatible collector such as `coverlet.collector`. Crap4CSharp never installs one or changes a project. A failed test run remains exit code `1`, even if a partial report can be displayed. Tool-owned result directories are retained and printed for diagnosis; unrelated test results are never deleted.

Every external command has a bounded timeout of 300 seconds by default. Set `--timeout-seconds` to a whole number from `1` through `86400` to override it. A timeout or Ctrl+C cancels the run, terminates the entire child process tree, prints a diagnostic, and exits `1`.

Solutions (`.sln` or `.slnx`) are preferred when exactly one exists in the working directory, then a single `.csproj`. Use `--project` when discovery would be ambiguous. Providing any `--coverage` report skips tests and neither creates nor deletes a test-results directory. Missing, malformed, or unsupported reports fail operationally. OpenCover and Cobertura/Coverlet XML are supported; multiple reports merge the union of distinct eligible sequence points. Repeated points are counted once and are visited if any report records a visit.

Coverage paths are parsed independently of the host OS. Repeatable `--coverage-path-map <report-root> <local-root>` translates foreign POSIX, drive, or UNC roots; `--coverage-path-case auto|sensitive|insensitive` controls only foreign comparisons (`auto` is drive/UNC insensitive and POSIX sensitive). Mappings are validated before test execution, use longest complete-component prefix precedence, and can resolve only files already present in the selected inventory. See [coverage source paths and diagnostics](docs/coverage-paths.md).
The local map root must be inside one of the selected source inputs; map the producer's source root to the corresponding selected local directory.

## Analysis policy

Ruleset details and the measured mapping matrix are documented in
[complexity rules](docs/complexity-rules.md) and [callable support](docs/callable-support.md).

- Roslyn syntax trees are used; source is never analyzed with regular expressions.
- Legacy invocations use filesystem discovery and default parser settings and remain explicitly labelled `syntaxOnly`. They do not claim the effective project's `Compile` items, links, target framework, symbols, language version, imports, generated inputs, or compiler binding.
- `ordinary-methods-v1` includes only concrete ordinary methods and preserves every historical exclusion and score.
- `callables-v1` inventories methods, authored constructors and initializer regions, destructors, accessors, operators/conversions, local functions, lambdas, anonymous methods, and top-level code. Bodyless declarations remain explicit `not-applicable` entries.
- Every decision belongs to exactly one innermost authored leaf. A separately labelled `crap.nestedFamilyRisk` guard unions owned points and decisions for nested families; it is not a method or an inventory total.
- Cyclomatic complexity starts at 1 and increments for conditionals, loops, catches, switch cases/arms, `?:`, `&&`, `||`, and `??`.
- Coverage uses method sequence points, not a type-level or file-level percentage. Hidden points are excluded; a method with zero eligible points is `N/A`. Point identity includes its line and available column/end/offset coordinates so duplicate reports do not inflate the denominator.
- Report methods are matched conservatively by exact normalized source path, namespace/nested/generic type identity, method name, compatible arity, and a unique source span. There is no nearest-name fallback. Ambiguous overloads and conflicting module/assembly candidates for the same source method remain `N/A`.
- Ordinary authored members use semantic identity and exclusive source ownership. Generated names are never guessed. A bounded, byte-only PE/portable-PDB mapper validates MVID, PDB association, source checksum, exact kickoff MethodDef, and public state-machine links. Reports without sufficient token/context evidence remain explicit `coverage.unsupportedGeneratedMapping`.
- Local functions, lambdas, anonymous methods, top-level lowering, initializer lowering, ambiguous anonymous fingerprints, and unsupported generated report shapes stay visible and fail closed. `callables-v1` rejects `--allow-missing-coverage`; unsupported or otherwise unresolved modern scope cannot be broadly waived.
- `--callable-exemptions` uses exact ruleset/context/TFM/callable/body/reason matching. A CLI-selected file is always `local-unreviewed`; it cannot turn incomplete scope into reviewed enforcement or suppress a known threshold violation.
- Default recursive discovery excludes `.git`, `bin`, `obj`, `packages`, `TestResults`, `node_modules`, `test`, `tests`, and `*.Test(s)` directories, plus common generated names (`*.g.cs`, `*.generated.cs`, `*.designer.cs`, assembly info, and global usings). Explicit eligible `.cs` files are accepted even if they are under an excluded directory; explicit directories retain exclusions. An existing explicit input that is not an eligible `.cs` source file fails instead of becoming an empty success.
- C# syntax errors are operational failures; malformed syntax trees are never scored.
- `--changed` consumes NUL-delimited Git porcelain v1, including spaces, newlines, untracked files, renames/copies, and deletions. Deleted files are ignored and paths escaping the repository root are rejected. This is the legacy whole-file worktree mode; it does not detect already committed changes and is not an alias for the future captured scope selectors.

The repository also contains tested captured Git scope and changed-method selection adapters for future command orchestration. They distinguish exact worktree, index, and commit bytes and preserve resolved ref/object identities. Public project-aware `check` plus `--scope`, `--base`, `--head`, `--source-state`, and `--granularity` orchestration is not implemented by issue #7. See [captured change scopes](docs/change-scopes.md).

Output is ordered by numeric CRAP score descending, followed by unknown (`N/A`) entries. Ties are deterministic by path and line.

## Machine-readable results

`--format human|json` controls console rendering (`human` is the default). JSON stdout contains exactly one UTF-8 document followed by a newline; progress and child diagnostics are routed to stderr. `--output <path>` always writes the JSON document through a unique sibling temporary file and atomic replacement, independent of console format. Destinations that alias source, project, or coverage inputs are rejected before any write.

The v1 document separates deterministic `evaluation` evidence from volatile `run` metadata. Generated coverage uses a content-addressed logical identity in `evaluation`; its temporary physical location remains in `run`. The document reports the executing assembly's informational version, preserves raw finite score precision, stable finding IDs/order, explicit null coverage, conservative coverage reason codes, check status, policy decision, and actual 0/1/2 exit semantics. See [the result contract](docs/results.md) and [JSON Schema](docs/result-schema-v1.json). Incompatible major schema versions must be rejected; additive fields within major version 1 may be ignored by readers.

## Exit codes

- `0`: analysis succeeded with complete admitted evidence and no score exceeded the threshold
- `1`: usage, discovery, test, timeout/cancellation, required unknown/unsupported/ambiguous coverage, invalid exemptions, or other operational failure
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
