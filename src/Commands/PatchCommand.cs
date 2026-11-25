using System.ComponentModel;
using System.Text.Json.Nodes;
using OpenTone;
using Spectre.Console;
using Spectre.Console.Cli;
using static OpenTone.FenderFirmware;

public class PatchSettings : CommandSettings
{
    [CommandArgument(0, "<input>")]
    [Description("A PcapNG capture of an official firmware update, or a firmware .bin image.")]
    public required string Input { get; init; }

    [CommandArgument(1, "[output]")]
    [Description("Path to save the patched firmware. Omit to only inspect.")]
    public string? Output { get; init; }

    [CommandOption("-d|--device")]
    [Description("Amp to read presets from with --pull (default mustang-lt25). Not needed otherwise.")]
    public string? Device { get; init; }

    [CommandOption("--pull <AMPSLOT=FWSLOT>")]
    [Description("Read a preset from the amp (1-60 or 'current') and write it to a firmware slot (1-30 or 'empty'). AMPSLOT alone uses the same slot. Repeatable.")]
    public string[] Pull { get; init; } = [];

    [CommandOption("--import <FWSLOT=FILE>")]
    [Description("Write a preset JSON file (e.g. saved from 'get preset') to a firmware slot (1-30 or 'empty'). Repeatable.")]
    public string[] Import { get; init; } = [];

    [CommandOption("--list")]
    [Description("List the factory presets, the version and the available models.")]
    public bool List { get; init; }

    [CommandOption("--show <TARGET>")]
    [Description("Show the parameters and allowed values of a preset (1-30, empty) or a model (e.g. SuperSonic).")]
    public string? Show { get; init; }

    [CommandOption("--rename <SLOT=NAME>")]
    [Description("Rename a preset, e.g. 2=MY LEAD or 2=MY|LEAD (two 8-character halves). Repeatable.")]
    public string[] Rename { get; init; } = [];

    [CommandOption("--set <SLOT:NODE.PARAM=VALUE>")]
    [Description("Change a preset parameter, e.g. 2:amp.gain=0.8 or empty:reverb.bypass=true. Repeatable.")]
    public string[] Set { get; init; } = [];

    [CommandOption("--model-default <MODEL.PARAM=VALUE>")]
    [Description("Change a model's default parameter, e.g. SuperSonic.gain=0.6. Repeatable.")]
    public string[] ModelDefault { get; init; } = [];

    [CommandOption("--fw-version <VERSION>")]
    [Description("Change the version string (exactly 5 characters, e.g. 2.1.5).")]
    public string? FirmwareVersion { get; init; }

    [CommandOption("--force-restore")]
    [Description("Boot patch: restore all presets on every boot. Flash stock firmware again afterwards.")]
    public bool ForceRestore { get; init; }

    [CommandOption("--reformat-flash")]
    [Description("Boot patch: erase the amp's data flash (presets and settings) on the next boot.")]
    public bool ReformatFlash { get; init; }

    [CommandOption("--export-image <NAME=FILE>")]
    [Description("Save a firmware image (splash, tone) as a 24-bit BMP to edit. Repeatable.")]
    public string[] ExportImage { get; init; } = [];

    [CommandOption("--image <NAME=FILE>")]
    [Description("Replace a firmware image (splash, tone) with a 24- or 32-bit BMP of exactly the same size. Repeatable.")]
    public string[] Image { get; init; } = [];

    [CommandOption("--dry-run")]
    [Description("Apply and validate the changes, but don't write the output.")]
    public bool DryRun { get; init; }
}

public class PatchCommand : Command<PatchSettings>
{
    private static readonly byte[] PcapNgMagic = [0x0A, 0x0D, 0x0D, 0x0A];

