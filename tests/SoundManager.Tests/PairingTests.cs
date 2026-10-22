using SoundManager.Core;
using SoundManager.Server;

namespace SoundManager.Tests;

public sealed class PairingTests
{
    [Theory]
    [InlineData("http://192.168.0.9:17842")]
    [InlineData("http://100.93.0.1:17842")]
    [InlineData("http://[fd00::1]:17842")]
    [InlineData("https://example.trycloudflare.com")]
    public void RoundTripsLocalAndExternalConnectionInformation(string address)
    {
        var original = new PairingLink(address, HubServer.CreateToken());
        Assert.Equal(original, PairingLink.Parse(original.Encode()));
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("http://8.8.8.8:17842")]
    [InlineData("https://user:password@example.com")]
    [InlineData("https://example.com/other")]
    [InlineData("https://example.com?key=secret")]
    [InlineData("https://example.com#fragment")]
    [InlineData("file:///etc/passwd")]
    public void RejectsUnsafeOrAmbiguousDestinations(string address)
    {
        Assert.Throws<ArgumentException>(() => new PairingLink(address, HubServer.CreateToken()).Encode());
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("soundmanager://pair?v=2&server=https%3A%2F%2Fexample.com&key=aaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("soundmanager://pair?v=1&server=https%3A%2F%2Fexample.com&key=short")]
    [InlineData("soundmanager://pair?v=1&v=1&server=https%3A%2F%2Fexample.com&key=aaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("soundmanager://pair?v=1&server=https%3A%2F%2Fexample.com&key=aaaaaaaaaaaaaaaaaaaaaaaa&extra=true")]
    [InlineData("soundmanager://attacker?v=1&server=https%3A%2F%2Fexample.com&key=aaaaaaaaaaaaaaaaaaaaaaaa")]
    public void RejectsUnrelatedAndMalformedQrCodes(string code)
    {
        Assert.Throws<ArgumentException>(() => PairingLink.Parse(code));
    }

    [Fact]
    public async Task TunnelRejectsUnverifiedExecutableBeforeLaunching()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "this must never be launched");
            await using var tunnel = new ExternalTunnel();
            await Assert.ThrowsAsync<InvalidOperationException>(() => tunnel.StartAsync(path));
        }
        finally { File.Delete(path); }
    }
}
