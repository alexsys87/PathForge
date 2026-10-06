using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PathForge.Core.Grbl;

namespace PathForge.Core.Tests;

/// <summary>The network transport against a local TCP server that plays a Grbl_Esp32 board (MKS DLC32).</summary>
public class TelnetTransportTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public void Exchanges_lines_and_realtime_bytes_with_the_board()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var transport = new TelnetTransport("127.0.0.1", port);
        using var board = listener.AcceptTcpClient();
        var stream = board.GetStream();

        // The welcome banner arrives right after connecting, before anyone listens: it must not be lost.
        Send(stream, "\r\nGrbl 1.1 ['$' for help]\r\n");
        Thread.Sleep(100);

        var lines = new BlockingCollection<string>();
        transport.LineReceived += lines.Add;
        transport.Start();

        Assert.Equal("", Take(lines));
        Assert.Equal("Grbl 1.1 ['$' for help]", Take(lines));

        // Telnet negotiation (IAC DO ECHO, IAC SB ... IAC SE) from bridges is skipped; a line may come in pieces.
        stream.Write(new byte[] { 255, 253, 1, 255, 250, 24, 1, 255, 240 });
        Send(stream, "<Idle|MPos:0.000,0.000,0.000|FS:0,0>\r\no");
        Thread.Sleep(50);
        Send(stream, "k\r\n");
        Assert.Equal("<Idle|MPos:0.000,0.000,0.000|FS:0,0>", Take(lines));
        Assert.Equal("ok", Take(lines));

        transport.Write("G0X1\n");
        transport.WriteRealtime((byte)'?');
        transport.WriteRealtime(0x85);
        var received = new byte[7];
        var read = 0;
        stream.ReadTimeout = 5000;
        while (read < received.Length)
        {
            read += stream.Read(received, read, received.Length - read);
        }

        Assert.Equal(Encoding.ASCII.GetBytes("G0X1\n?").Concat(new byte[] { 0x85 }), received);
    }

    [Fact]
    public void Board_closing_the_connection_is_reported_once()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var transport = new TelnetTransport("127.0.0.1", port);
        var failures = new BlockingCollection<Exception>();
        transport.Failed += failures.Add;
        transport.Start();

        // Grbl_Esp32 serves one telnet client: a second one is accepted and dropped at once.
        listener.AcceptTcpClient().Dispose();

        Assert.True(failures.TryTake(out _, Wait));
        Assert.False(failures.TryTake(out _, TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void Controller_over_the_network_notices_the_lost_board()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var controller = new GrblController(new TelnetTransport("127.0.0.1", port));
        var board = listener.AcceptTcpClient();
        Send(board.GetStream(), "<Idle|MPos:1.000,2.000,3.000|FS:0,0>\r\n");
        Assert.True(SpinWait.SpinUntil(() => controller.Status.State == GrblState.Idle, Wait));

        board.Dispose();
        Assert.True(SpinWait.SpinUntil(() => !controller.IsConnected, Wait));
        controller.Dispose();
    }

    [Fact]
    public void Disposing_does_not_report_a_failure()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var transport = new TelnetTransport("127.0.0.1", port);
        using var board = listener.AcceptTcpClient();
        var failed = false;
        transport.Failed += _ => failed = true;
        transport.Start();

        transport.Dispose();
        Thread.Sleep(200);

        Assert.False(failed);
        Assert.ThrowsAny<InvalidOperationException>(() => transport.Write("?\n"));
    }

    [Fact]
    public void Refused_connection_throws()
    {
        // A port that was just free: nothing listens on it.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Assert.ThrowsAny<SocketException>(() => new TelnetTransport("127.0.0.1", port));
    }

    private static void Send(NetworkStream stream, string text) => stream.Write(Encoding.ASCII.GetBytes(text));

    private static string Take(BlockingCollection<string> lines)
    {
        Assert.True(lines.TryTake(out var line, Wait), "no line from the transport");
        return line!;
    }
}
