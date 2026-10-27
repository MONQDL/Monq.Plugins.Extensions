using Monq.Plugins.Extensions.Net.Tcp;
using System.Net;
using Xunit;

namespace Monq.Plugins.Extensions.Tests;

public class TcpListenerHostTests
{
    [Fact]
    public void Constructor_RejectsNullEndpoint()
        => Assert.Throws<ArgumentNullException>(() => new TcpListenerHost(null!));

    [Fact]
    public async Task RunAsync_RejectsNullClientHandler()
    {
        var host = new TcpListenerHost(new IPEndPoint(IPAddress.Loopback, 0));

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => host.RunAsync(null!, null, null, CancellationToken.None));
    }
}
