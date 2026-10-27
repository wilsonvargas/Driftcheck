using Driftcheck.Mcp;

namespace Driftcheck.Tests;

public class BrandingTests
{
    [Fact]
    public void PngIconIsEmbedded()
    {
        // VS Code ignora los íconos SVG: el PNG tiene que estar en el ensamblado.
        const string prefix = "data:image/png;base64,";
        Assert.StartsWith(prefix, Branding.IconPngDataUri);

        var png = Convert.FromBase64String(Branding.IconPngDataUri[prefix.Length..]);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png[..4]);
    }
}
