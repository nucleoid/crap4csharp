using System.Reflection;
using System.Xml.Linq;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class TestExecutionAdapterTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ActualMappedTestsRunOnceWithoutAnyImplicitBuildOrRestore(bool passes, bool opposingNoBuild)
    {
        using var fixture = TestDirectory.Create("crap4csharp-actual-once-tests");
        var dependency = fixture.Write("Dep/Dep.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <Target Name="Count" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' != 'true'">
            <WriteLinesToFile File="$(MSBuildProjectDirectory)/build-count.txt" Lines="build" Overwrite="false" /></Target></Project>
            """);
        fixture.Write("Dep/Code.cs", "public class Dependency { public static int Value => 42; }");
        var project = fixture.Write("Tests/Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable>
            <DefineConstants Condition="'$(Flavor)' == 'left'">$(DefineConstants);LEFT</DefineConstants></PropertyGroup>
            <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" PrivateAssets="all" />
            <PackageReference Include="xunit.v3" Version="3.2.2" PrivateAssets="all" />
            <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" PrivateAssets="all" />
            <PackageReference Include="coverlet.collector" Version="6.0.4" PrivateAssets="all" />
            <ProjectReference Include="../Dep/Dep.csproj" AdditionalProperties="Configuration=Debug" /></ItemGroup>
            <Target Name="Count" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' != 'true'">
            <WriteLinesToFile File="$(MSBuildProjectDirectory)/build-count.txt" Lines="build" Overwrite="false" /></Target>
            <Target Name="ValidateMappedTestLaunch" BeforeTargets="VSTest">
            <Error Condition="'$(Flavor)' != 'left' Or '$(Configuration)' != 'Release' Or '$(TargetFramework)' != 'net10.0'" Text="Mapped test selectors lost" />
            <WriteLinesToFile File="$(MSBuildProjectDirectory)/launch-count.txt" Lines="test" Overwrite="false" /></Target>
            </Project>
            """);
        var source = fixture.Write("Tests/Tests.cs", """
            using Xunit;
            public class RuntimeAssertions {
            [Fact] public void Once() {
            #if !LEFT
            #error Mapped Flavor global property was lost
            #endif
            System.IO.File.AppendAllText(System.IO.Path.Combine(System.AppContext.BaseDirectory,"test-count.txt"), "test\n");
            Assert.Equal(EXPECTED, Dependency.Value);
            } }
            """.Replace("EXPECTED", passes ? "42" : "43", StringComparison.Ordinal));
        var request = new ProjectContextLoadRequest(project, "Release", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2));
        // LoadGraph has no extra root-global argument: use an entry root with the mapped test dependency.
        var root = fixture.Write("Root/Root.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <ItemGroup><ProjectReference Include="../Tests/Tests.csproj" AdditionalProperties="Configuration=Release;Flavor=leftOPPOSING" /></ItemGroup></Project>
            """.Replace("OPPOSING", opposingNoBuild ? ";vstestnobuild=false" : "", StringComparison.Ordinal));
        fixture.Write("Root/Code.cs", "public class Root {}");
        request = request with { Target = root, Configuration = "Debug" };
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, TestContext.Current.CancellationToken)).ExitCode);
        var discovery = await ProjectContextLoader.DiscoverTestRunnersAsync(request, TestContext.Current.CancellationToken);
        Assert.True(discovery.Success, discovery.FailureReason + string.Join(" | ", discovery.Diagnostics));
        var loaded = await ProjectContextLoader.LoadGraphAsync(request, TestContext.Current.CancellationToken);
        Assert.True(loaded.Success, loaded.FailureReason + string.Join(" | ", loaded.Diagnostics));
        var plan = CheckExecutionPlan.Create(discovery, loaded, TestContext.Current.CancellationToken);
        var bound = await CompilerGraphExecution.RunBoundAsync(request, plan.PreparedGraph, async (node, token) =>
        {
            var owned = System.IO.Path.Combine(fixture.Path, "build", node.Id);
            Directory.CreateDirectory(owned);
            try { return await CompilerEvidence.BuildAsync(node, owned, TimeSpan.FromMinutes(2), token); }
            catch (ProjectContextException failure) { throw new InvalidOperationException(failure.Message + "\n" + File.ReadAllText(System.IO.Path.Combine(owned, "stdout.log")) + "\n" + File.ReadAllText(System.IO.Path.Combine(owned, "stderr.log")), failure); }
        }, TestContext.Current.CancellationToken);
        Assert.Equal(3, bound.Observations.Count);
        Assert.Single(File.ReadAllLines(System.IO.Path.Combine(fixture.Path, "Tests/build-count.txt")));
        Assert.Single(File.ReadAllLines(System.IO.Path.Combine(fixture.Path, "Dep/build-count.txt")));
        var authored = new[] { project, dependency, root, source }.ToDictionary(p => p,
            p => (Bytes: File.ReadAllBytes(p), Time: File.GetLastWriteTimeUtc(p)));
        var outputs = bound.FinalGraph.Contexts.SelectMany(c => new[] { c.EvaluatedTargetPath!, System.IO.Path.ChangeExtension(c.EvaluatedTargetPath!, ".pdb") })
            .ToDictionary(p => p, p => File.ReadAllBytes(p));
        var assetsPath = System.IO.Path.Combine(fixture.Path, "Tests/obj/project.assets.json");
        var restores = (Bytes: File.ReadAllBytes(assetsPath), Time: File.GetLastWriteTimeUtc(assetsPath));
        var method = typeof(ProjectContextLoader).Assembly.GetType("Crap4CSharp.Core.TestExecutionAdapter")?.GetMethod("RunAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var ownedResults = System.IO.Path.Combine(fixture.Path, "test-run");
        Directory.CreateDirectory(ownedResults);
        var operation = (Task)method.Invoke(null, [plan, ownedResults, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken])!;
        await operation;
        var results = Assert.IsAssignableFrom<System.Collections.IEnumerable>(operation.GetType().GetProperty("Result")!.GetValue(operation));
        var observation = Assert.Single(results.Cast<object>());
        var process = (LoggedProcessResult)observation.GetType().GetProperty("Process")!.GetValue(observation)!;
        Assert.Equal(passes ? 0 : 1, process.ExitCode);
        var resultsDirectory = (string)observation.GetType().GetProperty("ResultsDirectory")!.GetValue(observation)!;
        var trx = XDocument.Load(Assert.Single(Directory.GetFiles(resultsDirectory, "*.trx")));
        var counters = Assert.Single(trx.Descendants(), e => e.Name.LocalName == "Counters");
        Assert.Equal("1", counters.Attribute("total")!.Value);
        Assert.Equal(passes ? "1" : "0", counters.Attribute("passed")!.Value);
        Assert.Equal(passes ? "0" : "1", counters.Attribute("failed")!.Value);
        Assert.Equal(passes ? "Passed" : "Failed", Assert.Single(trx.Descendants(), e => e.Name.LocalName == "UnitTestResult").Attribute("outcome")!.Value);
        if (!opposingNoBuild)
        {
            // Real RunBoundAsync outputs followed by actual VSTest, not fabricated Csc.
            var typedObservation = Assert.IsType<TestInvocationObservation>(observation);
            var testBinding = Assert.Single(bound.FinalGraph.GraphContexts, b => b.IsTestProject);
            var compiler = Assert.Single(bound.Observations, o => o.ProjectPath == project);
            var binder = typeof(ProjectContextLoader).Assembly.GetType("Crap4CSharp.Core.TestResultBinding")?
                .GetMethod("ValidateSuccessfulTrx", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(binder);
            void Validate() => binder.Invoke(null, [typedObservation, testBinding.Context, compiler, TestContext.Current.CancellationToken]);
            if (!passes)
            {
                var error = Assert.Throws<TargetInvocationException>(Validate);
                Assert.Equal("test.resultsUnproven", Assert.IsType<ProjectContextException>(error.InnerException).Reason);
            }
            else
            {
                var verified = Assert.IsType<BoundTestResult>(binder.Invoke(null,
                    [typedObservation, testBinding.Context, compiler, TestContext.Current.CancellationToken]));
                Assert.Equal(1, verified.Passed);
                Assert.Equal(compiler.Assembly!.Sha256, verified.AssemblySha256);
                Assert.Equal(compiler.Pdb!.Sha256, verified.PdbSha256);
                foreach (var modulePath in new[] { testBinding.Context.EvaluatedTargetPath!, Path.ChangeExtension(testBinding.Context.EvaluatedTargetPath!, ".pdb") })
                {
                    var saved = File.ReadAllBytes(modulePath); var time = File.GetLastWriteTimeUtc(modulePath);
                    var changed = saved.ToArray(); changed[^1] ^= 1;
                    File.WriteAllBytes(modulePath, changed); File.SetLastWriteTimeUtc(modulePath, time);
                    try
                    {
                        var error = Assert.Throws<TargetInvocationException>(Validate);
                        Assert.Equal("test.resultsUnproven", Assert.IsType<ProjectContextException>(error.InnerException).Reason);
                    }
                    finally { File.WriteAllBytes(modulePath, saved); File.SetLastWriteTimeUtc(modulePath, time); }
                }
                Validate();
                var path = Path.Combine(resultsDirectory, "tests.trx");
                var original = File.ReadAllBytes(path);
                XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
                foreach (var defect in new[] { "individual", "duplicate-result", "entry", "definition", "execution", "storage", "codebase", "namespace", "zero", "warning", "counter", "summary" })
                {
                    var changed = XDocument.Load(path);
                    var result = changed.Descendants(ns + "UnitTestResult").Single();
                    var definition = changed.Descendants(ns + "UnitTest").Single();
                    var counts = changed.Descendants(ns + "Counters").Single();
                    switch (defect)
                    {
                        case "individual": result.SetAttributeValue("outcome", "Failed"); break;
                        case "duplicate-result": result.AddAfterSelf(new XElement(result)); break;
                        case "entry": changed.Descendants(ns + "TestEntry").Single().Remove(); break;
                        case "definition": definition.Remove(); break;
                        case "execution": definition.Element(ns + "Execution")!.SetAttributeValue("id", Guid.NewGuid()); break;
                        case "storage": definition.SetAttributeValue("storage", Path.Combine(fixture.Path, "unowned", "Tests.dll").ToLowerInvariant()); break;
                        case "codebase": definition.Element(ns + "TestMethod")!.SetAttributeValue("codeBase", Path.Combine(fixture.Path, "unowned", "Tests.dll")); break;
                        case "namespace": result.Name = XName.Get("UnitTestResult", "urn:foreign"); break;
                        case "zero": counts.SetAttributeValue("total", "0"); break;
                        case "warning": counts.SetAttributeValue("warning", "1"); break;
                        case "counter": counts.SetAttributeValue("executed", "2"); break;
                        case "summary": changed.Descendants(ns + "ResultSummary").Single().SetAttributeValue("outcome", "Failed"); break;
                    }
                    changed.Save(path);
                    try
                    {
                        var error = Assert.Throws<TargetInvocationException>(Validate);
                        Assert.Equal("test.resultsUnproven", Assert.IsType<ProjectContextException>(error.InnerException).Reason);
                    }
                    finally { File.WriteAllBytes(path, original); }
                }
                // Owned file-reader controls use this actual TRX and independently built module.
                using (var oversized = new FileStream(path, FileMode.Create, FileAccess.Write))
                    oversized.SetLength(TestResultBinding.MaximumTrxBytes + 1);
                try
                {
                    var error = Assert.Throws<TargetInvocationException>(Validate);
                    Assert.Equal("test.resultsUnproven", Assert.IsType<ProjectContextException>(error.InnerException).Reason);
                }
                finally { File.WriteAllBytes(path, original); }
                var duplicate = Path.Combine(resultsDirectory, "duplicate.trx");
                File.WriteAllBytes(duplicate, original);
                try
                {
                    var error = Assert.Throws<TargetInvocationException>(Validate);
                    Assert.Equal("test.resultsUnproven", Assert.IsType<ProjectContextException>(error.InnerException).Reason);
                }
                finally { File.Delete(duplicate); }
                if (!OperatingSystem.IsWindows())
                {
                    File.WriteAllBytes(duplicate, original); File.Delete(path);
                    File.CreateSymbolicLink(path, duplicate);
                    try
                    {
                        var error = Assert.Throws<TargetInvocationException>(Validate);
                        Assert.Equal("test.resultsUnproven", Assert.IsType<ProjectContextException>(error.InnerException).Reason);
                    }
                    finally { File.Delete(path); File.Delete(duplicate); File.WriteAllBytes(path, original); }
                }
                Validate();
                // Real collector-owned report gate. This validates attachment ownership/format,
                // NOT yet complete graph module/PDB association or through-test preservation.
                var coverageBinder = typeof(ProjectContextLoader).Assembly.GetType("Crap4CSharp.Core.OwnedCoverageAttachment")?
                    .GetMethod("ReadOpenCover", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(coverageBinder);
                object ReadCoverage() => coverageBinder.Invoke(null,
                    [typedObservation, testBinding.Context, compiler, TestContext.Current.CancellationToken])!;
                var attachment = ReadCoverage();
                var coveragePath = (string)attachment.GetType().GetProperty("Path")!.GetValue(attachment)!;
                var declaredDeployment = trx.Descendants(ns + "Deployment").Single().Attribute("runDeploymentRoot")!.Value;
                var declaredHref = trx.Descendants(ns + "Collector").Single().Descendants(ns + "A").Single().Attribute("href")!.Value;
                Assert.Equal(Path.GetFullPath(Path.Combine(resultsDirectory, declaredDeployment, "In", declaredHref)), coveragePath);
                var coverageOriginal = File.ReadAllBytes(coveragePath);
                var coverageTime = File.GetLastWriteTimeUtc(coveragePath);
                Assert.Equal("CoverageSession", XDocument.Load(coveragePath).Root!.Name.LocalName);
                foreach (var defect in new[] { "collector-missing", "collector-uri", "collector-duplicate", "collector-foreign-namespace", "deployment-missing", "deployment-traversal", "deployment-rooted", "href-traversal", "href-rooted", "href-uri", "href-backslash", "href-encoded", "href-agent", "attachment-missing", "attachment-duplicate", "attachment-hidden" })
                {
                    var changed = XDocument.Load(path);
                    var collector = changed.Descendants(ns + "Collector").Single();
                    var deployment = changed.Descendants(ns + "Deployment").Single();
                    var link = collector.Descendants(ns + "A").Single();
                    switch (defect)
                    {
                        case "collector-missing": collector.Remove(); break;
                        case "collector-uri": collector.SetAttributeValue("uri", "datacollector://foreign/1.0"); break;
                        case "collector-duplicate": collector.AddAfterSelf(new XElement(collector)); break;
                        case "collector-foreign-namespace": collector.Name = XName.Get("Collector", "urn:foreign"); break;
                        case "deployment-missing": deployment.Remove(); break;
                        case "deployment-traversal": deployment.SetAttributeValue("runDeploymentRoot", "../outside"); break;
                        case "deployment-rooted": deployment.SetAttributeValue("runDeploymentRoot", fixture.Path); break;
                        case "href-traversal": link.SetAttributeValue("href", "../coverage.opencover.xml"); break;
                        case "href-rooted": link.SetAttributeValue("href", coveragePath); break;
                        case "href-uri": link.SetAttributeValue("href", "file://" + coveragePath); break;
                        case "href-backslash": link.SetAttributeValue("href", "fuego\\coverage.opencover.xml"); break;
                        case "href-encoded": link.SetAttributeValue("href", "%2e%2e/coverage.opencover.xml"); break;
                        case "href-agent": collector.SetAttributeValue("agentName", "other-agent"); break;
                        case "attachment-missing": link.Remove(); break;
                        case "attachment-duplicate": link.Parent!.AddAfterSelf(new XElement(link.Parent)); break;
                        case "attachment-hidden": changed.Root!.Add(new XElement(collector)); break;
                    }
                    changed.Save(path);
                    try
                    {
                        var error = Assert.Throws<TargetInvocationException>(() => ReadCoverage());
                        Assert.Equal("coverage.attachmentUnproven", Assert.IsType<ProjectContextException>(error.InnerException).Reason);
                    }
                    finally { File.WriteAllBytes(path, original); }
                }
                foreach (var report in new[] { "<coverage />", "<CoverageSession>", "<!DOCTYPE CoverageSession [<!ENTITY x 'unsafe'>]><CoverageSession>&x;</CoverageSession>" })
                {
                    File.WriteAllText(coveragePath, report);
                    try
                    {
                        var error = Assert.Throws<TargetInvocationException>(() => ReadCoverage());
                        Assert.Equal("coverage.attachmentUnproven", Assert.IsType<ProjectContextException>(error.InnerException).Reason);
                    }
                    finally { File.WriteAllBytes(coveragePath, coverageOriginal); File.SetLastWriteTimeUtc(coveragePath, coverageTime); }
                }
                Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(coverageOriginal)).ToLowerInvariant(),
                    (string)attachment.GetType().GetProperty("Sha256")!.GetValue(attachment)!);
                Assert.Equal(coverageOriginal,
                    ((System.Collections.Immutable.ImmutableArray<byte>)attachment.GetType().GetProperty("Bytes")!.GetValue(attachment)!).ToArray());
                var guidCopy = Directory.GetDirectories(resultsDirectory).Single(p => Guid.TryParse(Path.GetFileName(p), out _));
                var ignoredCopy = Path.Combine(guidCopy, "coverage.opencover.xml");
                var ignoredBytes = File.ReadAllBytes(ignoredCopy);
                File.WriteAllText(ignoredCopy, "not the declared attachment");
                try { ReadCoverage(); }
                finally { File.WriteAllBytes(ignoredCopy, ignoredBytes); }
                using (var oversized = new FileStream(coveragePath, FileMode.Create, FileAccess.Write))
                    oversized.SetLength(16 * 1024 * 1024 + 1);
                try
                {
                    var error = Assert.Throws<TargetInvocationException>(() => ReadCoverage());
                    Assert.Equal("coverage.attachmentUnproven", Assert.IsType<ProjectContextException>(error.InnerException).Reason);
                }
                finally { File.WriteAllBytes(coveragePath, coverageOriginal); File.SetLastWriteTimeUtc(coveragePath, coverageTime); }
                File.Delete(coveragePath); Directory.CreateDirectory(coveragePath);
                try
                {
                    var error = Assert.Throws<TargetInvocationException>(() => ReadCoverage());
                    Assert.Equal("coverage.attachmentUnproven", Assert.IsType<ProjectContextException>(error.InnerException).Reason);
                }
                finally { Directory.Delete(coveragePath); File.WriteAllBytes(coveragePath, coverageOriginal); File.SetLastWriteTimeUtc(coveragePath, coverageTime); }
                File.Delete(coveragePath);
                try
                {
                    var error = Assert.Throws<TargetInvocationException>(() => ReadCoverage());
                    Assert.Equal("coverage.attachmentUnproven", Assert.IsType<ProjectContextException>(error.InnerException).Reason);
                }
                finally { File.WriteAllBytes(coveragePath, coverageOriginal); File.SetLastWriteTimeUtc(coveragePath, coverageTime); }
                if (!OperatingSystem.IsWindows())
                {
                    var unowned = fixture.Write("unowned-coverage.xml", System.Text.Encoding.UTF8.GetString(coverageOriginal));
                    File.Delete(coveragePath); File.CreateSymbolicLink(coveragePath, unowned);
                    try
                    {
                        var error = Assert.Throws<TargetInvocationException>(() => ReadCoverage());
                        Assert.Equal("coverage.attachmentUnproven", Assert.IsType<ProjectContextException>(error.InnerException).Reason);
                    }
                    finally { File.Delete(coveragePath); File.WriteAllBytes(coveragePath, coverageOriginal); File.SetLastWriteTimeUtc(coveragePath, coverageTime); }
                }
                ReadCoverage();
                // Genuine once-built graph and real collector report, not synthetic compiler evidence.
                TestContext.Current.TestOutputHelper!.WriteLine("ACTUAL_GRAPH_OPENCOVER " + System.Text.Encoding.UTF8.GetString(coverageOriginal));
                var graphCoverageBinder = typeof(ProjectContextLoader).Assembly.GetType("Crap4CSharp.Core.CoverageGraphBinding")?
                    .GetMethod("ValidateInvocation", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(graphCoverageBinder);
                object BindGraphCoverage() => graphCoverageBinder.Invoke(null,
                    [typedObservation, bound, TestContext.Current.CancellationToken])!;
                var graphCoverage = BindGraphCoverage();
                Assert.Contains("Dep", (IReadOnlyList<string>)graphCoverage.GetType().GetProperty("ModuleNames")!.GetValue(graphCoverage)!);
                foreach (var defect in new[] { "foreign-module", "duplicate-module", "empty", "metadata-token", "point", "file", "missing-file", "outside-closure", "namespace", "fileid", "filteredpoint", "token-attribute", "method-name", "columns", "hidden-module", "duplicate-method", "negative-visits", "hidden-method" })
                {
                    var changed = XDocument.Load(coveragePath);
                    var module = changed.Descendants("Module").First(m => m.Element("ModuleName")!.Value == "Dep");
                    switch (defect)
                    {
                        case "foreign-module": module.Element("ModuleName")!.Value = "Foreign"; break;
                        case "duplicate-module": module.AddAfterSelf(new XElement(module)); break;
                        case "empty": changed.Root!.Element("Modules")!.RemoveNodes(); break;
                        case "metadata-token": module.Descendants("Method").First(m => m.Descendants("SequencePoint").Any()).Element("MetadataToken")!.Value = "100663295"; break;
                        case "point": module.Descendants("SequencePoint").First().SetAttributeValue("sl", "999999"); break;
                        case "file": module.Descendants("File").First().SetAttributeValue("fullPath", Path.Combine(fixture.Path, "Foreign.cs")); break;
                        case "missing-file": module.Descendants("File").First().Remove(); break;
                        case "outside-closure": module.Element("ModuleName")!.Value = "Root"; break;
                        case "namespace": module.Name = XName.Get("Module", "urn:foreign"); break;
                        case "fileid": module.Descendants("SequencePoint").First().SetAttributeValue("fileid", "9999"); break;
                        case "filteredpoint": module.Descendants("SequencePoint").First().SetAttributeValue("vc", "invalid"); break;
                        case "token-attribute": module.Descendants("Method").First().SetAttributeValue("metadataToken", "06000001"); break;
                        case "method-name": module.Descendants("Method").First().Element("Name")!.Value = "System.Int32 Dependency::Foreign()"; break;
                        case "columns": module.Descendants("SequencePoint").First().SetAttributeValue("sc", "999"); break;
                        case "hidden-module": changed.Root!.Add(new XElement(module)); break;
                        case "duplicate-method": var methodCopy = module.Descendants("Method").First(); methodCopy.AddAfterSelf(new XElement(methodCopy)); break;
                        case "negative-visits": module.Descendants("SequencePoint").First().SetAttributeValue("vc", "-1"); break;
                        case "hidden-method": changed.Root!.Add(new XElement(module.Descendants("Method").First())); break;
                    }
                    changed.Save(coveragePath);
                    try
                    {
                        var error = Assert.Throws<TargetInvocationException>(() => BindGraphCoverage());
                        Assert.Equal("coverage.graphUnproven", Assert.IsType<ProjectContextException>(error.InnerException).Reason);
                    }
                    finally { File.WriteAllBytes(coveragePath, coverageOriginal); File.SetLastWriteTimeUtc(coveragePath, coverageTime); }
                }
                BindGraphCoverage();
                var dependencyContext = Assert.Single(bound.FinalGraph.Contexts, c => c.AssemblyName == "Dep");
                foreach (var modulePath in new[] { dependencyContext.EvaluatedTargetPath!, Path.ChangeExtension(dependencyContext.EvaluatedTargetPath!, ".pdb") })
                {
                    var saved = File.ReadAllBytes(modulePath); var time = File.GetLastWriteTimeUtc(modulePath);
                    var changed = saved.ToArray(); changed[^1] ^= 1;
                    File.WriteAllBytes(modulePath, changed); File.SetLastWriteTimeUtc(modulePath, time);
                    try
                    {
                        var error = Assert.Throws<TargetInvocationException>(() => BindGraphCoverage());
                        Assert.Equal("coverage.graphUnproven", Assert.IsType<ProjectContextException>(error.InnerException).Reason);
                    }
                    finally { File.WriteAllBytes(modulePath, saved); File.SetLastWriteTimeUtc(modulePath, time); }
                }
                BindGraphCoverage();
                // Retain actual attachment/base/membership diagnostics for the next coverage
                // binder. These observations alone do NOT validate coverage provenance.
                TestContext.Current.TestOutputHelper!.WriteLine("ACTUAL_TRX_ATTACHMENT_SHAPE " +
                    string.Join(" | ", trx.Descendants().Where(e => e.Name.LocalName is "Deployment" or "Collector" or "A").Select(e => e.ToString(SaveOptions.DisableFormatting))));
                TestContext.Current.TestOutputHelper!.WriteLine("ACTUAL_RESULTS_MEMBERSHIP " +
                    string.Join(" | ", Directory.GetFiles(resultsDirectory, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(resultsDirectory, p))));
            }
        }
        Assert.NotEmpty(Directory.GetFiles(resultsDirectory, "coverage.opencover.xml", SearchOption.AllDirectories));
        Assert.NotEmpty(File.ReadAllBytes(process.StandardOutput.Path));
        Assert.True(File.Exists(process.StandardError.Path));
        Assert.Single(File.ReadAllLines(System.IO.Path.Combine(fixture.Path, "Tests/bin/Release/net10.0/test-count.txt")));
        // Check the actual target counter before and after VSTest, not just argv.
        Assert.Single(File.ReadAllLines(System.IO.Path.Combine(fixture.Path, "Tests/build-count.txt")));
        Assert.Single(File.ReadAllLines(System.IO.Path.Combine(fixture.Path, "Tests/launch-count.txt")));
        Assert.Single(File.ReadAllLines(System.IO.Path.Combine(fixture.Path, "Dep/build-count.txt")));
        Assert.Equal(restores.Bytes, File.ReadAllBytes(assetsPath));
        Assert.Equal(restores.Time, File.GetLastWriteTimeUtc(assetsPath));
        foreach (var (path, saved) in authored) { Assert.Equal(saved.Bytes, File.ReadAllBytes(path)); Assert.Equal(saved.Time, File.GetLastWriteTimeUtc(path)); }
        foreach (var (path, bytes) in outputs) Assert.Equal(bytes, File.ReadAllBytes(path));
    }
    [Fact]
    public void ActualTestArgumentsPreserveMappedSelectorsAndEscapeMSBuildDelimiters()
    {
        var properties = new Dictionary<string,string> { ["Flavor"] = "left;other=value,%$@'()*?", ["Platform"] = "AnyCPU", ["vstestnobuild"] = "false" };
        var node = new ProjectBuildGraphNode("test", Path.GetFullPath("Tests.csproj"), "Release", "AnyCPU", "net10.0", properties, []);
        var arguments = TestExecutionAdapter.CreateArguments(node);
        Assert.Equal(new[] { "test", node.ProjectPath, "--no-build", "--no-restore", "-m:1", "-nr:false" }, arguments.Take(6));
        Assert.Contains("-p:Flavor=left%3Bother=value%2C%25%24%40%27%28%29%2A%3F", arguments);
        Assert.Contains("-p:Configuration=Release", arguments);
        Assert.Contains("-p:TargetFramework=net10.0", arguments);
        Assert.Contains("-p:vstestnobuild=false", arguments);
        Assert.Equal("-p:VSTestNoBuild=true", arguments[^2]);
        Assert.Equal("-p:BuildProjectReferences=false", arguments[^1]);
        Assert.True(Assert.IsAssignableFrom<IList<string>>(arguments).IsReadOnly);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad-name")]
    [InlineData("bad;Another")]
    [InlineData("bad=name")]
    public void InvalidPropertyNamesRefuseBeforeTransport(string name)
    {
        var error = Assert.Throws<ProjectContextException>(() => MappedGlobalArguments.Create(new Dictionary<string,string> { [name] = "x" }));
        Assert.Equal("compiler.globalPropertyInvalid", error.Reason);
    }

    [Fact]
    public void DuplicateCaseInsensitivePropertiesAndNulRefuseBeforeTransport()
    {
        Assert.Throws<ProjectContextException>(() => MappedGlobalArguments.Create(new Dictionary<string,string>
            { ["Flavor"] = "left", ["flavor"] = "right" }));
        Assert.Throws<ProjectContextException>(() => MappedGlobalArguments.Create(new Dictionary<string,string>
            { ["Flavor"] = "left\0right" }));
    }

    [Fact]
    public async Task PrecancelledAdapterCannotValidateOrCreateResults()
    {
        using var fixture = TestDirectory.Create("crap4csharp-test-adapter-cancel");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => TestExecutionAdapter.RunAsync(null!, fixture.Path, TimeSpan.FromSeconds(10), cancellation.Token));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Path));
    }

    // Structural prelaunch controls; no fake Csc observation or successful SDK/test claim.
    [Theory]
    [InlineData("missing")]
    [InlineData("nonempty")]
    [InlineData("intermediate")]
    [InlineData("target")]
    [InlineData("input")]
    public async Task UnsafeResultsRootRefusesBeforeAnySdkOrTestLaunch(string defect)
    {
        using var fixture = TestDirectory.Create("crap4csharp-test-adapter-destination");
        var directory = Path.Combine(fixture.Path, defect is "intermediate" ? "obj" : defect is "target" ? "bin" : "results");
        if (defect != "missing") Directory.CreateDirectory(directory);
        if (defect == "nonempty") File.WriteAllText(Path.Combine(directory, "owned-by-user.txt"), "preserve");
        var project = Path.Combine(fixture.Path, "Project.csproj");
        var context = ProjectAnalysisContext.Create(project, "Project", "net10.0", "Release", "AnyCPU",
            Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest, [], Microsoft.CodeAnalysis.SourceCodeKind.Regular,
            [], [], new(false, false), new("sdk", "msbuild", "roslyn")) with
        {
            ResolvedProjectPath = project,
            EvaluatedTargetPath = Path.Combine(fixture.Path, "bin", "Project.dll"),
            EvaluatedIntermediateDirectory = Path.Combine(fixture.Path, "obj"),
            EvaluatedReferenceAssemblyPath = Path.Combine(fixture.Path, "obj", "ref", "Project.dll"),
            PhaseInputs = new[] { new ProjectPhaseInput(project, "project", false),
                new ProjectPhaseInput(defect == "input" ? Path.Combine(directory, "immutable.cs") : Path.Combine(fixture.Path, "Code.cs"), "source", false) }
        };
        var node = new ProjectBuildGraphNode("test", project, "Release", "AnyCPU", "net10.0", new Dictionary<string,string>(), []);
        var runner = new ProjectTestRunnerMetadata("test", project, "Release", "AnyCPU", "net10.0", "VSTest", "18.0.1", "6.0.4");
        var graph = new ProjectContextLoadResult(true, new[] { context }, [])
        { BuildGraph = new[] { node }, GraphContexts = new[] { new ProjectGraphContextBinding("test", project, context, true) }, TestRunners = new[] { runner } };
        var plan = CheckExecutionPlan.Create(graph, graph, TestContext.Current.CancellationToken);
        var before = Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).ToDictionary(p => p,
            p => (Bytes: File.ReadAllBytes(p), Time: File.GetLastWriteTimeUtc(p)));
        var membership = Directory.GetFileSystemEntries(fixture.Path, "*", SearchOption.AllDirectories).Order().ToArray();
        var error = await Assert.ThrowsAsync<ProjectContextException>(() => TestExecutionAdapter.RunAsync(plan, directory,
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal("test.planInvalid", error.Reason);
        Assert.Equal(membership, Directory.GetFileSystemEntries(fixture.Path, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, saved) in before) { Assert.Equal(saved.Bytes, File.ReadAllBytes(path)); Assert.Equal(saved.Time, File.GetLastWriteTimeUtc(path)); }
    }

}
