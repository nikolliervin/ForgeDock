using ForgeDock.Application;
using ForgeDock.Infrastructure;
using System.Net;

namespace ForgeDock.Tests;

public sealed class DomainTests
{
    private static readonly DomainSettings Settings = new(true, "ingress.example.com", ["203.0.113.10", "2001:db8::10"], "ops@example.com");
    [Theory]
    [InlineData("https://app.example.com")][InlineData("app.example.com:443")][InlineData("*.example.com")]
    [InlineData("127.0.0.1")][InlineData("localhost")][InlineData("app.local")][InlineData("app.internal")]
    [InlineData("app.example.com/path")][InlineData("app.example.com;evil")][InlineData("-app.example.com")]
    [InlineData("app..example.com")][InlineData("app.example.com\n")]
    public void RejectsUnsafeHostnames(string hostname) => Assert.False(DomainName.TryNormalize(hostname, out _));
    [Theory]
    [InlineData(" APP.Example.COM. ", "app.example.com")][InlineData("bücher.example.com", "xn--bcher-kva.example.com")]
    public void NormalizesHostnames(string input, string expected)
    { Assert.True(DomainName.TryNormalize(input, out var hostname)); Assert.Equal(expected, hostname); }
    [Fact]
    public async Task OwnershipAndEveryPublishedAddressMustMatch()
    {
        var dns = new FakeDns { Txt = ["token"], Addresses = [IPAddress.Parse("203.0.113.10")] };
        var verifier = new DomainDnsVerifier(dns);
        Assert.True((await verifier.VerifyAsync("app.example.com", "token", Settings, CancellationToken.None)).Verified);
        Assert.Equal("_forgedock.app.example.com", dns.TxtName);
        dns.Txt = ["wrong-token"];
        Assert.False((await verifier.VerifyAsync("app.example.com", "token", Settings, CancellationToken.None)).Verified);
        dns.Txt = ["token"];
        dns.Addresses = [IPAddress.Parse("203.0.113.10"), IPAddress.Parse("2001:db8::99")];
        Assert.False((await verifier.VerifyAsync("app.example.com", "token", Settings, CancellationToken.None)).Verified);
        dns.Addresses = [];
        Assert.False((await verifier.VerifyAsync("app.example.com", "token", Settings, CancellationToken.None)).Verified);
    }
    [Fact]
    public void InvalidHostingConfigurationCannotBeActivated()
    {
        Assert.Null(Settings.Validate());
        Assert.NotNull((Settings with { Enabled = false }).Validate());
        Assert.NotNull((Settings with { Email = "invalid" }).Validate());
        Assert.NotNull((Settings with { Target = "https://example.com" }).Validate());
        Assert.NotNull((Settings with { Addresses = [] }).Validate());
        Assert.NotNull((Settings with { AcmeDirectory = "http://example.com" }).Validate());
    }
    private sealed class FakeDns : IDomainDnsResolver
    {
        public IReadOnlyList<string> Txt { get; set; } = [];
        public IReadOnlyList<IPAddress> Addresses { get; set; } = [];
        public string? TxtName { get; private set; }
        public Task<IReadOnlyList<string>> TxtAsync(string name, CancellationToken ct) { TxtName = name; return Task.FromResult(Txt); }
        public Task<IReadOnlyList<IPAddress>> AddressesAsync(string name, CancellationToken ct) => Task.FromResult(Addresses);
    }
}
