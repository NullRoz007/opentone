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

    [CommandOption("--info")]
    [Description("Show everything that can be read from the image: identity, memory layout, USB, presets, models, images, boot patches and build details.")]
    public bool Info { get; init; }

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
        Ui.FirmwareSummary(settings.Input, firmware);

        var patcher = new FirmwarePatcher(firmware.Payload);
        if (!settings.Info)     // --info shows them in its presets section
            Ui.Messages("Warnings", patcher.Problems, Ui.Level.Warning);

        if (settings.Info) PrintInfo(FirmwareInfo.Read(firmware.Payload));
        if (settings.List && !settings.Info) PrintList(patcher);
        if (settings.Show != null) PrintShow(patcher, settings.Show);

        var notes = new List<string>();
        void Note(string? note) { if (note != null) notes.Add(note); }

        // Whole presets first, so --rename and --set can adjust them afterwards.
        foreach (var (target, preset, source) in ReadPulledPresets(settings))
            foreach (var n in patcher.ImportPreset(patcher.GetPreset(target), preset, source)) Note($"{source}: {n}");

        foreach (var spec in settings.Import)
        {
            var (target, file) = Split(spec, '=', "--import", "FWSLOT=FILE");
            var json = JsonNode.Parse(File.ReadAllText(file)) ?? throw new ArgumentException($"{file} is empty.");
            foreach (var n in patcher.ImportPreset(patcher.GetPreset(target), json, Path.GetFileName(file))) Note($"{Path.GetFileName(file)}: {n}");
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
            Note(patcher.SetPresetParam(patcher.GetPreset(slot), node, param, value) is { } n ? $"preset {slot} {node}.{param}: {n}" : null);
        }

        foreach (var spec in settings.ModelDefault)
        {
            var (target, value) = Split(spec, '=', "--model-default", "MODEL.PARAM=VALUE");
            int dot = target.LastIndexOf('.');
            if (dot <= 0) throw new ArgumentException($"--model-default expects MODEL.PARAM=VALUE, got '{spec}'.");
            Note(patcher.SetModelDefault(patcher.GetModel(target[..dot]), target[(dot + 1)..], value) is { } n ? $"{target}: {n}" : null);
        }

        // Export before replacing, so --export-image always saves what the input contains.
        if (settings.ExportImage.Length > 0)
        {
            var exported = Ui.Table("Exported images", "Image", "Size", "File");
            foreach (var spec in settings.ExportImage)
            {
                var (name, file) = Split(spec, '=', "--export-image", "NAME=FILE");
                var img = patcher.GetImage(name);
                Bmp.Write(file, img.Width, img.Height, patcher.ReadImage(img));
                exported.AddRow(Markup.Escape(img.Name), $"{img.Width}x{img.Height}", $"[bold]{Markup.Escape(file)}[/]");
            }
            AnsiConsole.Write(exported);
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

        Ui.Messages("Notes", notes, Ui.Level.Note);

        if (patcher.Changes.Count > 0)
        {
            var changes = Ui.Table("Changes", "#", "Change")
                .Caption($"[green]all checks passed[/], new CRC 0x{crc:X8}");
            for (int i = 0; i < patcher.Changes.Count; i++)
                changes.AddRow((i + 1).ToString(), Markup.Escape(patcher.Changes[i]));
            AnsiConsole.Write(changes);
        }

        if (settings.Output == null || settings.DryRun)
        {
            if (patcher.Changes.Count > 0 || settings.DryRun)
                Ui.Message("Output", settings.DryRun ? "Nothing written (--dry-run)." : "Nothing written (no output path given).", Ui.Level.Note);
            return 0;
        }

        if (Path.GetFullPath(settings.Output).Equals(Path.GetFullPath(settings.Input), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Refusing to overwrite the input file. Choose a different output path.");

        File.WriteAllBytes(settings.Output, result);
        AnsiConsole.Write(Ui.KeyValues("Output")
            .Row("Written to", $"[bold]{Markup.Escape(settings.Output)}[/]")
            .Row("Size", $"{result.Length} bytes")
            .Row("CRC-32", $"0x{crc:X8} [green]valid[/]"));
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

        var read = Ui.Table("Read from amp", "Source", "Name", "Firmware slot");

        foreach (var r in requests)
        {
            JsonNode preset = (r.Current ? amp.GetCurrentPreset() : amp.ReadPreset(int.Parse(r.From)))
                ?? throw new IOException($"The amp did not return preset {r.From}.");
            string source = r.Current ? "amp current preset" : $"amp slot {r.From}";
            read.AddRow(Markup.Escape(source), $"[bold]{Markup.Escape((string?)preset["info"]?["displayName"] ?? "?")}[/]", Markup.Escape(r.To));
            result.Add((r.To, preset, source));
        }
        AnsiConsole.Write(read);
        return result;
    }

    private static (string, string) Split(string spec, char separator, string option, string format)
    {
        int i = spec.IndexOf(separator);
        if (i <= 0 || i == spec.Length - 1) throw new ArgumentException($"{option} expects {format}, got '{spec}'.");
        return (spec[..i].Trim(), spec[(i + 1)..]);
    }

    private static string Hex(uint v) => $"0x{v:X8}";

    private static void PrintInfo(FirmwareInfo info)
    {
        var p = info.Patcher;

        Ui.Section("Image");
        AnsiConsole.Write(Ui.KeyValues()
            .Row("Size", $"{info.Size} bytes ({info.Size / 32} update frames of 32 bytes)")
            .Row("CRC-32 (STM32)", $"0x{info.CrcCalculated:X8} [green]valid[/]")
            .Row("SHA-256", info.Sha256)
            .Row("Identified as", info.KnownAs != null ? $"[green]{Markup.Escape(info.KnownAs)}[/]" : "[yellow]not a known official image (modified or unknown build)[/]")
            .Row("Load address", $"{Hex(info.LoadAddress)} - {Hex(info.LoadAddress + (uint)info.Size)}"));

        Ui.Section("Identity");
        var t = Ui.KeyValues().Row("Version", $"[bold]{Markup.Escape(info.Version)}[/]");
        if (info.ProductId != null) t.Row("Preset product_id", Markup.Escape(info.ProductId));
        if (info.ProductFamily != null) t.Row("Product family", Markup.Escape(info.ProductFamily));
        if (info.ProductNames.Count > 0) t.Row("Product strings", Markup.Escape(string.Join(", ", info.ProductNames)));
        AnsiConsole.Write(t);

        Ui.Section("Memory layout");
        if (info.Layout is { } l)
        {
            t = Ui.KeyValues()
                .Row("Initial stack pointer", Hex(l.InitialStackPointer))
                .Row("Reset handler", Hex(l.ResetHandler & ~1u))
                .Row(".data", $"{Hex(l.DataStart)} - {Hex(l.DataEnd)} ({l.DataEnd - l.DataStart} bytes, loaded from {Hex(l.DataLoadAddress)})")
                .Row(".bss", $"{Hex(l.BssStart)} - {Hex(l.BssEnd)} ({l.BssEnd - l.BssStart} bytes)");
            string[] callNames = ["SystemInit", "C++ constructors", "main"];
            for (int i = 0; i < l.StartupCalls.Count; i++)
                t.Row(i < callNames.Length ? $"{callNames[i]} (inferred)" : "call", Hex(l.StartupCalls[i]));
            AnsiConsole.Write(t);
        }
        else Ui.Message("Memory layout", "Reset handler not recognised.", Ui.Level.Note);

        Ui.Section("USB");
        t = Ui.KeyValues();
        if (info.Usb is { } u)
        {
            t.Row("Device descriptor", $"at {Hex(u.Address)}")
             .Row("VID:PID", $"{u.VendorId:X4}:{u.ProductId:X4}")
             .Row("bcdUSB / bcdDevice", $"{u.BcdUsb >> 8}.{u.BcdUsb & 0xFF:X2} / {u.BcdDevice >> 8}.{u.BcdDevice & 0xFF:X2}")
             .Row("EP0 max packet", $"{u.MaxPacketSize0} bytes, {u.Configurations} configuration(s)");
        }
        else t.Row("Device descriptor", "[grey]not found[/]");
        if (info.UsbRequests is { } reqs)
        {
            t.Row("Request dispatcher", $"at {Hex(info.UsbDispatcher!.Value)}, {reqs.Count} request types handled")
             .Row("Handled fields", Markup.Escape(string.Join(", ", reqs.Select(r => r.Name != null ? $"{r.Field} ({r.Name})" : r.Field.ToString()))));
        }
        else t.Row("Request dispatcher", "[grey]not recognised[/]");
        AnsiConsole.Write(t);

        Ui.Section("Boot and data flash");
        t = Ui.KeyValues();
        foreach (var (name, state) in info.BootPatches)
            t.Row(name, state switch { "stock" => "[green]stock[/]", "applied" => "[yellow]applied[/]", _ => "[grey]not found[/]" });
        if (info.DataFlashMarkers.Count > 0) t.Row("Data flash markers", Markup.Escape(string.Join(", ", info.DataFlashMarkers)));
        AnsiConsole.Write(t);

        Ui.Section("Factory presets");
        Ui.Messages("Warnings", p.Problems, Ui.Level.Warning);
        AnsiConsole.Write(PresetTable(p, null));

        Ui.Section("Models");
        var models = Ui.Table($"{p.Models.Count} defined", "Menu", "Name on amp", "Model", "Params", "Defined");
        foreach (var e in info.Catalogue)
        {
            int parameters = e.Defined ? p.GetParameters(p.Models[e.FenderId]).Count : 0;
            models.AddRow(Markup.Escape(e.CategoryMenuName), Markup.Escape(e.MenuName), Markup.Escape(e.FenderId.Replace("DUBS_", "")),
                e.Defined ? parameters.ToString() : "", e.Defined ? "[green]yes[/]" : "[red]no[/]");
        }
        if (info.ModelsNotInCatalogue.Count > 0)
            models.Caption($"Defined but not in any menu: {Markup.Escape(string.Join(", ", info.ModelsNotInCatalogue.Select(m => m.Replace("DUBS_", ""))))}");
        AnsiConsole.Write(models);

        Ui.Section("Images");
        if (info.Images.Count > 0)
        {
            var images = Ui.Table(null, "Image", "Size", "Address", "State", "Description");
            foreach (var img in info.Images)
                images.AddRow(Markup.Escape(img.Image.Name), $"{img.Image.Width}x{img.Image.Height}", Hex(FirmwarePatcher.LoadAddress + (uint)img.Image.Offset),
                    img.Factory ? "[green]factory[/]" : "[yellow]modified[/]", $"[grey]{Markup.Escape(img.Image.Description)}[/]");
            AnsiConsole.Write(images);
        }
        else Ui.Message("Images", "None recognised.", Ui.Level.Note);

        Ui.Section("Build");
        t = Ui.KeyValues();
        if (info.BuildAgentWorkDir != null) t.Row("Build agent", $"TeamCity, work directory {Markup.Escape(info.BuildAgentWorkDir)}");
        t.Row("Source files named", info.SourceFiles.Count.ToString());
        AnsiConsole.Write(t);

        // Group by top two folders; files directly under a top folder (e.g. Src/adc.c) share one group.
        static string Folder(string f) { var parts = f.Split('/'); return string.Join('/', parts.Take(Math.Min(2, parts.Length - 1))); }
        var sources = Ui.Table(null, "Folder", "Files", "Names");
        foreach (var group in info.SourceFiles.GroupBy(Folder).OrderBy(x => x.Key, StringComparer.Ordinal))
            sources.AddRow(Markup.Escape(group.Key + "/"), group.Count().ToString(), $"[grey]{Markup.Escape(string.Join(", ", group.Select(f => Path.GetFileName(f))))}[/]");
        AnsiConsole.Write(sources);
    }

    private static Table PresetTable(FirmwarePatcher patcher, string? title)
    {
        var table = Ui.Table(title, "Slot", "Name", "Amp", "Bytes", "Spare");
        foreach (var p in patcher.Presets.Append(patcher.EmptyPreset))
        {
            var preset = FenderPreset.Parse(patcher.GetJson(p));
            string slot = p.Kind == FirmwareJsonKind.EmptyPreset ? "empty (31-60)" : p.Slot.ToString();
            string amp = preset.Amp?.FenderId?.Replace("DUBS_", "") ?? "";
            table.AddRow(slot, $"[bold]{Markup.Escape(p.Label)}[/]", Markup.Escape(amp), p.Length.ToString(), (p.Capacity - p.Length).ToString());
        }
        return table;
    }

    private static void PrintList(FirmwarePatcher patcher)
    {
        AnsiConsole.Write(Ui.KeyValues("Image contents")
            .Row("Version", $"[bold]{Markup.Escape(patcher.Version)}[/]")
            .Row("Models", Markup.Escape(string.Join(", ", patcher.Models.Keys.Select(k => k.Replace("DUBS_", "")).Order())))
            .Row("Images", Markup.Escape(string.Join(", ", patcher.Images.Select(i => $"{i.Name} ({i.Width}x{i.Height})")))));
        AnsiConsole.Write(PresetTable(patcher, "Factory presets"));
    }

    private static void PrintShow(FirmwarePatcher patcher, string target)
    {
        var entry = target.Equals("empty", StringComparison.OrdinalIgnoreCase) || int.TryParse(target, out _)
            ? patcher.GetPreset(target)
            : patcher.GetModel(target);

        var table = Ui.Table($"{entry.Label.TrimEnd()} ({entry.Kind}, {entry.Length} bytes, {entry.Capacity - entry.Length} spare)",
            "Node", "Model", "Param", "Value", "Allowed");
        foreach (var p in patcher.GetParameters(entry))
            table.AddRow(Markup.Escape(p.NodeId), Markup.Escape(p.FenderId.Replace("DUBS_", "")), Markup.Escape(p.Param),
                Markup.Escape(p.Value), $"[grey]{Markup.Escape(p.Rule.Describe())}[/]");
        AnsiConsole.Write(table);
    }
}
