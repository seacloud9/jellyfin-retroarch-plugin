using System.Security.Cryptography;
using Jellyfin.Plugin.RetroBridge;

namespace RetroBridge.Tests;

public sealed class DemoLibraryTests
{
    [Fact]
    public void ShipsEightIdentifiableCartsWithScreenshots()
    {
        Assert.Equal(8, DemoLibrary.Games.Count);
        Assert.Contains(DemoLibrary.Games, x => x.Game.Title == "Hello 3D");
        foreach (var item in DemoLibrary.Games)
        {
            Assert.True(item.Game.IsBundled);
            using var cart = DemoLibrary.OpenCart(item);
            Assert.Equal("nova64-" + Convert.ToHexStringLower(SHA256.HashData(cart)), item.Game.Id);
            using var screenshot = DemoLibrary.OpenScreenshot(item);
            var signature = new byte[8];
            screenshot.ReadExactly(signature);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, signature);
        }
    }

    [Theory]
    [InlineData("retrobridge-web", false)]
    [InlineData("retrobridge-launcher", false)]
    [InlineData("retrobridge-0123456789abcdef0123456789abcdef", true)]
    public void RecognizesPairedCompanions(string id, bool expected) =>
        Assert.Equal(expected, JellyfinPairedDevices.IsCompanionId(id));
}
