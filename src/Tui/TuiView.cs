using OpenTone;
using Spectre.Console;
using Spectre.Console.Rendering;

public sealed class TuiView
{
    private const int InfoWidth = 60;

    private static int ContentHeight => Math.Max(1, Console.WindowHeight - 1);
    private static int OutputHeight  => Math.Max(1, ContentHeight - 4);

    public void PositionPrompt()
    {
        Console.SetCursorPosition(7, Console.WindowHeight - 1);
        Console.CursorVisible = true;
    }

    public void Draw(FenderAmp amp, string output)
    {
        var info = new Panel(BuildInfoGrid(amp))
            .Header("[blue]Device Info[/]")
            .BorderColor(Color.Grey)
            .Padding(0, 0)
            .Expand();

        var signalChain = new Panel(BuildSignalChain(amp))
            .Header("[blue]Signal Chain[/]")
            .BorderColor(Color.Grey)
            .Padding(1, 0)
            .Expand();

        var visible = string.Join(
            "\n",
            output.Replace("\r\n", "\n").Split('\n').Take(OutputHeight));

        var outputPanel = new Panel(new Text(visible))
            .Header("[grey]Output[/]")
            .BorderColor(Color.Grey)
            .Padding(1, 0)
            .Expand();

        var layout = new Layout("Root")
            .SplitRows(
                new Layout("Content").Size(ContentHeight),
                new Layout("Prompt").Size(1));

        layout["Content"].SplitColumns(
            new Layout("Left").Size(InfoWidth),
            new Layout("Output"));

        layout["Content"]["Left"].SplitRows(
            new Layout("Info"),
            new Layout("SignalChain"));

        layout["Content"]["Left"]["Info"].Update(info);
        layout["Content"]["Left"]["SignalChain"].Update(signalChain);
        layout["Content"]["Output"].Update(outputPanel);
        layout["Prompt"].Update(new Markup("[grey]tone>[/]"));

        AnsiConsole.Cursor.Hide();
        AnsiConsole.Clear();
        AnsiConsole.Write(layout);
    }

    private static Grid BuildInfoGrid(FenderAmp amp) {
        var node = amp.GetCurrentPreset();
        FenderPreset? preset = null;
        if(node != null) preset = FenderPreset.FromNode(node);


        return new Grid()
        .AddColumn()
        .AddColumn()
        .AddRow(new Markup("[grey]Amplifier[/]"),   new Text(amp.GetDeviceName() ?? "Unknown"))
        .AddRow(new Markup("[grey]Presets[/]"),     new Text(amp.PresetSlotCount.ToString()))
        .AddRow(new Markup("[grey]Current[/]"),     new Text(preset?.Info.DisplayName.Trim() ?? "Unknown"))
        .AddRow(new Markup(""), BuildPresetTable(preset).Width(100) ?? new Table());
    }

    public static Table BuildPresetTable(FenderPreset? preset)
    {
        var table = new Table()
            .Border(TableBorder.None)
            .BorderColor(Color.Grey)
            .HideHeaders()
            .AddColumn(new TableColumn("NodeId").Padding(0, 0))
            .AddColumn(new TableColumn("FenderId").Padding(0, 0));
        if(preset == null) return table;

        foreach (var node in preset.GetSignalChain())
        {
            string nodeId = string.IsNullOrEmpty(node.NodeId) ? "_" : node.NodeId;
             bool off = node.IsPassthru || node.Bypass == true;

            string color = off ? "grey" : "green";
            string fenderId = $"[{color}]{node.FenderId}[/]";
            table.AddRow(nodeId, fenderId);
        }

        return table;
    }

    private static IRenderable BuildSignalChain(FenderAmp amp)
    {
        var node = amp.GetCurrentPreset();
        if (node == null) return new Markup("[grey]No preset[/]");

        var chain = FenderPreset.FromNode(node).GetSignalChain();
        var rows = new List<IRenderable>();

        for (int i = 0; i < chain.Count; i++)
        {
            var n = chain[i];
            bool off = n.IsPassthru || n.Bypass == true;

            string color = off ? "grey" : "green";
            string model = n.IsPassthru
                ? "(empty)"
                : n.FenderId;

            rows.Add(new Markup($"{Markup.Escape(n.NodeId),-7} [{color}]{model}[/]"));

            if (i < chain.Count - 1)
                rows.Add(new Markup("[grey]  ↓[/]"));
        }

        return new Rows(rows);
    }
}