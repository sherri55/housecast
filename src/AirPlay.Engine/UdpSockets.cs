using System.Net;
using System.Net.Sockets;

namespace AirPlay.Engine;

internal static class UdpSockets
{
    // SIO_UDP_CONNRESET: without it, an ICMP "port unreachable" from an earlier send makes
    // the next receive on the socket fail on Windows
    private const int SioUdpConnReset = -1744830452;

    /// <param name="port">0 lets Windows pick a free port.</param>
    public static UdpClient Bind(int port)
    {
        var socket = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        socket.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        return socket;
    }
}
