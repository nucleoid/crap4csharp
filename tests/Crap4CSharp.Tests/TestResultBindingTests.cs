using System.Collections.Immutable;
using System.Text;
using System.Xml.Linq;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

// Synthetic XML protocol controls ONLY. Actual once-build/VSTest/module proof lives
// in TestExecutionAdapterTests and is never fabricated by these parser fixtures.
public sealed class TestResultBindingTests
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void CompleteUniquePassingIdentitiesSupportLowercaseStorage(int count)
    {
        var target = Path.GetFullPath("MixedCase.Tests.dll");
        TestResultBinding.ValidateDocument(Bytes(Document(target, count)), target, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("nested-result")]
    [InlineData("nested-definition")]
    public void HiddenNestedIndividualEvidenceCannotBeIgnored(string defect)
    {
        var target = Path.GetFullPath("MixedCase.Tests.dll");
        var document = Document(target, 1);
        if (defect == "nested-result")
        {
            var result = document.Descendants(Ns + "UnitTestResult").Single();
            var hidden = new XElement(result);
            hidden.SetAttributeValue("outcome", "Failed");
            result.Add(new XElement(Ns + "InnerResults", hidden));
        }
        else
        {
            var definition = document.Descendants(Ns + "UnitTest").Single();
            definition.Add(new XElement(Ns + "NestedDefinitions", new XElement(definition)));
        }
        Assert.Equal("test.resultsUnproven", Assert.Throws<ProjectContextException>(() =>
            TestResultBinding.ValidateDocument(Bytes(document), target, TestContext.Current.CancellationToken)).Reason);
    }

    [Theory]
    [InlineData("duplicate-test-id")]
    [InlineData("duplicate-execution-id")]
    [InlineData("entry-list")]
    [InlineData("missing-list")]
    [InlineData("name")]
    [InlineData("skipped")]
    [InlineData("inconclusive")]
    [InlineData("negative")]
    [InlineData("overflow")]
    [InlineData("run-warning")]
    [InlineData("foreign-root")]
    public void ConflictingIdentityOrIncompleteRunRefuses(string defect)
    {
        var target = Path.GetFullPath("MixedCase.Tests.dll");
        var document = Document(target, 2);
        var results = document.Descendants(Ns + "UnitTestResult").ToArray();
        var definitions = document.Descendants(Ns + "UnitTest").ToArray();
        var entries = document.Descendants(Ns + "TestEntry").ToArray();
        switch (defect)
        {
            case "duplicate-test-id": definitions[1].SetAttributeValue("id", definitions[0].Attribute("id")!.Value); break;
            case "duplicate-execution-id": results[1].SetAttributeValue("executionId", results[0].Attribute("executionId")!.Value); break;
            case "entry-list": entries[0].SetAttributeValue("testListId", Guid.NewGuid()); break;
            case "missing-list": document.Descendants(Ns + "TestList").Single().Remove(); break;
            case "name": results[0].SetAttributeValue("testName", "different"); break;
            case "skipped": results[0].SetAttributeValue("outcome", "NotExecuted"); break;
            case "inconclusive": document.Descendants(Ns + "Counters").Single().SetAttributeValue("inconclusive", "1"); break;
            case "negative": document.Descendants(Ns + "Counters").Single().SetAttributeValue("passed", "-1"); break;
            case "overflow": document.Descendants(Ns + "Counters").Single().SetAttributeValue("total", "2147483648"); break;
            case "run-warning": document.Descendants(Ns + "ResultSummary").Single().Add(new XElement(Ns + "RunInfos", new XElement(Ns + "RunInfo", "warning"))); break;
            case "foreign-root": document.Root!.Name = XName.Get("TestRun", "urn:foreign"); break;
        }
        Assert.Equal("test.resultsUnproven", Assert.Throws<ProjectContextException>(() =>
            TestResultBinding.ValidateDocument(Bytes(document), target, TestContext.Current.CancellationToken)).Reason);
    }

    [Fact]
    public void ParserRejectsEmptyOversizedMalformedAndDtdInputs()
    {
        var target = Path.GetFullPath("Tests.dll");
        foreach (var bytes in new[] { ImmutableArray<byte>.Empty, ImmutableArray.Create(new byte[TestResultBinding.MaximumTrxBytes + 1]),
            ImmutableArray.Create(Encoding.UTF8.GetBytes("<TestRun>")),
            ImmutableArray.Create(Encoding.UTF8.GetBytes("<!DOCTYPE TestRun [<!ENTITY x SYSTEM 'file:///unowned'>]><TestRun>&x;</TestRun>")) })
            Assert.Equal("test.resultsUnproven", Assert.Throws<ProjectContextException>(() =>
                TestResultBinding.ValidateDocument(bytes, target, TestContext.Current.CancellationToken)).Reason);
    }

    [Fact]
    public void PrecancelledBindingCannotParseOrValidateNullInvocation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => TestResultBinding.ValidateSuccessfulTrx(null!, null!, null!, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => TestResultBinding.ValidateDocument(default, "invalid", cancellation.Token));
    }

    private static ImmutableArray<byte> Bytes(XDocument document) => ImmutableArray.Create(Encoding.UTF8.GetBytes(document.ToString()));
    private static XDocument Document(string target, int count)
    {
        var list = Guid.NewGuid();
        var results = new XElement(Ns + "Results"); var definitions = new XElement(Ns + "TestDefinitions"); var entries = new XElement(Ns + "TestEntries");
        for (var i = 0; i < count; i++)
        {
            var test = Guid.NewGuid(); var execution = Guid.NewGuid(); var name = "Test" + i;
            results.Add(new XElement(Ns + "UnitTestResult", new XAttribute("testId", test), new XAttribute("executionId", execution),
                new XAttribute("testName", name), new XAttribute("testListId", list), new XAttribute("outcome", "Passed")));
            definitions.Add(new XElement(Ns + "UnitTest", new XAttribute("id", test), new XAttribute("name", name), new XAttribute("storage", target.ToLowerInvariant()),
                new XElement(Ns + "Execution", new XAttribute("id", execution)), new XElement(Ns + "TestMethod", new XAttribute("codeBase", target))));
            entries.Add(new XElement(Ns + "TestEntry", new XAttribute("testId", test), new XAttribute("executionId", execution), new XAttribute("testListId", list)));
        }
        return new(new XElement(Ns + "TestRun", results, definitions, entries,
            new XElement(Ns + "TestLists", new XElement(Ns + "TestList", new XAttribute("id", list))),
            new XElement(Ns + "ResultSummary", new XAttribute("outcome", "Completed"), new XElement(Ns + "Counters",
                new XAttribute("total", count), new XAttribute("executed", count), new XAttribute("passed", count), new XAttribute("completed", 0)))));
    }
}
