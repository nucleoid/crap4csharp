using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Crap4CSharp.Core;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class EvaluationEngineTests
{
    [Fact]
    public void PureEvaluationIsCultureAndInputOrderIndependent()
    {
        var sourceA = CapturedSource.Create("src/A.cs", Encoding.UTF8.GetBytes("class A { int M() => 1; }"));
        var sourceB = CapturedSource.Create("src/B.cs", Encoding.UTF8.GetBytes("class B { int N() => 2; }"));
        var report = ImmutableArray.Create(Encoding.UTF8.GetBytes(Cobertura()).ToArray());
        var first = Evaluate([sourceA, sourceB], [new CapturedCoverage("coverage.xml", report, "cobertura", "line")], "en-US");
        var second = Evaluate([sourceB, sourceA], [new CapturedCoverage("coverage.xml", report, "cobertura", "line")], "tr-TR");

        Assert.Equal(first, second);
        Assert.Equal("captured", first.Provenance.Status);
        Assert.Equal(2, first.Metrics.Count);
    }

    [Fact]
    public void ByteCoverageReaderRejectsDtdWithoutFilesystemAccess()
    {
        var bytes = Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY y SYSTEM 'file:///etc/passwd'>]><coverage />");
        Assert.Throws<InvalidDataException>(() => CoverageReader.Read(bytes, "captured/report.xml"));
    }

    [Fact]
    public void CapturedCoberturaChoosesTheOnlyMappedSourceRoot()
    {
        var source = CapturedSource.Create("src/C.cs",
            Encoding.UTF8.GetBytes("class C { int M() => 1; }"));
        var report = ImmutableArray.Create(Encoding.UTF8.GetBytes("""
            <coverage><sources><source>C:\wrong</source><source>C:\agent\repo</source></sources>
            <packages><package name="App"><classes><class name="C" filename="src/C.cs">
            <methods><method name="M" signature="()"><lines><line number="1" hits="1" /></lines></method></methods>
            </class></classes></package></packages></coverage>
            """));
        var input = new EvaluationInput("ctx", [source],
            [new CapturedCoverage("coverage.xml", report, "cobertura", "line")],
            CSharpParseOptions.Default, new PolicyOptions(8, false),
            new ProvenanceResult(ProvenanceStatus.Captured, "captureConsistency", false, true, []))
        {
            PathPolicy = new CapturedPathPolicy(false,
                [new ManifestReportRootMapping(@"C:\agent\repo", "")])
        };

        var result = EvaluationEngine.Evaluate(input, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, Assert.Single(result.Metrics).Coverage);
    }

    private static EvaluationSnapshot Evaluate(IReadOnlyList<CapturedSource> sources,
        IReadOnlyList<CapturedCoverage> coverage, string culture)
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            return EvaluationEngine.Evaluate(new EvaluationInput("ctx", sources, coverage,
                CSharpParseOptions.Default, new PolicyOptions(8, false),
                new ProvenanceResult(ProvenanceStatus.Captured, "captureConsistency", false, true, [])),
                CancellationToken.None);
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    private static string Cobertura() => """
        <coverage><packages><package name="App"><classes>
          <class name="A" filename="src/A.cs"><methods><method name="M" signature="()"><lines><line number="1" hits="1" /></lines></method></methods></class>
          <class name="B" filename="src/B.cs"><methods><method name="N" signature="()"><lines><line number="1" hits="1" /></lines></method></methods></class>
        </classes></package></packages></coverage>
        """;
}
