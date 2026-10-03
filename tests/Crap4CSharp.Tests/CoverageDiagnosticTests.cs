using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CoverageDiagnosticTests : IDisposable
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "crap4csharp-coverage-diagnostics", Guid.NewGuid().ToString("N"));

    public CoverageDiagnosticTests() => Directory.CreateDirectory(temporary);
    public void Dispose() => Directory.Delete(temporary, recursive: true);

    [Fact]
    public void MatcherReportsDeepestObservedReasonAndAllSupportingReasons()
    {
        var file = Write("Code.cs", """
            class C {
              int M(int value) {
                return value;
              }
            }
            """);
        var source = new SourceAnalyzer().AnalyzeFiles([file]);
        var methods = new[]
        {
            new CoverageMethod(file, "Wrong.C", "M", 1, [new(2, 1)], "Fixture"),
            new CoverageMethod(file, "C", "M", 2, [new(2, 1)], "Fixture"),
            new CoverageMethod(file, "C", "M", 1, [new(99, 1)], "Fixture")
        };

        var result = CoverageMatcher.ApplyDetailedResult(source, [methods]);
        var match = Assert.Single(result.Matches);

        Assert.Equal(CoverageReasonCodes.SpanMismatch, match.CoverageReason);
        Assert.Contains(CoverageReasonCodes.TypeMismatch, match.ReasonCodes);
        Assert.Contains(CoverageReasonCodes.SignatureMismatch, match.ReasonCodes);
        Assert.Contains(CoverageReasonCodes.SpanMismatch, match.ReasonCodes);
        Assert.All(result.Diagnostics, diagnostic => Assert.False(string.IsNullOrWhiteSpace(diagnostic.Id)));
    }

    [Fact]
    public void SpecialAndGeneratedReportMembersDoNotBlameOrdinaryMethods()
    {
        var file = Write("Members.cs", "class C { int Value { get; set; } int M() => 1; int Uncovered() => 2; }");
        var source = new SourceAnalyzer().AnalyzeFiles([file]).Select(method => method with
        {
            LogicalPath = "Members.cs"
        }).ToArray();
        var report = new[]
        {
            new CoverageMethod(file, "C", ".ctor", 0, [new(1, 1)], "Fixture"),
            new CoverageMethod(file, "C", "get_Value", 0, [new(1, 1)], "Fixture"),
            new CoverageMethod(file, "C", "<M>g__Local|0_0", 0, [new(1, 1)], "Fixture"),
            new CoverageMethod(file, "C/<>c", "<M>b__0_0", 0, [new(1, 1)], "Fixture")
        };

        var result = CoverageMatcher.ApplyDetailedResult(source, [report]);

        Assert.All(result.Matches, match =>
        {
            Assert.Equal(CoverageReasonCodes.NoMatchingMethod, match.CoverageReason);
            Assert.Empty(match.DiagnosticIds);
            Assert.DoesNotContain(CoverageReasonCodes.SignatureMismatch, match.ReasonCodes);
            Assert.DoesNotContain(CoverageReasonCodes.TypeMismatch, match.ReasonCodes);
        });
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Scope == CoverageDiagnosticScope.Method);
    }

    [Fact]
    public void GeneratedMoveNextIsEvidenceWithoutInventedSourceAssociation()
    {
        var file = Write("Async.cs", "class C { async System.Threading.Tasks.Task<int> Work() => await System.Threading.Tasks.Task.FromResult(1); }");
        var source = new SourceAnalyzer().AnalyzeFiles([file]);
        var report = new[] { new CoverageMethod(file, "C+<Work>d__0", "MoveNext", 0, [new(1, 1)], "Fixture") };

        var result = CoverageMatcher.ApplyDetailedResult(source, [report]);

        Assert.Equal(CoverageReasonCodes.NoMatchingMethod, Assert.Single(result.Matches).CoverageReason);
        var generated = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == CoverageReasonCodes.UnsupportedGeneratedMapping);
        Assert.Null(generated.MethodId);
    }

    [Fact]
    public void OpenCoverDoesNotAssignMultipleDocumentsToFirstFile()
    {
        var first = Write("First.cs", "class First { int M() => 1; }");
        var second = Write("Second.cs", "class Second { int M() => 1; }");
        var report = Write("multi.xml", $"""
            <CoverageSession><Modules><Module><ModuleName>Fixture</ModuleName><Files>
            <File uid="1" fullPath="{Escape(first)}" /><File uid="2" fullPath="{Escape(second)}" />
            </Files><Classes><Class><FullName>First</FullName><Methods><Method><Name>First.M()</Name>
            <SequencePoints><SequencePoint vc="1" sl="1" fileid="1" /><SequencePoint vc="0" sl="1" fileid="2" /></SequencePoints>
            </Method></Methods></Class></Classes></Module></Modules></CoverageSession>
            """);
        var resolver = Resolver([first, second]);

        var result = CoverageReader.ReadDetailed(report, resolver);

        Assert.Empty(result.Methods);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == CoverageReasonCodes.UnsupportedMultiDocumentMapping);
    }

    [Fact]
    public void MissingOpenCoverFileIdProducesStableMissingPathEvidence()
    {
        var report = Write("missing-id.xml", """
            <CoverageSession><Modules><Module><ModuleName>Fixture</ModuleName><Files />
            <Classes><Class><FullName>C</FullName><Methods><Method><Name>C.M()</Name>
            <SequencePoints><SequencePoint vc="1" sl="1" fileid="404" /></SequencePoints>
            </Method></Methods></Class></Classes></Module></Modules></CoverageSession>
            """);

        var result = CoverageReader.ReadDetailed(report, Resolver([]));

        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == CoverageReasonCodes.MissingPath);
        Assert.Equal(CoverageDiagnosticStage.Path, diagnostic.Stage);
        Assert.Equal(CoverageDiagnosticScope.Observation, diagnostic.Scope);
        Assert.NotNull(diagnostic.ObservationId);
    }

    [Fact]
    public void DuplicateOpenCoverFileIdWithDifferentPathsIsInvalid()
    {
        var first = Write("First.cs", "class First { int M() => 1; }");
        var second = Write("Second.cs", "class Second { int M() => 1; }");
        var report = Write("duplicate-id.xml", $"""
            <CoverageSession><Modules><Module><Files>
            <File uid="1" fullPath="{Escape(first)}" /><File uid="1" fullPath="{Escape(second)}" />
            </Files><Classes><Class><FullName>First</FullName><Methods><Method><Name>First.M()</Name>
            <SequencePoints><SequencePoint vc="1" sl="1" fileid="1" /></SequencePoints>
            </Method></Methods></Class></Classes></Module></Modules></CoverageSession>
            """);

        var result = CoverageReader.ReadDetailed(report, Resolver([first, second]));

        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == CoverageReasonCodes.InvalidPath);
        Assert.Equal(CoverageDiagnosticScope.Report, diagnostic.Scope);
        Assert.Equal(CoverageDiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void MultiDocumentMethodWithUnlabelledPointIsUnsupported()
    {
        var first = Write("First.cs", "class First { int M() => 1; }");
        var second = Write("Second.cs", "class Second { int M() => 1; }");
        var report = Write("unlabelled-point.xml", $"""
            <CoverageSession><Modules><Module><Files>
            <File uid="1" fullPath="{Escape(first)}" /><File uid="2" fullPath="{Escape(second)}" />
            </Files><Classes><Class><FullName>First</FullName><Methods><Method><Name>First.M()</Name>
            <FileRef uid="1" /><FileRef uid="2" />
            <SequencePoints><SequencePoint vc="1" sl="1" fileid="1" /><SequencePoint vc="0" sl="1" /></SequencePoints>
            </Method></Methods></Class></Classes></Module></Modules></CoverageSession>
            """);

        var result = CoverageReader.ReadDetailed(report, Resolver([first, second]));

        Assert.Empty(result.Methods);
        Assert.Contains(result.Diagnostics, item => item.Code == CoverageReasonCodes.UnsupportedMultiDocumentMapping);
    }

    [Fact]
    public void MethodDiagnosticsRemainScopedToTheirLogicalPath()
    {
        var first = Write("one/Code.cs", "class C { int M() => 1; }");
        var second = Write("two/Code.cs", "class C { int M() => 1; }");
        var source = new SourceAnalyzer().AnalyzeFiles([first, second]).Select(method => method with
        {
            LogicalPath = Path.GetRelativePath(temporary, method.File).Replace(Path.DirectorySeparatorChar, '/')
        }).ToArray();
        var report = new[] { new CoverageMethod(first, "C", "M", 1, [new(1, 1)], "Fixture") };

        var result = CoverageMatcher.ApplyDetailedResult(source, [report]);

        var firstMatch = Assert.Single(result.Matches, match => match.Source.File == first);
        var secondMatch = Assert.Single(result.Matches, match => match.Source.File == second);
        Assert.NotEmpty(firstMatch.DiagnosticIds);
        Assert.Empty(secondMatch.DiagnosticIds);
        Assert.All(result.Diagnostics.Where(item => item.Scope == CoverageDiagnosticScope.Method),
            item => Assert.Equal("one/Code.cs", item.Path));
    }

    [Fact]
    public void CompatibleReportsStillUnionDistinctPoints()
    {
        var file = Write("Union.cs", "class C {\n int M() {\n  var x = 1;\n  return x;\n }\n}");
        var source = new SourceAnalyzer().AnalyzeFiles([file]);
        var first = new[] { new CoverageMethod(file, "C", "M", 0, [new(3, 1, 2), new(4, 0, 2)], "Fixture") };
        var second = new[] { new CoverageMethod(file, "C", "M", 0, [new(4, 1, 2)], "Fixture") };

        var result = CoverageMatcher.ApplyDetailedResult(source, [first, second]);

        Assert.Equal(1, Assert.Single(result.Matches).Metric.Coverage);
    }

    private CoveragePathResolver Resolver(IEnumerable<string> files)
    {
        var entries = files.Select(file => new CoverageSourceEntry(file,
            Path.GetRelativePath(temporary, file).Replace(Path.DirectorySeparatorChar, '/'), temporary, file, temporary, null)).ToArray();
        return new CoveragePathResolver(PathIdentityPolicy.Sensitive,
            new CoverageSourceInventory(PathIdentityPolicy.Sensitive, entries, [new(temporary, temporary)]), [], CoveragePathCase.Auto);
    }

    private string Write(string relative, string contents)
    {
        var path = Path.Combine(temporary, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return Path.GetFullPath(path);
    }

    private static string Escape(string value) => System.Security.SecurityElement.Escape(value)!;
}
