using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using PathForge.Core.Grbl;

namespace PathForge.Core.Tests;

/// <summary>The WebSocket transport against a local server that plays the web interface of an MKS DLC32 (Grbl_Esp32).</summary>
public sealed class WebSocketTransportTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly HttpListener _listener = new();
    private readonly int _wsPort = FreePort();
    private readonly int _httpPort = FreePort();
    private readonly TaskCompletionSource<WebSocket> _board = new();
    private readonly BlockingCollection<string> _commands = new();
    private readonly BlockingCollection<string> _rawQueries = new();

    public WebSocketTransportTests()
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{_wsPort}/");
        _listener.Prefixes.Add($"http://127.0.0.1:{_httpPort}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    /// <summary>Delay of every HTTP answer, to imitate a slow WiFi link.</summary>
    private TimeSpan HttpDelay { get; set; }

    private HttpStatusCode HttpStatus { get; set; } = HttpStatusCode.OK;

    public void Dispose() => _listener.Close();

    [Fact]
    public void Output_comes_from_the_socket_and_commands_go_over_http()
    {
        using var transport = Connect(WebSocketCommandRoute.Http);
        var lines = new BlockingCollection<string>();
        transport.LineReceived += lines.Add;
        transport.Start();
        var board = Board();

        // Service messages of the web interface are not GRBL output; a line may be split between frames.
        SendText(board, "CURRENT_ID:0");
        SendBinary(board, "<Idle|MPos:0.000,0.000,0.000|FS:0,0>\r\no");
        SendText(board, "PING:5000:5000");
        SendBinary(board, "k\r\n");
        Assert.Equal("<Idle|MPos:0.000,0.000,0.000|FS:0,0>", Take(lines));
        Assert.Equal("ok", Take(lines));

        transport.Write("G0X1\n");
        transport.WriteRealtime((byte)'?');
        transport.WriteRealtime(0x85);

        var received = new List<string>();
        while (received.Count < 3)
        {
            Assert.True(_commands.TryTake(out var command, Wait), "no command reached the board");
            received.AddRange(command.Split('\n'));
        }

        Assert.Equal(new[] { "G0X1", "?", "\u0085" }, received);
        // Real-time bytes above 0x7F travel as UTF-8; the board strips the C2 prefix.
        Assert.Contains(_rawQueries, q => q.Contains("%C2%85", StringComparison.Ordinal));
    }

    [Fact]
    public void Status_queries_do_not_pile_up_on_a_slow_link()
    {
        HttpDelay = TimeSpan.FromMilliseconds(300);
        using var transport = Connect(WebSocketCommandRoute.Http);
        transport.Start();

        for (var i = 0; i < 10; i++)
        {
            transport.WriteRealtime((byte)'?');
            Thread.Sleep(20);
        }

        transport.Write("G0X1\n");
        var received = new List<string>();
        while (!received.Contains("G0X1"))
        {
            Assert.True(_commands.TryTake(out var command, Wait), "no command reached the board");
            received.AddRange(command.Split('\n'));
        }

        Assert.InRange(received.Count(c => c == "?"), 1, 3);
    }

    [Fact]
    public async Task Bridge_route_writes_commands_into_the_socket()
    {
        using var transport = Connect(WebSocketCommandRoute.WebSocket);
        transport.Start();
        var board = Board();

        transport.Write("G0X1\n");
        transport.WriteRealtime(0x85);

        var frames = new List<(WebSocketMessageType Type, byte[] Data)>();
        var buffer = new byte[256];
        using var timeout = new CancellationTokenSource(Wait);
        while (frames.Sum(f => f.Data.Length) < 6)
        {
            var result = await board.ReceiveAsync(buffer, timeout.Token);
            frames.Add((result.MessageType, buffer[..result.Count]));
        }

        Assert.Equal(Encoding.ASCII.GetBytes("G0X1\n").Append((byte)0x85), frames.SelectMany(f => f.Data));
        // Bytes above 0x7F are not valid UTF-8 text: such frames are binary.
        Assert.All(frames.Where(f => f.Data.Any(b => b >= 0x80)), f => Assert.Equal(WebSocketMessageType.Binary, f.Type));
        Assert.Empty(_commands);
    }

    [Fact]
    public async Task Board_closing_the_socket_is_reported()
    {
        using var transport = Connect(WebSocketCommandRoute.Http);
        var failures = new BlockingCollection<Exception>();
        transport.Failed += failures.Add;
        transport.Start();

        await Board().CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);

        Assert.True(failures.TryTake(out _, Wait));
        Assert.Throws<IOException>(() => transport.Write("G0X1\n"));
    }

    [Fact]
    public void Login_required_by_the_board_is_reported()
    {
        HttpStatus = HttpStatusCode.Unauthorized;
        using var transport = Connect(WebSocketCommandRoute.Http);
        var failures = new BlockingCollection<Exception>();
        transport.Failed += failures.Add;
        transport.Start();

        transport.Write("$X\n");

        Assert.True(failures.TryTake(out var error, Wait));
        Assert.IsType<IOException>(error);
    }

    [Fact]
    public void Missing_board_throws()
    {
        Assert.ThrowsAny<IOException>(() => new WebSocketTransport("127.0.0.1", FreePort()));
    }

    private WebSocketTransport Connect(WebSocketCommandRoute route) => new("127.0.0.1", _wsPort, route, _httpPort);

    private WebSocket Board()
    {
        Assert.True(_board.Task.Wait(Wait), "the transport did not open the WebSocket");
        return _board.Task.Result;
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            if (context.Request.IsWebSocketRequest)
            {
                var socket = await context.AcceptWebSocketAsync("arduino");
                _board.TrySetResult(socket.WebSocket);
                continue;
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(HttpDelay);
                var query = context.Request.Url!.Query;
                _rawQueries.Add(query);
                const string key = "?commandText=";
                if (query.StartsWith(key, StringComparison.Ordinal))
                {
                    _commands.Add(Uri.UnescapeDataString(query[key.Length..]));
                }

                context.Response.StatusCode = (int)HttpStatus;
                context.Response.Close();
            });
        }
    }

    private static void SendText(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.ASCII.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();

    private static void SendBinary(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.ASCII.GetBytes(text), WebSocketMessageType.Binary, true, CancellationToken.None).GetAwaiter().GetResult();

    private static string Take(BlockingCollection<string> lines)
    {
        Assert.True(lines.TryTake(out var line, Wait), "no line from the transport");
        return line!;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
