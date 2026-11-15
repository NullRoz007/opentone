using System.Net;
using System.Net.WebSockets;
using System.Text;
using OpenTone;

class WebSocketServer
{
    // Browsers attach the page's origin to WebSocket connections. Add your own host if you self-host Strudel.
    static readonly string[] AllowedOrigins = ["https://strudel.cc"];
    const int MaxMessageBytes = 16 * 1024;

    private readonly FenderAmp amp;
    private WebSocketMessageHandler? messageHandler;

    public WebSocketServer(FenderAmp amp)
    {
        this.amp = amp;
    }

    static bool OriginAllowed(string? origin)
    {
        if (string.IsNullOrEmpty(origin)) return true;          // non-browser client (curl, a script)
        if (AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback;   // local dev servers
    }

    public async Task HandleSocket(WebSocket socket, Guid socketId, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult received;

                do
                {
                    received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                        return;
                    }

                    message.Write(buffer, 0, received.Count);

                    if (message.Length > MaxMessageBytes)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "", CancellationToken.None);
                        return;
                    }
                }
                while (!received.EndOfMessage);

                if (received.MessageType == WebSocketMessageType.Text)
                {
                    string text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    messageHandler?.HandleMessage(text);
                }
            }
        }
        catch (WebSocketException) { /* the client vanished without a close handshake */ }
        catch (OperationCanceledException) { }
        finally
        {
            socket.Dispose();
        }
    }

    public async Task Run(int port, CancellationToken ct = default)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        using var stopOnCancel = ct.Register(listener.Stop);

        messageHandler = new WebSocketMessageHandler(amp);
        Task worker = messageHandler.RunAsync(ct);               // the only code that talks to the amp

        try
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { break; }

                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    continue;
                }

                if (!OriginAllowed(context.Request.Headers["Origin"]))
                {
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    continue;
                }

                var socket = await context.AcceptWebSocketAsync(null);
                _ = HandleSocket(socket.WebSocket, Guid.NewGuid(), ct);
            }
        }
        finally
        {
            listener.Close();
            try { await worker; } catch (OperationCanceledException) { }
        }
    }
}