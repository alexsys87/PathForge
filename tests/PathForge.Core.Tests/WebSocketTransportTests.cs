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
    private static readonly TimeSpan FailureWait = TimeSpan.FromSeconds(15);

    private readonly HttpListener _listener = new();
    private readonly int _wsPort = FreePort();
    private readonly int _httpPort = FreePort();
    private readonly TaskCompletionSource<WebSocket> _board = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

    public void Dispose()
    {
        if (_board.Task.IsCompletedSuccessfully)
        {
            _board.Task.Result.Dispose();
        }

        _listener.Close();
    }

    [Fact]
    public async Task Output_comes_from_the_socket_and_commands_go_over_http()
    {
        using var transport = await ConnectAsync(WebSocketCommandRoute.Http);
        var lines = new BlockingCollection<string>();
        transport.LineReceived += lines.Add;
        transport.Start();
        var board = await BoardAsync();

        // Service messages of the web interface are not GRBL output; a line may be split between frames.
        await SendTextAsync(board, "CURRENT_ID:0");
        await SendBinaryAsync(board, "<Idle|MPos:0.000,0.000,0.000|FS:0,0>\r\no");
        await SendTextAsync(board, "PING:5000:5000");
        await SendBinaryAsync(board, "k\r\n");
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
    public async Task Status_queries_do_not_pile_up_on_a_slow_link()
    {
        HttpDelay = TimeSpan.FromMilliseconds(300);
        using var transport = await ConnectAsync(WebSocketCommandRoute.Http);
        transport.Start();

        for (var i = 0; i < 10; i++)
        {
            transport.WriteRealtime((byte)'?');
            await Task.Delay(20);
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
        using var transport = await ConnectAsync(WebSocketCommandRoute.WebSocket);
        transport.Start();
        var board = await BoardAsync();

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

    [Theory]
    [InlineData(WebSocketCommandRoute.Http)]
    [InlineData(WebSocketCommandRoute.WebSocket)]
    public async Task Board_closing_the_socket_is_reported(WebSocketCommandRoute route)
    {
        using var transport = await ConnectAsync(route);
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Failed += error => failure.TrySetResult(error);
        transport.Start();

        using var timeout = new CancellationTokenSource(FailureWait);
        var board = await BoardAsync();
        await board.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);

        // Do not block a test-host worker while waiting for network I/O completions. In Debug on
        // .NET 8 the busy Windows runner can otherwise time out before the close is dispatched.
        var error = await failure.Task.WaitAsync(timeout.Token);
        Assert.IsType<IOException>(error);
        Assert.Throws<IOException>(() => transport.Write("G0X1\n"));
    }

    [Fact]
    public async Task Login_required_by_the_board_is_reported()
    {
        HttpStatus = HttpStatusCode.Unauthorized;
        using var transport = await ConnectAsync(WebSocketCommandRoute.Http);
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Failed += error => failure.TrySetResult(error);
        transport.Start();

        transport.Write("$X\n");

        var error = await failure.Task.WaitAsync(FailureWait);
        Assert.IsType<IOException>(error);
    }

    [Fact]
    public void Missing_board_throws()
    {
        Assert.ThrowsAny<IOException>(() => new WebSocketTransport("127.0.0.1", FreePort()));
    }

    // The public constructor is synchronous. Run it outside xUnit's bounded worker context,
    // so the local HTTP/WebSocket server can finish the handshake while the test awaits it.
    // CI scheduling is not a board-connect-timeout test: allow a separate setup budget without
    // changing the application's four-second default or retrying a failed test.
    private Task<WebSocketTransport> ConnectAsync(WebSocketCommandRoute route) => Task.Run(() =>
        new WebSocketTransport("127.0.0.1", _wsPort, route, _httpPort, connectTimeout: FailureWait));

    private Task<WebSocket> BoardAsync() => _board.Task.WaitAsync(FailureWait);

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

    private static async Task SendTextAsync(WebSocket socket, string text)
    {
        using var timeout = new CancellationTokenSource(Wait);
        await socket.SendAsync(Encoding.ASCII.GetBytes(text), WebSocketMessageType.Text, true, timeout.Token);
    }

    private static async Task SendBinaryAsync(WebSocket socket, string text)
    {
        using var timeout = new CancellationTokenSource(Wait);
        await socket.SendAsync(Encoding.ASCII.GetBytes(text), WebSocketMessageType.Binary, true, timeout.Token);
    }

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
