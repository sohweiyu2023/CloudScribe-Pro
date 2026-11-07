using System.Net.Sockets;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Production transport for Long Audio and its private storage reads.
/// TCP keepalive helps detect unresponsive peers. It cannot detect every stalled application on a live connection.
/// It adds no application deadline to a POST: a live connection still waits for Google's answer, and a detected
/// failure after the send started remains an unknown outcome under the existing no-resend rules.</summary>
public static class GoogleLongAudioTransport
{
    /// <summary>Idle time before the first keepalive probe.</summary>
    public static readonly TimeSpan KeepAliveIdle = TimeSpan.FromSeconds(60);

    /// <summary>Interval between unanswered probes.</summary>
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(10);

    /// <summary>Unanswered probes before the connection is treated as dead (about two minutes in total).</summary>
    public const int KeepAliveProbes = 6;

    /// <summary>How long one idempotent operation-status GET may wait before the next status check replaces it.</summary>
    public static readonly TimeSpan PollResponseDeadline = TimeSpan.FromMinutes(2);

    /// <summary>Upper bound for one private output download and its verification reads. Resume repeats only GETs.</summary>
    public static readonly TimeSpan StorageReadDeadline = TimeSpan.FromMinutes(30);

    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectCallback = static async (context, token) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, (int)KeepAliveIdle.TotalSeconds);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, (int)KeepAliveInterval.TotalSeconds);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, KeepAliveProbes);
                await socket.ConnectAsync(context.DnsEndPoint, token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}
