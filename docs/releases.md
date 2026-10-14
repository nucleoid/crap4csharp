# Candidate preparation and release gates

No package publication is authorized by the handoff. This document prepares a
reviewable local candidate. A feed choice, stable version and publication require
separate explicit owner authorization after acceptance.

The manual `Candidate preparation (publication disabled)` workflow accepts an
explicit new prerelease version. It prepares one package from clean committed
source, records its commit/tree/version/SHA-256, and checks those same package
bytes on Windows and Linux through the installed-tool and manifest-consumer
matrix in both Debug and Release suites. The test harness accepts the paired
`CRAP4CSHARP_CANDIDATE_PACKAGE` and `CRAP4CSHARP_CANDIDATE_VERSION` inputs for this
purpose; ordinary test runs still create their own isolated unique candidate.
Preparation and successful per-OS acceptance receipts expire after seven days.
Each configuration uses 16 deterministic method partitions. Theory rows stay
together, and each runner executes its partition serially. At most eight hosted
suite jobs run concurrently; each has a bounded six-hour job limit. Individual
test deadlines, including the original 180-second checks, remain unchanged.

Partition receipts bind the observed process exit, actual terminal cases and TRX
hash to the source tree, SDK, complete runtime inventory and workflow attempt.
The aggregator requires every discovered case exactly once and every required
process-cleanup control to pass. Only the ten existing Windows cases requiring
Linux facilities or file-symlink privileges may be skipped. Unknown skips fail.
Exported receipts contain case digests and numeric counts, without raw test
names, arguments, diagnostics, attachments or consumer paths. Raw discovery,
filters, TRX and logs remain private to the runner.

A final comparison requires complete Debug and Release unions on Windows and
Linux from the same workflow attempt, bound to the prepared package SHA-256,
version, commit and tree, and passing real collector gates. The CI checks
`test (windows-latest)` and `test (ubuntu-latest)` require the complete Release
union and collector gates; individual shard successes cannot satisfy them.
The workflow is not yet validated by a hosted run. Its publication job is
disabled and also refuses execution until separately approved feed, credentials
and artifact verification are wired. It contains no package push or tag trigger.

Keep the framework-dependent tool and Core on `net10.0`. Build with the supported
.NET SDK floor (10.0.103/latestPatch); executing the tool requires the .NET 10
runtime. Package version, JSON schema version and callable ruleset are distinct
identities.

Choose a new immutable prerelease version for each candidate. Pass that same
`Version` to restore, build, tests and pack; do not pack an old assembly under a
new version. Use owned output/feed/cache locations. The candidate sequence is:

```powershell
dotnet restore Crap4CSharp.slnx --disable-parallel -p:Version=$CandidateVersion
dotnet build Crap4CSharp.slnx -c Release --no-restore -m:1 -nr:false -p:Version=$CandidateVersion
dotnet test Crap4CSharp.slnx -c Release --no-build -m:1 -nr:false -p:Version=$CandidateVersion
dotnet pack src/Crap4CSharp.Tool/Crap4CSharp.Tool.csproj -c Release --no-build -o $OwnedFeed -p:Version=$CandidateVersion
```

These commands do not replace the full acceptance matrix. Before release,
require exact committed-head Debug and Release suites, original unchanged public
deadline cases, deterministic fixture gates, real Coverlet E2E, Windows/Linux
process cleanup, and installed-executable 0/1/2 semantics. Prove fresh check,
prior compiler/fresh test execution, current complete reuse, captured replay,
baseline/exemption trust and unknown/stale evidence refusal. Exercise actual
net9/net10 consumer contexts separately, and run the independent manifest
consumer outside the tool checkout on both operating systems.

Inspect the exact nupkg with `scripts/assert-package-metadata.ps1`: exact ID and
version, net10-only tool assets, tool/Core binaries, README, original LICENSE and
ATTRIBUTION, and MIT license expression. Record its SHA-256, source commit/tree,
SDK/runtime inventory, executing tool version and acceptance receipts. Require
the configured-default independent whole-scope review of that same head. A source
change invalidates earlier acceptance/review claims for the final candidate.

Use normal protected merges for #10, then #11, then #12. Demonstrate Delivery
Forge with the pinned installed package and its real pass, threshold violation,
failed-test and operational-error receipts before closing the handoff/epic.
Neither a package file's existence nor selected passing unit tests proves release
readiness.

Any later authorized publication must send the exact accepted nupkg, not rebuild
after approval. Use an approved feed and environment-protected least-privilege
publishing credentials or supported trusted publishing. Never request tokens in
chat, expose secrets in logs, publish from a pull-request context, or use
`skip-duplicate` to disguise same-version replacement. Refuse unknown feed,
missing authorization, changed hash or stale acceptance. There is no automatic
publication or tag-trigger release in the current preparation.

## Rerunning candidate validation

Candidate workflow reruns reuse the original `candidate` artifact from the same
workflow run. They do not repack it or overwrite it. If that artifact is missing
or expired, start a new workflow run with a new candidate version. Even a failed
first preparation requires a new run if it never uploaded its candidate.

Build, partition and platform artifacts have attempt-specific names. A partial
rerun selects the newest available receipt for each partition and platform from
that same workflow run, then checks the complete source, SDK, runtime and package
identity. A newer failed or invalid receipt cannot be replaced by an older pass.
Rebuilding a configuration changes its runtime identity; every accepted partition
must still bind to the selected shared build. Rerun its complete partition set
when rebuilding rather than combining receipts from different runtime bytes.

These rules permit mixed-attempt receipts without permitting mixed candidates,
source revisions or runtime inventories. Publication remains disabled.

## Updating the acceptance SDK

Release acceptance uses the exact SDK version declared in `global.json`, shared
by the preparation and receipt scripts. `latestPatch` permits ordinary developer
selection to roll forward; it does not authorize accepting a package validated
with a different SDK. Hosted acceptance installs the declared version in its
isolated SDK directory and records the actual selected version.

To update the acceptance pin, change `global.json` in a reviewed source change,
update synthetic identity fixtures, and rerun complete Debug/Release and
installed-package acceptance on both operating systems with the same candidate
bytes. Do not edit a receipt to claim the new SDK. Prepare a new candidate version
and preserve the older acceptance evidence.

The native SDK identity provider separately supports its verified SDK layout.
It falls back to the independently executed CLI for other selected versions.
Extend that native support only after verifying resolver, missing-SDK, host/path,
environment and cancellation behavior on both operating systems. An acceptance
pin update alone does not extend the native provider's verified layout.
