# Process execution boundary

The compatibility `ProcessRunner.RunAsync` APIs return complete decoded text or refuse
output exceeding 4 Mi UTF-16 characters per stream. They never substitute a tail for a
complete Git/JSON/XML response. Both pipes are drained concurrently; timeout and
cancellation remain primary terminal outcomes after termination.

`RunLoggedAsync` writes complete, byte-preserving stdout/stderr into exclusively created
`stdout.log` and `stderr.log` files in the caller's invocation-owned existing directory.
Existing log files are refused, and reparse-point ancestors are refused. The caller must
not use consumer input directories as owned logs. Failed setup can leave an empty newly
created log; failed/cancelled execution retains partial diagnostics, not a completed claim.
Success reports separate byte totals, exact final byte tails, and truncation flags.
The default tail is 64 KiB per stream, configurable from zero through 1 MiB. Retention is
one fixed tail plus one 8 KiB read buffer per stream, independent of output length; complete
logs consume disk space and disk exhaustion is an operational sink failure. This is not a
measured whole-process memory bound. Log bytes are not a parsed protocol or UTF-8 promise.

Both platforms share the same byte pump. A pump/sink failure signals the owner immediately,
without waiting for the other inherited pipe to close. Linux establishes a retained session
leader before launching the consumer and terminates its verified process group even on
success. Deliberate session escape is unsupported. Windows creates the consumer suspended,
assigns a non-breakaway kill-on-close job before resuming, and terminates/positively observes
the job on completion or failure. Required Windows and Linux CI exercise their respective
process ownership and cancellation controls. Cleanup has a separate finite five-second bound.
Linux requires executable `setsid` and `sh` on the effective child `PATH`, including
per-invocation environment overrides. Both utilities are resolved before launch rather
than assumed at fixed distribution paths. Missing utilities fail with a typed launch
diagnostic before consumer execution; there is no unowned-process fallback. Utility
selection and later file replacement are not malicious-executable attestation.

`RunProtocolAsync` captures complete stdout/stderr bytes without UTF-8 transcoding.
Each stream has an independent configurable limit, default 4 MiB and maximum 16 MiB;
zero accepts only an empty stream. Exceeding either limit drains without additional
retention, then throws `InvalidDataException` at EOF with stream, limit and observed-byte
metadata. It never returns a tail as a complete response. The operation deadline still
bounds an ongoing over-limit producer, and timeout/cancellation takes precedence.
Each pump retains at most its bounded MemoryStream capacity (at most max(256 bytes, twice the limit)),
one 8 KiB buffer and, on success, the complete result array. This is an algorithmic bound,
not a measured whole-process memory benchmark. Protocol responses are distinct from
complete disk logs; callers needing large diagnostic output use `RunLoggedAsync`.

Finite binary protocol stdin is optional and bounded at 16 MiB. The adapter stages an
invocation-owned private input snapshot under the operation deadline, before launching
consumer code. Linux redirects ONLY the consumer's stdin to that file; supervisor stdin
remains the private ownership handshake. Windows explicitly inherits the owned read-only
input file handle along with the two output handles before resuming the assigned consumer.
Both output pumps are active before consumer execution. A consumer may fill both output
pipes before reading input without blocking any parent input writer: there is no pipe
writer task to orphan. Exact bytes and EOF are preserved, including empty input. Environment
overrides use the same owned process adapter. Staging errors prevent consumer launch and
remove the owned snapshot; cancellation/timeout includes staging and execution.

This is a finite, seekable file-backed stdin protocol, NOT interactive or pipe-type-equivalent
stdin. Callers needing terminal/pipe-specific behavior or input over 16 MiB are unsupported;
there is no secret-bearing persistent input artifact. The caller must keep its supplied
memory unchanged until the call completes. The snapshot is deleted after owned cleanup.
Windows CI exercises inherited handles and runtime cleanup directly. Fresh `check` uses
these adapters for owned restore, build and test execution; its compiler, coverage and
publication proofs remain separate requirements.

Git scope commands now use the same owned byte-protocol adapter, with complete stdout and
stderr limited independently to 16 MiB. They preserve existing no-optional-locks and diff
safety switches/environment, exact NUL-delimited stdout and blob bytes, empty-tree stdin,
nonzero handling and strict UTF-8 decoding at text-consuming callsites. Oversized replies
fail `scope.gitProtocolLimit`; they are never silently parsed as a tail or partial scope.
This intentionally refuses huge individual blobs/diffs/index listings instead of consuming
unbounded memory. Worktree file reads and the combined captured scope inventory are separate
from the per-process protocol bound; this is not a whole-scope memory limit.

Internal selected-SDK graph discovery now preserves dependency-first project/global-property
contexts, including ProjectReference configuration/custom property mappings. Cross-target
outer nodes are structural: their graph-selected inner contexts are expanded, without a
nearest-framework/minimal-build claim. All declared dependency inner contexts may therefore
be scheduled conservatively; each identical mapped context appears once. Graph discovery
executes no restore/build/Workspace targets. It remains a private orchestration seam, not
public fresh-check completion or actual compiler evidence. Mapped globals and physical
paths must stay in private local transport, never exported as raw diagnostic evidence.

Git scope preserves raw C# bytes, including line-ending-only diagnostics. For filtered textual configuration and response inputs, CRLF and LF separators alone do not widen the selected graph; independently captured compiler inputs still bind exact bytes. Binary/UTF-16 inputs, arbitrary clean/smudge transformations and substantive configuration edits retain conservative raw-byte handling. Hidden build-relevant skip-worktree/assume-unchanged entries are refused.