    public override int Execute(CommandContext context, PatchSettings settings, CancellationToken cancellation)
    {
        var firmware = Load(settings.Input);
        AnsiConsole.MarkupLine($"Loaded [bold]{Markup.Escape(settings.Input)}[/]: {firmware.Size} bytes, CRC 0x{firmware.CheckSumCalculated:X8} [green]valid[/]");

        var patcher = new FirmwarePatcher(firmware.Payload);
        foreach (var problem in patcher.Problems) AnsiConsole.MarkupLine($"[red]warning:[/] {Markup.Escape(problem)}");

        if (settings.List) PrintList(patcher);
        if (settings.Show != null) PrintShow(patcher, settings.Show);

        // Whole presets first, so --rename and --set can adjust them afterwards.
        foreach (var (target, preset, source) in ReadPulledPresets(settings))
            foreach (var n in patcher.ImportPreset(patcher.GetPreset(target), preset, source)) Note(n);

        foreach (var spec in settings.Import)
        {
            var (target, file) = Split(spec, '=', "--import", "FWSLOT=FILE");
            var json = JsonNode.Parse(File.ReadAllText(file)) ?? throw new ArgumentException($"{file} is empty.");
            foreach (var n in patcher.ImportPreset(patcher.GetPreset(target), json, Path.GetFileName(file))) Note(n);
        }

        foreach (var spec in settings.Rename)
        {
            var (slot, name) = Split(spec, '=', "--rename", "SLOT=NAME");
            patcher.SetPresetName(patcher.GetPreset(slot), FirmwarePatcher.FormatPresetName(name));
        }

        foreach (var spec in settings.Set)
        {
            var (slot, rest) = Split(spec, ':', "--set", "SLOT:NODE.PARAM=VALUE");
            var (target, value) = Split(rest, '=', "--set", "SLOT:NODE.PARAM=VALUE");
            var (node, param) = Split(target, '.', "--set", "SLOT:NODE.PARAM=VALUE");
            Note(patcher.SetPresetParam(patcher.GetPreset(slot), node, param, value));
        }

        foreach (var spec in settings.ModelDefault)
        {
            var (target, value) = Split(spec, '=', "--model-default", "MODEL.PARAM=VALUE");
            int dot = target.LastIndexOf('.');
            if (dot <= 0) throw new ArgumentException($"--model-default expects MODEL.PARAM=VALUE, got '{spec}'.");
            Note(patcher.SetModelDefault(patcher.GetModel(target[..dot]), target[(dot + 1)..], value));
        }

        // Export before replacing, so --export-image always saves what the input contains.
        foreach (var spec in settings.ExportImage)
        {
            var (name, file) = Split(spec, '=', "--export-image", "NAME=FILE");
            var img = patcher.GetImage(name);
            Bmp.Write(file, img.Width, img.Height, patcher.ReadImage(img));
            AnsiConsole.MarkupLine($"Saved image {Markup.Escape(img.Name)} ({img.Width}x{img.Height}) to [bold]{Markup.Escape(file)}[/]");
        }

        foreach (var spec in settings.Image)
        {
            var (name, file) = Split(spec, '=', "--image", "NAME=FILE");
            var (w, h, px) = Bmp.Read(file);
            patcher.WriteImage(patcher.GetImage(name), w, h, px, Path.GetFileName(file));
        }

        if (settings.FirmwareVersion != null) patcher.SetVersion(settings.FirmwareVersion);
        if (settings.ForceRestore) patcher.Apply(BootPatch.ForceRestore);
        if (settings.ReformatFlash) patcher.Apply(BootPatch.ReformatFlash);

        byte[] result = patcher.Changes.Count > 0 ? patcher.Build() : firmware.Payload;
        ValidateFirmware(result, out _, out uint crc);

        if (patcher.Changes.Count > 0)
        {
            AnsiConsole.MarkupLine($"[bold]{patcher.Changes.Count} change(s)[/], all checks passed, new CRC 0x{crc:X8}:");
            foreach (var c in patcher.Changes) AnsiConsole.MarkupLine($"  [green]+[/] {Markup.Escape(c)}");
        }

        if (settings.Output == null || settings.DryRun)
        {
            if (patcher.Changes.Count > 0 || settings.DryRun) AnsiConsole.MarkupLine("[grey]Nothing written (no output path, or --dry-run).[/]");
            return 0;
        }

        if (Path.GetFullPath(settings.Output).Equals(Path.GetFullPath(settings.Input), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Refusing to overwrite the input file. Choose a different output path.");

        File.WriteAllBytes(settings.Output, result);
        AnsiConsole.MarkupLine($"Wrote {result.Length} bytes to [bold]{Markup.Escape(settings.Output)}[/]");
        return 0;
    }

    /// <summary>True if the file starts with the PcapNG section header magic.</summary>
    public static bool IsCapture(string path)
    {
        byte[] head = new byte[4];
        using var f = File.OpenRead(path);
        return f.Read(head, 0, 4) == 4 && head.AsSpan().SequenceEqual(PcapNgMagic);
    }

    private static FenderFirmware Load(string path) =>
        IsCapture(path)
            ? CreateFromCapture(path)
            : new FenderFirmware(File.ReadAllBytes(path));      // throws if the CRC is invalid

    /// <summary>Connects to the amp once and reads every preset named by --pull. Read-only.</summary>
    private static List<(string Target, JsonNode Preset, string Source)> ReadPulledPresets(PatchSettings settings)
    {
        var result = new List<(string, JsonNode, string)>();
        if (settings.Pull.Length == 0) return result;

        var requests = settings.Pull.Select(spec =>
        {
            int eq = spec.IndexOf('=');
            string from = (eq < 0 ? spec : spec[..eq]).Trim();
            string to = eq < 0 ? from : spec[(eq + 1)..].Trim();
            bool current = from.Equals("current", StringComparison.OrdinalIgnoreCase);
            if (!current && !(int.TryParse(from, out int s) && s >= 1 && s <= 60))
                throw new ArgumentException($"--pull: amp slot must be 1-60 or 'current', got '{from}'.");
            if (current && eq < 0) throw new ArgumentException("--pull current needs a firmware slot, e.g. current=5.");
            bool validTarget = to.Equals("empty", StringComparison.OrdinalIgnoreCase)
                || (int.TryParse(to, out int t) && t >= 1 && t <= FirmwarePatcher.FactoryPresetCount);
            if (!validTarget)
                throw new ArgumentException($"--pull {spec}: firmware slot must be 1-{FirmwarePatcher.FactoryPresetCount} or 'empty'" +
                    (eq < 0 ? $" (amp slot {from} has no firmware slot of the same number; use {from}=FWSLOT)." : "."));
            return (From: from, To: to, Current: current);
        }).ToList();

        string device = settings.Device ?? FenderDevices.LT25;
        if (device != FenderDevices.LT25) throw new ArgumentException($"--pull needs the amp in normal mode ({FenderDevices.LT25}), not '{device}'.");

        using var amp = FenderAmp.Connect(device);
        if (amp.Hello() == null) throw new IOException("No reply to hello. Close Fender Tone and check the amp is in normal mode.");

        foreach (var r in requests)
        {
            JsonNode preset = (r.Current ? amp.GetCurrentPreset() : amp.ReadPreset(int.Parse(r.From)))
                ?? throw new IOException($"The amp did not return preset {r.From}.");
            string source = r.Current ? "amp current preset" : $"amp slot {r.From}";
            AnsiConsole.MarkupLine($"Read {Markup.Escape(source)}: [bold]{Markup.Escape((string?)preset["info"]?["displayName"] ?? "?")}[/]");
            result.Add((r.To, preset, source));
        }
        return result;
    }

    private static (string, string) Split(string spec, char separator, string option, string format)
    {
        int i = spec.IndexOf(separator);
        if (i <= 0 || i == spec.Length - 1) throw new ArgumentException($"{option} expects {format}, got '{spec}'.");
        return (spec[..i].Trim(), spec[(i + 1)..]);
    }

    private static void Note(string? note)
    {
        if (note != null) AnsiConsole.MarkupLine($"  [yellow]note:[/] {Markup.Escape(note)}");
    }

    private static void PrintList(FirmwarePatcher patcher)
    {
        AnsiConsole.MarkupLine($"Version [bold]{Markup.Escape(patcher.Version)}[/]");

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Slot").AddColumn("Name").AddColumn("Amp")
            .AddColumn(new TableColumn("Bytes").RightAligned()).AddColumn(new TableColumn("Spare").RightAligned());
        foreach (var p in patcher.Presets.Append(patcher.EmptyPreset))
        {
            var preset = FenderPreset.Parse(patcher.GetJson(p));
            string slot = p.Kind == FirmwareJsonKind.EmptyPreset ? "empty (31-60)" : p.Slot.ToString();
            string amp = preset.Amp?.FenderId?.Replace("DUBS_", "") ?? "";
            table.AddRow(slot, $"[bold]{Markup.Escape(p.Label)}[/]", Markup.Escape(amp), p.Length.ToString(), (p.Capacity - p.Length).ToString());
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]Models: {Markup.Escape(string.Join(", ", patcher.Models.Keys.Select(k => k.Replace("DUBS_", "")).Order()))}[/]");
        foreach (var img in patcher.Images)
            AnsiConsole.MarkupLine($"[grey]Image {Markup.Escape(img.Name)}: {img.Width}x{img.Height} RGB565 - {Markup.Escape(img.Description)}[/]");
    }

    private static void PrintShow(FirmwarePatcher patcher, string target)
    {
        var entry = target.Equals("empty", StringComparison.OrdinalIgnoreCase) || int.TryParse(target, out _)
            ? patcher.GetPreset(target)
            : patcher.GetModel(target);

        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(entry.Label)}[/] ({entry.Kind}, {entry.Length} bytes, {entry.Capacity - entry.Length} spare)");
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Node").AddColumn("Model").AddColumn("Param").AddColumn("Value").AddColumn("Allowed");
        foreach (var p in patcher.GetParameters(entry))
            table.AddRow(Markup.Escape(p.NodeId), Markup.Escape(p.FenderId.Replace("DUBS_", "")), Markup.Escape(p.Param),
                Markup.Escape(p.Value), $"[grey]{Markup.Escape(p.Rule.Describe())}[/]");
        AnsiConsole.Write(table);
    }
}
