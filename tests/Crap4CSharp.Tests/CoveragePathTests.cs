using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CoveragePathTests : IDisposable
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "crap4csharp-coverage-paths", Guid.NewGuid().ToString("N"));

    public CoveragePathTests() => Directory.CreateDirectory(temporary);
    public void Dispose() => Directory.Delete(temporary, recursive: true);

    [Fact]
    public void WindowsDriveMappingIsLexicalAndForeignCaseDoesNotChangeLocalIdentity()
    {
        var upper = Write("work/src/C.cs", "class C { int M() => 1; }");
        var resolver = Resolver([upper], Path.Combine(temporary, "work"),
            [new CoveragePathMapping(@"C:\agent\repo", Path.Combine(temporary, "work"))]);

        var resolved = resolver.Resolve(@"c:\AGENT\REPO\src\C.cs", [], Path.Combine(temporary, "results", "coverage.xml"));
        var localCaseMiss = resolver.Resolve(@"C:\agent\repo\src\c.cs", [], Path.Combine(temporary, "results", "coverage.xml"));

        Assert.Equal(CoveragePathResolutionStatus.Resolved, resolved.Status);
        Assert.Equal(upper, resolved.LocalPath);
        Assert.Equal(CoveragePathResolutionStatus.Missing, localCaseMiss.Status);
        Assert.Equal(CoverageReasonCodes.MissingPath, localCaseMiss.Diagnostic!.Code);
    }

    [Fact]
    public void UncMappingWorksWithoutHostPathParsing()
    {
        var source = Write("work/src/C.cs", "class C { }");
        var resolver = Resolver([source], Path.Combine(temporary, "work"),
            [new CoveragePathMapping(@"\\server\share\repo", Path.Combine(temporary, "work"))]);

        var result = resolver.Resolve(@"\\SERVER\SHARE\REPO\src\C.cs", [], Path.Combine(temporary, "coverage.xml"));

        Assert.Equal(CoveragePathResolutionStatus.Resolved, result.Status);
        Assert.Equal(source, result.LocalPath);
    }

    [Fact]
    public void LongestComponentMappingWinsIndependentOfRuleOrder()
    {
        var expected = Write("specific/C.cs", "class C { }");
        var general = Path.Combine(temporary, "general");
        Directory.CreateDirectory(general);
        var mappings = new[]
        {
            new CoveragePathMapping("/agent/src", Path.Combine(temporary, "specific")),
            new CoveragePathMapping("/agent", general)
        };

        foreach (var ordered in new[] { mappings, mappings.Reverse().ToArray() })
        {
            var resolver = Resolver([expected], temporary, ordered);
            var result = resolver.Resolve("/agent/src/C.cs", [], Path.Combine(temporary, "coverage.xml"));
            Assert.Equal(expected, result.LocalPath);
        }
    }

    [Fact]
    public void MappingPrefixRequiresAComponentBoundaryAndNeverFallsBack()
    {
        var tempting = Write("selected/src2/C.cs", "class C { }");
        var target = Path.Combine(temporary, "selected");
        var resolver = Resolver([tempting], target,
            [new CoveragePathMapping("/build/src", target)]);

        var result = resolver.Resolve("/build/src2/C.cs", [], Path.Combine(temporary, "coverage.xml"));

        Assert.Equal(CoveragePathResolutionStatus.Missing, result.Status);
        Assert.Null(result.LocalPath);
    }

    [Fact]
    public void ConflictingEquivalentMapsFailDuringConstruction()
    {
        var first = Path.Combine(temporary, "first");
        var second = Path.Combine(temporary, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        var inventory = new CoverageSourceInventory(PathIdentityPolicy.Sensitive, [],
            [new CoverageSourceRoot(first, first), new CoverageSourceRoot(second, second)]);

        var exception = Assert.Throws<CoveragePathException>(() => new CoveragePathResolver(
            PathIdentityPolicy.Sensitive, inventory,
            [new("/agent/repo", first), new("/agent/./repo", second)], CoveragePathCase.Auto));

        Assert.Equal(CoverageReasonCodes.PathMappingConflict, exception.Code);
    }

    [Fact]
    public void MappingIdentityChangesWithWorkspaceLogicalDestination()
    {
        var workspace = Path.Combine(temporary, "workspace");
        var source = Write("workspace/src/C.cs", "class C { }");
        var library = Write("workspace/lib/C.cs", "class C { }");
        var inventory = SourcePathCapture.Capture([source, library],
            [Path.GetDirectoryName(source)!, Path.GetDirectoryName(library)!], [], [], workingDirectory: workspace);

        var sourceMapping = new CoveragePathResolver(PathIdentityPolicy.Sensitive, inventory,
            [new CoveragePathMapping(@"C:\agent\repo", Path.GetDirectoryName(source)!)], CoveragePathCase.Auto);
        var libraryMapping = new CoveragePathResolver(PathIdentityPolicy.Sensitive, inventory,
            [new CoveragePathMapping(@"C:\agent\repo", Path.GetDirectoryName(library)!)], CoveragePathCase.Auto);

        Assert.NotEqual(Assert.Single(sourceMapping.MappingIdentities).Id,
            Assert.Single(libraryMapping.MappingIdentities).Id);
    }

    [Theory]
    [InlineData("C:src\\C.cs")]
    [InlineData(@"\\?\C:\src\C.cs")]
    [InlineData(@"\rooted\without-drive.cs")]
    [InlineData("file:///src/C.cs")]
    public void UnsupportedForeignFormsAreInvalid(string reportedPath)
    {
        var resolver = Resolver([], temporary, []);

        var result = resolver.Resolve(reportedPath, [], Path.Combine(temporary, "coverage.xml"));

        Assert.Equal(CoveragePathResolutionStatus.Invalid, result.Status);
        Assert.Equal(CoverageReasonCodes.InvalidPath, result.Diagnostic!.Code);
    }

    [Fact]
    public void CoberturaRelativeSourceRootUsesReportDirectoryNotCurrentDirectory()
    {
        var source = Write("fixture/src/C.cs", "class C { int M() => 1; }");
        var report = Write("fixture/results/coverage.xml", """
            <coverage><sources><source>../src</source></sources><packages><package name="Fixture"><classes>
            <class name="C" filename="C.cs"><methods><method name="M" signature="()"><lines><line number="1" hits="1" /></lines></method></methods></class>
            </classes></package></packages></coverage>
            """);
        var resolver = Resolver([source], Path.Combine(temporary, "fixture", "src"), []);

        var result = CoverageReader.ReadDetailed(report, resolver);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == CoverageDiagnosticSeverity.Error);
        Assert.Equal(source, Assert.Single(result.Methods).File);
    }

    [Fact]
    public void CoberturaMultipleRootsRemainAmbiguousBeforeTypeMatching()
    {
        var first = Write("one/C.cs", "class C { int M() => 1; }");
        var second = Write("two/C.cs", "class Other { int M() => 1; }");
        var report = Write("coverage.xml", $"""
            <coverage><sources><source>{Escape(Path.GetDirectoryName(first)!)}</source><source>{Escape(Path.GetDirectoryName(second)!)}</source></sources>
            <packages><package><classes><class name="C" filename="C.cs"><methods><method name="M" signature="()"><lines><line number="1" hits="1" /></lines></method></methods></class></classes></package></packages></coverage>
            """);
        var inventory = new CoverageSourceInventory(PathIdentityPolicy.Sensitive,
            [new(first, "one/C.cs", Path.GetDirectoryName(first)!, first, Path.GetDirectoryName(first)!, null),
             new(second, "two/C.cs", Path.GetDirectoryName(second)!, second, Path.GetDirectoryName(second)!, null)],
            [new(Path.GetDirectoryName(first)!, Path.GetDirectoryName(first)!), new(Path.GetDirectoryName(second)!, Path.GetDirectoryName(second)!)]);
        var resolver = new CoveragePathResolver(PathIdentityPolicy.Sensitive, inventory, [], CoveragePathCase.Auto);

        var result = CoverageReader.ReadDetailed(report, resolver);

        Assert.DoesNotContain(result.Methods, method => method.File is not null);
        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == CoverageReasonCodes.AmbiguousPath);
        Assert.Equal(2, diagnostic.CandidatePaths.Count);
    }

    [Fact]
    public void RelativeFilenameMayNormalizeInsideRootButCannotEscapeIt()
    {
        var source = Write("src/C.cs", "class C { }");
        var resolver = Resolver([source], Path.Combine(temporary, "src"), []);
        var report = Path.Combine(temporary, "coverage.xml");

        var inside = resolver.Resolve("nested/../C.cs", [Path.Combine(temporary, "src")], report);
        var outside = resolver.Resolve("../../C.cs", [Path.Combine(temporary, "src")], report);

        Assert.Equal(CoveragePathResolutionStatus.Resolved, inside.Status);
        Assert.Equal(CoveragePathResolutionStatus.OutsideRoot, outside.Status);
        Assert.Equal(CoverageReasonCodes.PathOutsideRoot, outside.Diagnostic!.Code);
    }

    [Fact]
    public void ExplicitForeignCaseDoesNotChangeNativeLocalIdentity()
    {
        var upper = Write("src/C.cs", "class Upper { }");
        var lower = Write("src/c.cs", "class Lower { }");
        var inventory = new CoverageSourceInventory(PathIdentityPolicy.Sensitive,
            [Entry(upper, temporary), Entry(lower, temporary)], [new(temporary, temporary)]);
        var resolver = new CoveragePathResolver(PathIdentityPolicy.Sensitive, inventory, [], CoveragePathCase.Insensitive);

        var upperResult = resolver.Resolve(upper, [], Path.Combine(temporary, "coverage.xml"));
        var lowerResult = resolver.Resolve(lower, [], Path.Combine(temporary, "coverage.xml"));

        Assert.Equal(upper, upperResult.LocalPath);
        Assert.Equal(lower, lowerResult.LocalPath);
    }

    [Fact]
    public void InsensitiveForeignCaseDoesNotRescueNativeCaseMiss()
    {
        var upper = Write("src/C.cs", "class Upper { }");
        var inventory = new CoverageSourceInventory(PathIdentityPolicy.Sensitive,
            [Entry(upper, temporary)], [new(temporary, temporary)]);
        var resolver = new CoveragePathResolver(PathIdentityPolicy.Sensitive, inventory, [], CoveragePathCase.Insensitive);

        var result = resolver.Resolve(Path.Combine(temporary, "src", "c.cs"), [], Path.Combine(temporary, "coverage.xml"));

        Assert.Equal(CoveragePathResolutionStatus.Missing, result.Status);
    }

    [Fact]
    public void PosixBackslashIsALiteralFilenameCharacter()
    {
        if (OperatingSystem.IsWindows()) return;
        var source = Write("src/back\\slash.cs", "class C { }");
        var resolver = Resolver([source], temporary, []);

        var result = resolver.Resolve(source, [], Path.Combine(temporary, "coverage.xml"));

        Assert.Equal(CoveragePathResolutionStatus.Resolved, result.Status);
        Assert.Equal("src/back\\slash.cs", Assert.Single(result.CandidatePaths));
    }

    private CoveragePathResolver Resolver(IEnumerable<string> files, string root, IEnumerable<CoveragePathMapping> mappings)
    {
        Directory.CreateDirectory(root);
        var entries = files.Select(file => Entry(file, root)).ToArray();
        var inventory = new CoverageSourceInventory(PathIdentityPolicy.Sensitive, entries, [new(root, root)]);
        return new CoveragePathResolver(PathIdentityPolicy.Sensitive, inventory, mappings, CoveragePathCase.Auto);
    }

    private static CoverageSourceEntry Entry(string file, string root) =>
        new(file, Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'), root, file, root, null);

    private string Write(string relative, string contents)
    {
        var path = Path.Combine(temporary, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return Path.GetFullPath(path);
    }

    private static string Escape(string value) => System.Security.SecurityElement.Escape(value)!;
}
