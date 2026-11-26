using System.Text.Json.Nodes;
using OpenTone;
using Spectre.Console;
using Spectre.Console.Json;

/// <summary>
/// Shared console building blocks, so every command formats output the same way:
/// rounded grey-bordered tables, key/value tables for summaries, panels for JSON and messages.
/// </summary>
public static class Ui
{
    public enum Level { Info, Note, Success, Warning, Error }
    private readonly static TableBorder TableBorderStyle = TableBorder.Rounded;
    private readonly static BoxBorder PanelBorderStyle = BoxBorder.Rounded;
    private static Color BorderColor(Level level) => level switch
    {
        Level.Success => Color.Green,
        Level.Warning => Color.Yellow,
        Level.Error => Color.Red,
        _ => Color.Grey,
    };

    /// <summary>
    /// Creates a borderless table inside a titled live panel.
    /// </summary>
    public static (Table Table, Panel Panel) LiveTable(string title, params string[] columns)
    {
        var table = Table(columns)
            .Border(TableBorder.None)
            .HideHeaders();

        var panel = new Panel(table)
            .Header($"[bold]{Markup.Escape(title)}[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey);

        return (table, panel);
    }

    /// <summary>
    /// Runs an asynchronous live display.
    /// </summary>
    public static Task Live(
        Panel panel,
        Func<Spectre.Console.LiveDisplayContext, Task> action,
        bool autoClear = false)
    {
        return AnsiConsole.Live(panel)
            .AutoClear(autoClear)
            .StartAsync(action);
    }

    /// <summary>A data table with a header row.</summary>
    public static Table Table(params string[] columns)
    {
        var table = new Table().Border(TableBorderStyle).BorderColor(Color.Grey);
        foreach (var c in columns) table.AddColumn(new TableColumn($"[bold]{Markup.Escape(c)}[/]"));
        return table;
    }

    /// <summary>A two-column key/value table without a header row. Keys are plain text, values are markup.</summary>
    public static Table KeyValues()
    {
        var table = new Table().Border(TableBorderStyle).BorderColor(Color.Grey).HideHeaders()
            .AddColumn(new TableColumn("").NoWrap())
            .AddColumn(new TableColumn(""));
/*
        if (title != null)
            table.Title($"[bold]{Markup.Escape(title)}[/]", new Style(justify: Justify.Left));
*/
        return table;
    }
    /// <summary>Adds a key/value row; the key is escaped, the value is markup.</summary>
    public static Table Row(this Table table, string key, string valueMarkup)
    {
        table.AddRow($"[grey]{Markup.Escape(key)}[/]", valueMarkup);
        return table;
    }

    /// <summary>A section heading for long, multi-part output.</summary>
    public static void Section(string title) => AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(title)}[/]").LeftJustified().RuleStyle("grey"));

    /// <summary>A panel listing messages (warnings, notes, errors). Nothing is printed for an empty list.</summary>
    public static void Messages(string title, IEnumerable<string> messages, Level level)
    {
        var list = messages.ToList();
        if (list.Count == 0) return;
        var body = new Rows(list.Select(m => new Markup(Markup.Escape(m))));
        AnsiConsole.Write(new Panel(body)
            .Header($"[bold]{Markup.Escape(title)}[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(BorderColor(level))
            .Expand());
    }

    public static void Message(string title, string message, Level level) => Messages(title, [message], level);

    /// <summary>Pretty-printed JSON in a panel.</summary>
    public static void Json(string title, JsonNode? json)
    {
        AnsiConsole.Write(new Panel(new JsonText(json?.ToJsonString() ?? "null"))
            .Header($"[bold]{Markup.Escape(title)}[/]")
            .Border(PanelBorderStyle)
            .BorderColor(Color.Grey));
    }

    /// <summary>Shows a command result: JSON objects/arrays as a JSON panel, anything else as a key/value table.</summary>
    public static void Result(string title, string? result)
    {
        JsonNode? node = null;
        try { if (result != null) node = JsonNode.Parse(result); } catch (System.Text.Json.JsonException) { }

        if (node is JsonObject or JsonArray) Json(title, node);
        else AnsiConsole.Write(KeyValues().Row(title, Markup.Escape(result ?? "Unknown")));
    }

    /// <summary>The loaded image (and optionally the connected device), identical for flash and patch.</summary>
    public static void FirmwareSummary(string path, FenderFirmware firmware, FenderAmp? amp = null)
    {
        var table = KeyValues()
            .Row("File", $"[bold]{Markup.Escape(path)}[/]")
            .Row("Size", $"{firmware.Info.Size} bytes")
            .Row("CRC-32", $"0x{firmware.Info.CrcCalculated:X8} [green]valid[/]");

        if (amp != null)
        {
            var usb = amp.GetUsbDevice();
            table.Row("Device", $"[bold]{Markup.Escape(amp.GetDeviceName() ?? "Unknown device")}[/]")
                 .Row("VID:PID", $"{usb.VendorId:X4}:{usb.ProductId:X4}");
        }
        AnsiConsole.Write(table);
    }

    /// <summary>Renders an unhandled command error as a red panel (installed as the app's exception handler).</summary>
    public static int Error(Exception e)
    {
        var messages = new List<string>();
        for (Exception? x = e; x != null; x = x.InnerException)
            if (messages.Count == 0 || messages[^1] != x.Message) messages.Add(x.Message);
        Messages("Error", messages, Level.Error);
        return -1;
    }
}
