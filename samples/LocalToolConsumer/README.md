# Independent local-tool consumer

Copy this directory outside the tool source checkout. It references only its own
production project and explicitly configured test packages.

The committed tool-manifest pin `0.1.0` is a template, not a published-package
claim. Candidate acceptance replaces it with the exact immutable candidate
version and places that nupkg in the sibling `feed` directory. The package-source
mapping confines `Crap4CSharp.Tool` to that local feed; ordinary test dependencies
use NuGet.org.

From the copied directory, restore the exact tool and consumer dependencies:

```powershell
dotnet tool restore --configfile nuget.config
dotnet restore Consumer.slnx --disable-parallel
```

Commit the policy and source in the consumer repository, select a reviewed base
commit/ref, then run the command in [AGENTS.md](AGENTS.md). For a non-Git local
experiment, omit `--base` and retain `--scope all`; its trust is explicitly
`local-unreviewed`. Production task gates should use the repository's reviewed
base and policy.

This sample requires the .NET 10 SDK/runtime. The tool does not add or update the
test collector automatically. Candidate installation and check acceptance must
be verified on Windows and Linux before this template is used as release proof.
