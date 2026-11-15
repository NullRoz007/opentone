using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace OpenTone;

/// <summary>
/// A Fender Tone preset: metadata plus an audio graph of DSP units.
/// Unknown fields are preserved in <see cref="Extra"/> so a preset can be
/// read, edited, and sent back to the amp without losing anything.
/// </summary>
public sealed class FenderPreset
{
    public const string RootNodeId = "preset";

    private static readonly JsonSerializerOptions Compact = new();
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [JsonPropertyName("nodeType")]   public string NodeType { get; set; } = "preset";
    [JsonPropertyName("nodeId")]     public string NodeId { get; set; } = RootNodeId;
    [JsonPropertyName("version")]    public string Version { get; set; } = "";
    [JsonPropertyName("numInputs")]  public int NumInputs { get; set; }
    [JsonPropertyName("numOutputs")] public int NumOutputs { get; set; }
    [JsonPropertyName("info")]       public PresetInfo Info { get; set; } = new();
    [JsonPropertyName("audioGraph")] public AudioGraph AudioGraph { get; set; } = new();

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    // ---- Parsing / serialization -------------------------------------------

    public static FenderPreset Parse(string json) =>
        JsonSerializer.Deserialize<FenderPreset>(json, Compact)
        ?? throw new FormatException("preset JSON was empty");

    public static FenderPreset FromNode(JsonNode node) =>
        node.Deserialize<FenderPreset>(Compact)
        ?? throw new FormatException("preset JSON was empty");

    public string ToJson(bool indented = true) =>
        JsonSerializer.Serialize(this, indented ? Indented : Compact);

    // ---- Graph helpers -----------------------------------------------------

    public DspNode? FindNode(string nodeId) =>
        AudioGraph.Nodes.FirstOrDefault(n => n.NodeId == nodeId);

    public DspNode? FindNodeByFenderId(string FenderId) =>
        AudioGraph.Nodes.FirstOrDefault(n => n.FenderId == FenderId);

    public DspNode? Stomp  => FindNode("stomp");
    public DspNode? Mod    => FindNode("mod");
    public DspNode? Amp    => FindNode("amp");
    public DspNode? Delay  => FindNode("delay");
    public DspNode? Reverb => FindNode("reverb");

    /// <summary>
    /// Nodes in signal order, from the preset input to the preset output.
    /// Follows channel 0 only, since channel 1 mirrors it. Stops if the graph loops.
    /// </summary>
    public IReadOnlyList<DspNode> GetSignalChain()
    {
        var chain = new List<DspNode>();
        var visited = new HashSet<string> { RootNodeId };
        string current = RootNodeId;

        while (true)
        {
            var next = AudioGraph.Connections
                .FirstOrDefault(c => c.Source.NodeId == current && c.Source.Index == 0);

            if (next is null) break;

            string destId = next.Destination.NodeId;
            if (destId == RootNodeId || !visited.Add(destId)) break;

            var node = FindNode(destId);
            if (node is null) break;

            chain.Add(node);
            current = destId;
        }

        return chain;
    }

    /// <summary>Short human-readable summary Table for the TUI.</summary>
    public Table ToSummaryTable()
    {
        var table = new Table()
            .Border(TableBorder.None)
            .BorderColor(Color.Grey)
            .HideHeaders()
            .AddColumn(new TableColumn("NodeId").Padding(0, 0))
            .AddColumn(new TableColumn("FenderId").Padding(0, 0));

        foreach (var node in GetSignalChain())
        {
            string nodeId = string.IsNullOrEmpty(node.NodeId) ? "_" : node.NodeId;
            string color = node.Bypass == true ? "grey" : "white";
            string fenderId = $"[{color}]{node.FenderId}[/]";
            table.AddRow(nodeId, fenderId);
        }

        return table;
    }
}

