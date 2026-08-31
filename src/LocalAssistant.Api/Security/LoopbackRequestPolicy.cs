using System.Net;

namespace LocalAssistant.Api.Security;

public interface ILoopbackRequestPolicy
{
    bool IsLoopback(HttpContext context);
}

public sealed class ConnectionLoopbackRequestPolicy : ILoopbackRequestPolicy
{
    public bool IsLoopback(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } remoteAddress && IPAddress.IsLoopback(remoteAddress);
}
