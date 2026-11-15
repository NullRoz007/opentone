using System.Text.Json.Nodes;
using OpenTone;

public class WebSocketMessageHandler
{
    private readonly FenderAmp amp;
    public WebSocketMessageHandler(FenderAmp  amp)
    {
        this.amp = amp;
    }

    public void HandleMessage(string message) // we only recieve one type of message, ever
    {
        var paramChange = JsonNode.Parse(message);

        string? node = null, param = null, value = null;

        node = paramChange?["node"]?.ToString();
        param = paramChange?["param"]?.ToString();
        value = paramChange?["value"]?.ToString();
        AmpActions.SetParamAuto(amp, node ?? "", param ?? "", value ?? "");
    }
}