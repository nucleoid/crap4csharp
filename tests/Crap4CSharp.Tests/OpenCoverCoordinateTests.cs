using System.Collections.Immutable;
using System.Text;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class OpenCoverCoordinateTests
{
    [Theory]
    [InlineData("", "line")]
    [InlineData(" sc=\"1\" ec=\"2\" el=\"9\"", "sequence-point")]
    [InlineData(" sc=\"1\"", "sequence-point")]
    [InlineData(" offset=\"0\"", "sequence-point")]
    public void InspectorDerivesCoordinatesFromActualPointShapeNotFormatLabel(string attributes, string expected)
    {
        var xml = "<CoverageSession><Modules><Module><ModuleName>Library</ModuleName><Classes><Class><Methods><Method>" +
            "<SequencePoints><SequencePoint sl=\"9\" vc=\"1\"" + attributes + "/></SequencePoints>" +
            "</Method></Methods></Class></Classes></Module></Modules></CoverageSession>";
        var inspected = ArtifactEvidenceInspector.InspectCoverage(ImmutableArray.Create(Encoding.UTF8.GetBytes(xml)));
        Assert.Equal("opencover", inspected.Format); Assert.Equal(expected, inspected.CoordinateKind);
    }
}
