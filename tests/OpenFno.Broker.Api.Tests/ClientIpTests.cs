using System.Net;
using Microsoft.AspNetCore.Http;
using OpenFno.Broker.Api.Http;

namespace OpenFno.Broker.Api.Tests;

/// <summary>
/// Which address the broker believes a caller came from. Everything about the
/// static-IP rule rests on this: read it wrong and every caller behind the same
/// proxy shares one identity.
/// </summary>
public class ClientIpTests
{
    private const string Header = "CF-Connecting-IP";

    private static HttpContext From(string peer, string? forwarded = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        if (forwarded is not null) context.Request.Headers[Header] = forwarded;
        return context;
    }

    [Fact]
    public void The_socket_address_is_used_when_there_is_no_header()
    {
        Assert.Equal("49.36.1.2", RequestTraceMiddleware.ResolveClientIp(From("49.36.1.2"), Header));
    }

    [Fact]
    public void A_header_from_this_machine_is_believed()
    {
        var context = From("127.0.0.1", "49.36.1.2");

        Assert.Equal("49.36.1.2", RequestTraceMiddleware.ResolveClientIp(context, Header));
    }

    [Fact]
    public void A_header_from_a_stranger_is_ignored()
    {
        // Anyone can send the header; only the proxy's own connection makes it true.
        var context = From("203.0.113.9", "49.36.1.2");

        Assert.Equal("203.0.113.9", RequestTraceMiddleware.ResolveClientIp(context, Header));
    }

    [Fact]
    public void A_header_from_a_listed_proxy_is_believed()
    {
        // In a container the tunnel reaches the broker across the Docker bridge,
        // so its gateway is not loopback and has to be vouched for by name.
        var context = From("172.18.0.1", "49.36.1.2");

        Assert.Equal("49.36.1.2", RequestTraceMiddleware.ResolveClientIp(context, Header, ["172.18.0.1"]));
    }

    [Fact]
    public void A_listed_range_covers_the_proxy_the_container_runtime_happens_to_pick()
    {
        // Docker hands out a different bridge subnet per project; the range
        // spares the operator from chasing it after every recreate.
        var context = From("172.31.0.1", "49.36.1.2");

        Assert.Equal("49.36.1.2", RequestTraceMiddleware.ResolveClientIp(context, Header, ["172.16.0.0/12"]));
    }

    [Fact]
    public void A_proxy_outside_the_listed_range_is_still_a_stranger()
    {
        var context = From("10.0.0.5", "49.36.1.2");

        Assert.Equal("10.0.0.5", RequestTraceMiddleware.ResolveClientIp(context, Header, ["172.16.0.0/12"]));
    }
}
