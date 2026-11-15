using System.Net;
using System.Net.WebSockets;
using System.Text;
using OpenTone;

class WebSocketServer
{
    private readonly FenderAmp amp;
    private WebSocketMessageHandler? messageHandler;
    public WebSocketServer(FenderAmp amp)
    {
        this.amp = amp;
    }
    public async Task HandleSocket(WebSocket socket, Guid socketId)
    {
        while (socket.State == WebSocketState.Open)
        {
            var buffer = new ArraySegment<byte>(new byte[4096]);
            var received = await socket.ReceiveAsync(buffer, CancellationToken.None);

            if (received.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                break;
            }

            var message = Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, received.Count);
            messageHandler?.HandleMessage(message);
        }
    }
    public async Task Run(int port)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        messageHandler = new WebSocketMessageHandler(amp);

        while (true)
        {
            var context = await listener.GetContextAsync();
            if (context.Request.IsWebSocketRequest)
            {
                var socket = await context.AcceptWebSocketAsync(null);
                var socketId = Guid.NewGuid();
                _ = HandleSocket(socket.WebSocket, socketId);
            }
            else
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
            }
        }
    }
}