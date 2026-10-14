# Consumer quality gate

After editing C# code, run the manifest-pinned tool against the reviewed base:

```powershell
dotnet tool run crap4csharp -- check --project Consumer.slnx --policy crap4csharp.policy.json --base $ReviewedBase --scope all --format json
```

Use the base supplied by the task or repository policy; do not silently replace it
with the current HEAD. Inspect the exit code and the current invocation's JSON.
Exit 0 passes, 2 means a policy threshold violation, and 1 means usage or an
operational failure, including unknown required coverage or failing tests.

Improve the code or tests in response to findings. Do not suppress unknown
coverage, relax the reviewed policy, or rewrite baselines/exemptions merely to
pass. Baseline and exemption changes require explicit review. CRAP describes
maintainability and test-coverage risk; passing it does not prove correctness.

The tool owns its result directories. Do not delete unrelated results or install
collectors into analyzed projects during a gate invocation. Restore the committed
tool manifest during setup; do not add the tool to production PackageReferences.
