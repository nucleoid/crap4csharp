# Pinned local-tool onboarding

Use an explicitly accepted candidate version and its SHA-256 receipt. The independent
[sample consumer](../samples/LocalToolConsumer/README.md) is a setup template;
its `0.1.0` pin does not imply that a release has been published.

Require the .NET 10 SDK and runtime. The supported SDK floor is 10.0.103 with
`latestPatch` selection. The tool and Core remain `net10.0`. Additional consumer
target frameworks require their own runtime and separately verified collector
support; they do not change the tool's runtime requirement.

Copy the sample outside the tool source checkout. Obtain the exact tested nupkg,
candidate version and SHA-256 receipt. Place that package in the sibling `feed`
directory and replace only the manifest's tool-version value in the disposable
copy. Keep the exact version in the consumer's committed tool manifest after
review. The sample's NuGet source mapping restricts the tool ID to the local feed.

One-time setup, from the consumer directory:

```powershell
dotnet tool restore --configfile nuget.config
dotnet restore Consumer.slnx --disable-parallel
```

The sample test project explicitly references Microsoft.NET.Test.Sdk 18.0.1,
xunit.v3 3.2.2, its VSTest adapter 3.1.5, and coverlet.collector 6.0.4. Collector
configuration belongs in reviewed test-project setup. The tool does not install
packages or collectors into projects it analyzes. Production projects reference
neither the tool nor Core.

Commit the policy and source, then select the task's approved base commit/ref.
Routine task gate:

```powershell
dotnet tool run crap4csharp -- check --project Consumer.slnx --policy crap4csharp.policy.json --base $ReviewedBase --scope all --format json
```

`--base` is the reviewed Git base selector used by the change-scope/trust
contract; see [change scopes](change-scopes.md). Do not substitute current HEAD
to obtain an empty comparison. This sample uses strict/all policy, so required
tests still run and all eligible authored callables are scored. A non-Git local
experiment may omit `--base` with `--scope all`; its trust is explicitly
`local-unreviewed`.

Capture the exit code and exactly one current JSON document together. Exit 0
passes, 2 is a metric/policy threshold violation, and 1 is usage or an operational
failure, including failed tests or unknown required coverage. A stale result file
does not establish the outcome of a newer failed invocation. Use unique result
destinations and [check-mode result semantics](check.md) when retaining evidence.

Fresh check performs its declared preparation/build/test schedule. Prior compiler
reuse still requires fresh tests. Complete current reuse independently revalidates
saved evidence and current inputs. Captured analyze/replay is a distinct mode;
it does not execute tests or establish current success. Use only the documented
selectors in [check mode](check.md) and [provenance](provenance.md).

Unknown or ambiguous coverage remains unknown; do not assign zero or suppress
it merely to pass. CRAP measures maintainability and test-coverage risk, not
correctness. Deterministic evaluation of identical captured observations is
separate from potentially flaky tests and changing runtime/tool environments.
