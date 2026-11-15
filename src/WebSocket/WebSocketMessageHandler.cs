using System.Text.Json;
using System.Text.Json.Nodes;
using OpenTone;
using static OpenTone.FenderProtocol;

public sealed class WebSocketMessageHandler(FenderAmp amp)
{
    static readonly HashSet<string> Nodes = ["stomp", "mod", "amp", "delay", "reverb"];

    readonly object gate = new();
    readonly Dictionary<(string Node, string Param), JsonValue> pendingParams = new();
    readonly Queue<(string Node, string Param)> paramOrder = new();
    readonly Queue<(string Node, string Model)> pendingModels = new();
    readonly SemaphoreSlim signal = new(0);

    // ---- receive side: parse, validate, queue. Never touches the amp. ----

    public void HandleMessage(string message)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(message); }
        catch (JsonException) { Log("not JSON: " + message); return; }

        string? node = msg?["node"]?.ToString();
        if (node is null || !Nodes.Contains(node)) { Log($"unknown node: {node}"); return; }

        // {"node":"amp","model":"DUBS_Champ57"}
        if (msg?["model"] is JsonNode m)
        {
            string model = m.ToString();
            if (!model.StartsWith("DUBS_")) model = "DUBS_" + model;

            lock (gate)
            {
                // pending parameter values were meant for the old model
                foreach (var key in pendingParams.Keys.Where(k => k.Node == node).ToList())
                    pendingParams.Remove(key);
                pendingModels.Enqueue((node, model));
            }
            signal.Release();
            return;
        }

        // {"node":"amp","param":"gain","value":0.5 | true | "text"}
        string? param = msg?["param"]?.ToString();
        if (string.IsNullOrEmpty(param) || msg?["value"] is not JsonValue value)
        {
            Log("missing param or value");
            return;
        }

        lock (gate)
        {
            var key = (node, param);
            if (!pendingParams.ContainsKey(key)) paramOrder.Enqueue(key);
            pendingParams[key] = value;              // latest value wins
        }
        signal.Release();
    }

    // ---- send side: one worker, the only caller of the amp ----

    public async Task RunAsync(CancellationToken ct)
    {
        while (true)
        {
            await signal.WaitAsync(ct);

            while (TryTake(out var work))
            {
                try { work(); }
                catch (Exception e) when (
                    e is TimeoutException or IOException or FormatException
                      or NotSupportedException or ArgumentException)
                {
                    Log("amp: " + e.Message);
                }
            }
        }
    }

    bool TryTake(out Action work)
    {
        lock (gate)
        {
            if (pendingModels.Count > 0)             // model swaps go first
            {
                var (node, model) = pendingModels.Dequeue();
                work = () =>
                {
                    if (amp.SetModel(node, model) is null) Log($"no echo for {node} model");
                };
                return true;
            }

            while (paramOrder.Count > 0)
            {
                var key = paramOrder.Dequeue();
                if (!pendingParams.Remove(key, out var value)) continue;   // dropped by a model swap
                work = () => Apply(key.Node, key.Param, value);
                return true;
            }
        }

        work = null!;
        return false;
    }

    void Apply(string node, string param, JsonValue value)
    {
        ParamChange? change;

        if (value.TryGetValue<bool>(out var b))
        {
            change = amp.SetBool(node, param, b);
        }
        else if (value.TryGetValue<double>(out var d))
        {
            if (param == "volume") d = Math.Min(d, 0);       // dB: 0 was the highest seen, never boost
            change = amp.SetFloat(node, param, (float)d);
        }
        else if (value.TryGetValue<string>(out var s))
        {
            AmpActions.SetParamAuto(amp, node, param, s);    // slow path: reads the preset
            return;
        }
        else
        {
            Log($"unsupported value for {node}.{param}");
            return;
        }

        if (change is null) Log($"no echo for {node}.{param}");
    }

    static void Log(string text) => Console.Error.WriteLine(text);
}