# Process execution boundary (issue #10 implementation in progress)

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
the job on completion or failure. Windows runtime acceptance remains pending; Linux tests
and compilation are not Windows proof. Cleanup has a separate finite five-second bound.

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

Consumer stdin support and fresh `check` orchestration are still unfinished. The Linux supervisor's stdin is a private start/status ownership handshake,
not a consumer input channel. This document does not claim that fresh check is implemented.
