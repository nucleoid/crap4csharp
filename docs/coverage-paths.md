# Coverage source paths

Crap4CSharp resolves every report filename against the selected source inventory. A report path never authorizes a source read, and matching never searches by basename, suffix, type, or nearest method.

## Command line

`--coverage-path-map <report-root> <local-root>` is repeatable and always consumes two operands. `report-root` must be a lexically absolute POSIX, Windows drive, or UNC path. `local-root` is resolved against the invocation directory, must be an existing directory inside a declared source root, and cannot escape that root through a symlink.

`--coverage-path-case auto|sensitive|insensitive` controls only report-root and report-path comparisons. `auto` compares Windows drive/UNC paths without case and POSIX paths with case. The selected local inventory keeps the host's local identity policy; a foreign insensitive comparison that exposes two case-distinct local files is ambiguous.

```bash
crap4csharp --coverage-path-map 'C:\agent\repo' "$PWD" --coverage windows.opencover.xml src
crap4csharp --coverage-path-map /agent/repo "$PWD" --coverage container.cobertura.xml src
```

Rules match complete path components. The longest matching report root wins, independent of option order. `/agent/src` does not match `/agent/src2`. Once a rule wins, a missing translated file remains missing; shorter rules and native/basename fallbacks are not attempted. Equivalent roots with different destinations are `coverage.pathMappingConflict` before tests or other child processes run.

## Report grammar and roots

- Accepted absolute dialects: `/posix/path`, `C:\drive\path`, and `\\server\share\path`.
- Rejected: drive-relative (`C:src`), Windows device namespaces, root-relative Windows paths without a drive/share, URIs, NUL, and malformed roots.
- Relative filenames use their candidate source-root dialect. `.` and internal `..` are normalized only while they remain inside that source root.
- Cobertura preserves every nonempty direct `<sources>/<source>` value. Relative source roots resolve against the report directory. With no sources, the report directory is the implicit root. Absolute filenames bypass source roots.
- OpenCover retains each eligible point's file ID. A missing ID is missing-path evidence. A callable spanning multiple documents is conservatively unsupported rather than assigning all points to its first file.

## Identity and containment

Local paths are normalized without lowercasing. Windows local identity is ordinal-ignore-case; other platforms use ordinal identity. Output is sorted ordinally after identity deduplication. A collision under the selected local policy is diagnosed instead of silently dropping a source.

Fresh filesystem capture resolves existing parent links for source roots, mapping targets, and selected files. Recursive discovery cannot authorize a symlink escape. An explicitly selected external/link file receives an external-root identity. Pure resolution consumes those captured facts and performs no filesystem reads.

## Diagnostics

Normalized results use stable codes and logical identities. Raw report/local paths remain volatile run evidence.

| Code | Meaning |
|---|---|
| `coverage.missingPath` | Missing filename/file ID, unmapped foreign path, or no selected inventory entry |
| `coverage.invalidPath` | Unsupported or malformed path grammar |
| `coverage.pathOutsideRoot` | Lexical or captured physical containment escape |
| `coverage.pathMappingConflict` | Contradictory mapping rules |
| `coverage.ambiguousPath` | More than one selected source identity remains |
| `coverage.typeMismatch` | Resolved path has no exact compatible reported type |
| `coverage.signatureMismatch` | Type matches but method name or known arity does not |
| `coverage.spanMismatch` | Eligible points do not fit a compatible source span |
| `coverage.ambiguousMethod` | More than one span-compatible callable remains |
| `coverage.conflictingModule` | Incompatible module identities would be unioned |
| `coverage.noEligiblePoints` | Exact callable observation has no usable points |
| `coverage.unsupportedGeneratedMapping` | Generated callable lacks authoritative source mapping |
| `coverage.unsupportedMultiDocumentMapping` | One reported callable spans multiple documents |
| `coverage.contextMismatch` / `coverage.contextUnbound` | Reserved additive hooks for project-context binding |
| `coverage.noMatchingMethod` | No compatible source observation can be attributed |
| `coverage.unavailable` | Coverage capture itself was unavailable |

Unknown coverage remains `null`, never zero. `--allow-missing-coverage` permits method-level unknowns while known scores still gate; invalid mappings, malformed paths, and unsafe containment remain operational failures.
