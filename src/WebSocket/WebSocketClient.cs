using System.Net.WebSockets;

public class WebSocketClient
{
    Guid guid;
    WebSocket socket;
    public WebSocketClient(Guid guid, WebSocket socket)
    {
        this.guid = guid;
        this.socket = socket;
    }

    public void SendMessage(string message){}
}