public sealed class PresetInfo
{
    [JsonPropertyName("displayName")]        public string DisplayName { get; set; } = "";
    [JsonPropertyName("preset_id")]          public string PresetId { get; set; } = "";
    [JsonPropertyName("author")]             public string Author { get; set; } = "";
    [JsonPropertyName("source_id")]          public string SourceId { get; set; } = "";
    [JsonPropertyName("timestamp")]          public long Timestamp { get; set; }
    [JsonPropertyName("created_at")]         public long CreatedAt { get; set; }
    [JsonPropertyName("product_id")]         public string ProductId { get; set; } = "";
    [JsonPropertyName("is_factory_default")] public bool IsFactoryDefault { get; set; }
    [JsonPropertyName("bpm")]                public double Bpm { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class AudioGraph
{
    [JsonPropertyName("nodes")]       public List<DspNode> Nodes { get; set; } = [];
    [JsonPropertyName("connections")] public List<Connection> Connections { get; set; } = [];

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class DspNode
{
    [JsonPropertyName("nodeId")]   public string NodeId { get; set; } = "";
    [JsonPropertyName("nodeType")] public string NodeType { get; set; } = "dspUnit";

    /// <summary>Effect or amp model, e.g. "DUBS_Twin65" or "DUBS_Passthru".</summary>
    [JsonPropertyName("FenderId")] public string FenderId { get; set; } = "";

    /// <summary>
    /// Parameters vary by FenderId (bool, string, or number), so they are kept as raw JSON nodes.
    /// </summary>
    [JsonPropertyName("dspUnitParameters")]
    public Dictionary<string, JsonNode?> Parameters { get; set; } = new();

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public bool IsPassthru => FenderId == "DUBS_Passthru";
    [JsonIgnore] public bool? Bypass => GetBool("bypass");

    public double? GetDouble(string name) =>
        Parameters.TryGetValue(name, out var n) && n is JsonValue v && v.TryGetValue<double>(out var d)
            ? d : null;

    public bool? GetBool(string name) =>
        Parameters.TryGetValue(name, out var n) && n is JsonValue v && v.TryGetValue<bool>(out var b)
            ? b : null;

    public string? GetString(string name) =>
        Parameters.TryGetValue(name, out var n) && n is JsonValue v && v.TryGetValue<string>(out var s)
            ? s : null;

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{NodeId}: {FenderId}");

        int width = Parameters.Count == 0 ? 0 : Parameters.Keys.Max(k => k.Length);

        foreach (var (name, value) in Parameters)
            sb.AppendLine($"  {name.PadRight(width)}  {FormatValue(value)}");

        return sb.ToString().TrimEnd();
    }

    private static readonly JsonSerializerOptions Options = new();

    public JsonNode ToJson() =>
    JsonSerializer.SerializeToNode(this, Options)
    ?? throw new InvalidOperationException("node serialized to null");

    private static string FormatValue(JsonNode? node) => node switch
    {
        null => "null",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<double>(out var d) =>
            d.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
        _ => node.ToJsonString()
    };

    public void Set(string name, double value) => Parameters[name] = JsonValue.Create(value);
    public void Set(string name, bool value)   => Parameters[name] = JsonValue.Create(value);
    public void Set(string name, string value) => Parameters[name] = JsonValue.Create(value);

    
}

/// <summary>
/// A directed link in the audio graph. In the JSON, "input" is the signal source
/// and "output" is where it goes. Source "preset" is the amp's audio input, and
/// destination "preset" is its audio output.
/// </summary>
public sealed class Connection
{
    [JsonPropertyName("input")]  public Endpoint Source { get; set; } = new();
    [JsonPropertyName("output")] public Endpoint Destination { get; set; } = new();

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class Endpoint
{
    [JsonPropertyName("nodeId")] public string NodeId { get; set; } = "";
    [JsonPropertyName("index")]  public int Index { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// Converts between the amp's normalized wire values and the numbers its display shows.
/// Only parameters listed here are converted; everything else is shown and sent as-is.
/// </summary>
public static class FenderScaling
{
    private readonly record struct Scale(Func<float, float> ToDisplay,
                                         Func<float, float> FromDisplay,
                                         string Unit = "");

    // 1..10 knob: display = 1 + 9 * wire (confirmed for feedback and wowLevel)
    private static readonly Scale Knob = new(w => 1f + 9f * w, d => (d - 1f) / 9f);

    // seconds <-> milliseconds (confirmed for dlyTime)
    private static readonly Scale Millis = new(w => w * 1000f, d => d / 1000f, " ms");

    // Keyed by parameter name. Add entries only after checking them against the amp's display.
    private static readonly Dictionary<string, Scale> Scales =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["feedback"] = Knob,
            ["wowLevel"] = Knob,
            ["wetLvl"]   = Knob,
            ["dlyTime"]  = Millis,
        };

    public static bool HasScale(string param) => Scales.ContainsKey(param);

    public static float ToDisplay(string param, float wire) =>
        Scales.TryGetValue(param, out var s) ? s.ToDisplay(wire) : wire;

    public static float FromDisplay(string param, float display) =>
        Scales.TryGetValue(param, out var s) ? s.FromDisplay(display) : display;

    public static string Unit(string param) =>
        Scales.TryGetValue(param, out var s) ? s.Unit : "";
}