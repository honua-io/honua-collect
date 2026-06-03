using Honua.Collect.Core.Sync;

namespace Honua.Collect.Core.Tests.Sync;

public class EndpointSecurityTests
{
    [Theory]
    [InlineData("https://collect.honua.io", true)]   // https anywhere
    [InlineData("https://10.0.2.2:18080", true)]
    [InlineData("http://10.0.2.2:18080", true)]       // emulator host loopback, cleartext ok
    [InlineData("http://localhost:18080", true)]
    [InlineData("http://127.0.0.1:5000", true)]
    [InlineData("http://collect.honua.io", false)]    // cleartext to a real host
    [InlineData("http://192.168.1.50:8080", false)]   // cleartext to a LAN host
    public void IsTransportSecure_allows_https_or_loopback_cleartext_only(string url, bool expected)
        => Assert.Equal(expected, EndpointSecurity.IsTransportSecure(new Uri(url)));

    [Fact]
    public void EnsureSecureTransport_throws_for_cleartext_to_a_real_host()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => EndpointSecurity.EnsureSecureTransport(new Uri("http://collect.honua.io")));
        Assert.Contains("cleartext", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureSecureTransport_passes_for_https_and_loopback()
    {
        EndpointSecurity.EnsureSecureTransport(new Uri("https://collect.honua.io"));
        EndpointSecurity.EnsureSecureTransport(new Uri("http://10.0.2.2:18080"));
        // no throw
    }
}
