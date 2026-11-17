using MovieDiscovery.Bot;
using Xunit;

namespace MovieDiscovery.Bot.Tests;

public class TunnelServiceTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void MissingPublicUrl_PreservesQuickTunnelMode(string? url)
    {
        Assert.False(new TunnelService(url).IsRegistered);
    }

    [Theory]
    [InlineData("kino.example.test")]
    [InlineData("file:///tmp/index.html")]
    [InlineData("https://user:pass@kino.example.test")]
    [InlineData("https://kino.example.test?token=secret")]
    [InlineData("https://kino.example.test/#section")]
    public void InvalidPublicUrl_IsRejected(string url)
    {
        Assert.Throws<ArgumentException>(() => new TunnelService(url));
    }
}
