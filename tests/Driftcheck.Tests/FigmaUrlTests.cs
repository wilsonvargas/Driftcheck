using Driftcheck.Mcp.Figma;

namespace Driftcheck.Tests;

public class FigmaUrlTests
{
    [Theory]
    [InlineData("https://www.figma.com/design/AbC123xyz/Paylo?node-id=12-34&t=xyz", "AbC123xyz", "12:34")]
    [InlineData("https://www.figma.com/file/AbC123xyz/Paylo?node-id=12%3A34", "AbC123xyz", "12:34")]
    [InlineData("https://www.figma.com/proto/AbC123xyz/Paylo?page-id=0-1&node-id=5-6", "AbC123xyz", "5:6")]
    public void ParsesFileKeyAndNodeId(string url, string fileKey, string nodeId)
    {
        var (key, node) = FigmaClient.ParseUrl(url);

        Assert.Equal(fileKey, key);
        Assert.Equal(nodeId, node);
    }

    [Fact]
    public void RequiresNodeId() =>
        Assert.Throws<ArgumentException>(() => FigmaClient.ParseUrl("https://www.figma.com/design/AbC123xyz/Paylo"));
}
