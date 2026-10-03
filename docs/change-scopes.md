# Captured change scopes

The core and tool assemblies expose the capture/selection contracts needed by the future `check` orchestration. The current command-line surface remains the legacy option-only command; `check`, `analyze`, and their `--scope` flags are **not implemented here**.

## Scope semantics

- `worktree` compares current working content with `HEAD`, including staged, unstaged, and non-ignored untracked files. With an unborn `HEAD`, the tracked index and current untracked sources are additions.
- `staged` compares index blobs with `HEAD` and excludes unstaged and untracked content. With an unborn `HEAD`, the index is compared with Git's empty tree.
- `base` resolves the supplied base and head refs to commit objects exactly once and requires one merge base. `sourceState=head` reads only commit blobs. `sourceState=worktree` requires the resolved head to be current `HEAD` and compares the merge base with current working content.
- No upstream, default branch, or remote is inferred. Capture never fetches, checks out, resets, stashes, cleans, or writes a temporary index.

Git path lists use NUL-delimited records. Source bytes come from the selected commit, index, or a captured disk read; SHA-256 content identities and resolved Git object IDs are retained. External diff drivers and text conversion are disabled. Rename and copy detection are pinned at 50 percent. Invalid refs, missing/ambiguous merge bases, conflicted indexes, malformed records, non-UTF-8 or NUL-bearing C# files, and capture races are operational errors rather than empty scopes.

`SourceAnalyzer.AnalyzeCaptured` parses the captured bytes under their logical path and never reopens that path. `ChangedMethodSelector` maps zero-context old/new ranges to ordinary-method identities. Added files select all current methods; fully removed methods are removal records; a pure content-identical rename selects no methods at method granularity; file granularity intentionally selects all current methods. Ambiguous identity mapping widens to the changed file and records `scope.ambiguousCallableMapping`.

## Current limitations and future contracts

Project/import/configuration changes are reported as `scope.contextIncomplete`; issue #5 owns compile-context invalidation and binding. Constructors, accessors, local functions, lambdas, and other unsupported callable kinds remain issue #7's responsibility. Captured scope acquisition does not claim artifact provenance or faithful execution.

V1 execution is current-worktree-only. `ScopeExecutionContract.ValidateBeforeConsumerLoad` rejects staged and `sourceState=head` execution with `scope.executionSnapshotUnsupported` before a consumer loader or test runner is invoked. `RunRequiredChecksBeforeScoringAsync` intentionally runs required checks even when the selected CRAP method count is zero; issue #10 owns the actual `check` command and process orchestration.

Legacy `--changed` remains a separate whole-file porcelain-status mode. It analyzes current files and ignores deletions; it is not a post-commit/base scope and has not been reinterpreted.
