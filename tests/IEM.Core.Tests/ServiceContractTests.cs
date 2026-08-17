using IEM.Storage;

namespace IEM.Core.Tests;

/// <summary>
/// The two halves of this application update separately: the service through an installer
/// that needs administrator rights, the window through whatever the user happens to run. A
/// 2.3 window talking to a 2.2 service is the ordinary state of affairs for as long as it
/// takes someone to get round to the second half - and it has to say so plainly, because a
/// user told "the service is not running" will reinstall something that works perfectly well.
/// </summary>
public sealed class ServiceContractTests
{
    [Fact]
    public void A_peer_speaking_this_protocol_is_supported()
    {
        Assert.True(ServiceContract.SupportsProtocol(ServiceContract.ProtocolVersion));
    }

    /// <summary>
    /// Equality, deliberately. A range would be a claim about compatibility nobody has
    /// tested; when a version 2 exists and is genuinely backward compatible, the one place
    /// that says so is <see cref="ServiceContract.SupportsProtocol"/>.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    public void A_peer_speaking_anything_else_is_not(int theirVersion)
    {
        Assert.False(ServiceContract.SupportsProtocol(theirVersion));
    }

    [Fact]
    public void The_protocol_version_is_stated_and_positive()
    {
        Assert.True(ServiceContract.ProtocolVersion > 0);
        Assert.NotEmpty(ServiceContract.AppVersion);
    }
}
