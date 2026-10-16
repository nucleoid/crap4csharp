using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class OwnedCoverageAttachmentPathTests
{
    [Theory]
    [InlineData("NUCLEOID/coverage.opencover.xml")]
    [InlineData("NUCLEOID\\coverage.opencover.xml")]
    public void ExactVstestPlatformRelativeAttachmentNamesAreAccepted(string href)
        => Assert.Equal("coverage.opencover.xml", OwnedCoverageAttachment.AttachmentFileName(href, "NUCLEOID"));

    [Theory]
    [InlineData("/NUCLEOID/coverage.opencover.xml")]
    [InlineData("\\NUCLEOID\\coverage.opencover.xml")]
    [InlineData("NUCLEOID/../coverage.opencover.xml")]
    [InlineData("NUCLEOID\\..\\coverage.opencover.xml")]
    [InlineData("NUCLEOID\\sub/coverage.opencover.xml")]
    [InlineData("NUCLEOID/%63overage.opencover.xml")]
    [InlineData("OTHER/coverage.opencover.xml")]
    [InlineData("NUCLEOID/other.xml")]
    [InlineData("file:///NUCLEOID/coverage.opencover.xml")]
    [InlineData("C:\\NUCLEOID\\coverage.opencover.xml")]
    public void UnsafeAmbiguousOrUnrelatedAttachmentsRemainRefused(string href)
    {
        var error = Assert.Throws<ProjectContextException>(() => OwnedCoverageAttachment.AttachmentFileName(href, "NUCLEOID"));
        Assert.Equal("coverage.attachmentUnproven", error.Reason);
    }
}
