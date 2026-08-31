using System.Net;
using LocalAssistant.Api.Security;
using Microsoft.AspNetCore.Http;

namespace LocalAssistant.Tests.Api;

public sealed class LoopbackRequestPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.1.10", false)]
    public void IsLoopbackUsesTheRemoteConnectionAddress(string remoteAddress, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);

        var result = new ConnectionLoopbackRequestPolicy().IsLoopback(context);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsLoopbackRejectsAnUnknownRemoteAddress()
    {
        Assert.False(new ConnectionLoopbackRequestPolicy().IsLoopback(new DefaultHttpContext()));
    }
}
