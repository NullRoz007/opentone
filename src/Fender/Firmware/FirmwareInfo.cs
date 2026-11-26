using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenTone;

/// <summary>
/// Everything that can be read from a firmware image without running it. Each section is derived from
/// the image's own data (vector table, reset handler, descriptors, JSON, code signatures); a section
/// that can't be recognised is left null instead of guessed.
/// </summary>
public sealed class FirmwareInfo
{
    /// <summary>SHA-256 of known official images.</summary>
    private static readonly Dictionary<string, string> KnownImages = new()
    {
        ["0f48ee0286c619aec2772949f1adeb6fdb0a206925a4a902d950d656a8c036ac"] = "official Mustang LT25 2.1.4",
    };

    /// <summary>SHA-256 of the factory pixel data of each known bitmap.</summary>
    private static readonly Dictionary<string, string> FactoryBitmaps = new()
    {
        ["splash"] = "7fed0f40dac82557bae1c2f9e8fda967c9aef05cd1b586b370c27cc532ebc126",
        ["tone"] = "084b2a3538dd5a70ad6e5bfd861d130a5a9c73ac7e526b310df6527ce1dd8ca5",
    };

    public sealed record MemoryLayout(
        uint InitialStackPointer, uint ResetHandler,
        uint DataLoadAddress, uint DataStart, uint DataEnd, uint BssStart, uint BssEnd,
        IReadOnlyList<uint> StartupCalls);

    public sealed record UsbDescriptor(uint Address, ushort BcdUsb, byte MaxPacketSize0, ushort VendorId, ushort ProductId,
        ushort BcdDevice, byte Configurations);

    public sealed record UsbRequest(int Field, string? Name, uint Handler);

    public sealed record CatalogueEntry(string Category, string CategoryMenuName, string FenderId, string MenuName, bool Defined);

    public sealed record ImageState(FirmwareImage Image, bool Factory);

    public int Size { get; private init; }
    public uint CrcCalculated { get; private init; }
    public uint CrcExpected { get; private init; }
    public string Sha256 { get; private init; } = "";
    public string? KnownAs { get; private init; }
    public uint LoadAddress => FirmwarePatcher.LoadAddress;

    public string Version { get; private init; } = "";
    public string? ProductId { get; private init; }
    public string? ProductFamily { get; private init; }
    public IReadOnlyList<string> ProductNames { get; private init; } = [];
    public string? BuildAgentWorkDir { get; private init; }
    public IReadOnlyList<string> SourceFiles { get; private init; } = [];

    public MemoryLayout? Layout { get; private init; }
    public UsbDescriptor? Usb { get; private init; }
    public IReadOnlyList<UsbRequest>? UsbRequests { get; private init; }
    public uint? UsbDispatcher { get; private init; }
    public IReadOnlyList<CatalogueEntry> Catalogue { get; private init; } = [];
    public IReadOnlyList<string> ModelsNotInCatalogue { get; private init; } = [];
    public IReadOnlyList<ImageState> Images { get; private init; } = [];
    public IReadOnlyList<(string Name, string State)> BootPatches { get; private init; } = [];
    public IReadOnlyList<string> DataFlashMarkers { get; private init; } = [];
    public FirmwarePatcher Patcher { get; private init; } = null!;

    public static FirmwareInfo Read(byte[] firmware)
    {
        FenderFirmware.ValidateFirmware(firmware, out uint expected, out uint calculated);
        var patcher = new FirmwarePatcher(firmware);            // also validates the CRC and structure
        string sha = Convert.ToHexStringLower(SHA256.HashData(firmware));

        var (catalogue, family, uncatalogued) = ReadCatalogue(firmware, patcher);

        return new FirmwareInfo
        {
            Size = firmware.Length,
            CrcCalculated = calculated,
            CrcExpected = expected,
            Sha256 = sha,
            KnownAs = KnownImages.GetValueOrDefault(sha),
            Version = patcher.Version,
            ProductId = (string?)JsonNode.Parse(patcher.GetJson(patcher.Presets[0]))?["info"]?["product_id"],
            ProductFamily = family,
            ProductNames = FindStrings(firmware, @"^(MUSTANG LT ?25|Mustang LT ?25|mustang-lt-25|FENDER TONE)$"),
            BuildAgentWorkDir = Regex.Match(Encoding.ASCII.GetString(firmware), @"/home/teamcity/buildAgent/work/([0-9a-f]+)/") is { Success: true } m ? m.Groups[1].Value : null,
            SourceFiles = FindSourceFiles(firmware),
            Layout = ReadLayout(firmware),
            Usb = ReadUsbDescriptor(firmware),
            UsbRequests = ReadUsbRequests(firmware, out uint? dispatcher),
            UsbDispatcher = dispatcher,
            Catalogue = catalogue,
            ModelsNotInCatalogue = uncatalogued,
            Images = patcher.Images
                .Select(i => new ImageState(i, FactoryBitmaps.TryGetValue(i.Name, out var h)
                    && Convert.ToHexStringLower(SHA256.HashData(firmware.AsSpan(i.Offset, i.ByteLength))) == h))
                .ToList(),
            BootPatches =
            [
                ("force-restore (restore presets every boot)", patcher.GetBootPatchState(BootPatch.ForceRestore)),
                ("reformat-flash (erase data flash on boot)", patcher.GetBootPatchState(BootPatch.ReformatFlash)),
            ],
            DataFlashMarkers = FindStrings(firmware, @"^FMIC [A-Z ]+$"),
            Patcher = patcher,
        };
    }

    // ---- sections ---------------------------------------------------------------------------

    private static int ToOffset(byte[] data, uint address) =>
        address >= FirmwarePatcher.LoadAddress && address < FirmwarePatcher.LoadAddress + data.Length - 4
            ? (int)(address - FirmwarePatcher.LoadAddress) : -1;

    private static uint Word(byte[] d, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(offset));
    private static ushort Half(byte[] d, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(offset));

    /// <summary>
    /// Decodes the reset handler: the literal loads give the stack pointer and the .data/.bss bounds,
    /// and the calls after the copy loops are SystemInit, the C++ constructors and main (in that order).
    /// </summary>
    private static MemoryLayout? ReadLayout(byte[] d)
    {
        uint sp = Word(d, 0), reset = Word(d, 4);
        int pc = ToOffset(d, reset & ~1u);
        if (pc < 0) return null;

        var literals = new List<uint>();
        var calls = new List<uint>();
        for (int i = pc; i < pc + 0x60 && i + 4 <= d.Length; )
        {
            ushort hw = Half(d, i);
            uint addr = FirmwarePatcher.LoadAddress + (uint)i;
            if (hw == 0x4770) break;                                            // bx lr
            if ((hw & 0xF800) == 0x4800)                                        // ldr rX, [pc, #imm8*4]
            {
                int lit = ToOffset(d, ((addr + 4) & ~3u) + (uint)((hw & 0xFF) * 4));
                if (lit >= 0) literals.Add(Word(d, lit));
                i += 2; continue;
            }
            if ((hw & 0xE000) == 0xE000 && (hw & 0x1800) != 0)                  // 32-bit instruction
            {
                ushort hw2 = Half(d, i + 2);
                if (hw == 0xF8DF)                                               // ldr.w rX, [pc, #imm12]
                {
                    int lit = ToOffset(d, ((addr + 4) & ~3u) + (uint)(hw2 & 0xFFF));
                    if (lit >= 0) literals.Add(Word(d, lit));
                }
                else if ((hw & 0xF800) == 0xF000 && (hw2 & 0xD000) == 0xD000)   // bl
                {
                    int s = (hw >> 10) & 1, j1 = (hw2 >> 13) & 1, j2 = (hw2 >> 11) & 1;
                    int imm = (s << 24) | ((~(j1 ^ s) & 1) << 23) | ((~(j2 ^ s) & 1) << 22) | ((hw & 0x3FF) << 12) | ((hw2 & 0x7FF) << 1);
                    if (s == 1) imm |= unchecked((int)0xFE000000);
                    calls.Add((uint)(addr + 4 + imm));
                }
                i += 4; continue;
            }
            i += 2;
        }

        // Expected order: SP, .data LMA, .data start, .data end, .bss start, .bss end.
        if (literals.Count < 6 || literals[0] != sp) return null;
        uint lma = literals[1], ds = literals[2], de = literals[3], bs = literals[4], be = literals[5];
        if (!(ds <= de && de <= bs && bs <= be) || ToOffset(d, lma) < 0 || ToOffset(d, lma) + (de - ds) > d.Length - 4) return null;
        return new MemoryLayout(sp, reset, lma, ds, de, bs, be, calls);
    }

    private static UsbDescriptor? ReadUsbDescriptor(byte[] d)
    {
        // bLength=18, bDescriptorType=DEVICE, then the vendor id of a known Fender device.
        var vids = FenderDevices.Known.Values.Select(k => k.Vid).ToHashSet();
        for (int i = 0; i + 18 <= d.Length - 4; i++)
        {
            if (d[i] != 0x12 || d[i + 1] != 0x01) continue;
            ushort vid = Half(d, i + 8);
            if (!vids.Contains(vid)) continue;
            byte maxPacket = d[i + 7];
            if (maxPacket is not (8 or 16 or 32 or 64) || d[i + 17] == 0) continue;
            return new UsbDescriptor(FirmwarePatcher.LoadAddress + (uint)i, Half(d, i + 2), maxPacket, vid, Half(d, i + 10), Half(d, i + 12), d[i + 17]);
        }
        return null;
    }

    /// <summary>
    /// Decodes the request dispatcher: "subs r3, #base; cmp r3, #count-1; bhi.w default; tbh [pc, r3, lsl #1]".
    /// Field numbers whose table entry differs from the most common (default) target are handled.
    /// </summary>
    private static List<UsbRequest>? ReadUsbRequests(byte[] d, out uint? dispatcher)
    {
        dispatcher = null;
        var hits = new List<int>();
        for (int i = 0; i + 12 <= d.Length - 4; i += 2)
            if (d[i + 1] == 0x3B && d[i + 3] == 0x2B && d[i + 5] == 0xF2 && d[i + 7] == 0x80
                && d[i + 8] == 0xDF && d[i + 9] == 0xE8 && d[i + 10] == 0x13 && d[i + 11] == 0xF0)
                hits.Add(i);
        if (hits.Count != 1) return null;

        int at = hits[0], baseField = d[at], count = d[at + 2] + 1, table = at + 12;
        if (table + count * 2 > d.Length - 4) return null;
        uint tableAddr = FirmwarePatcher.LoadAddress + (uint)table;
        var targets = Enumerable.Range(0, count).Select(k => tableAddr + 2u * Half(d, table + 2 * k)).ToList();
        uint fallback = targets.GroupBy(t => t).OrderByDescending(g => g.Count()).First().Key;

        dispatcher = FirmwarePatcher.LoadAddress + (uint)at;
        return targets.Select((t, k) => (t, field: k + baseField))
            .Where(x => x.t != fallback)
            .Select(x => new UsbRequest(x.field, Enum.IsDefined(typeof(FieldTag), x.field) ? ((FieldTag)x.field).ToString() : null, x.t))
            .ToList();
    }

    private static (List<CatalogueEntry>, string?, List<string>) ReadCatalogue(byte[] d, FirmwarePatcher patcher)
    {
        var entries = new List<CatalogueEntry>();
        int o = d.AsSpan().IndexOf("{\"productFamily\""u8);
        if (o < 0) return (entries, null, []);
        int end = Array.IndexOf(d, (byte)0, o);
        JsonNode? root;
        try { root = JsonNode.Parse(Encoding.ASCII.GetString(d, o, end - o)); }
        catch { return (entries, null, []); }

        void Add(string category, string menu, JsonArray? units)
        {
            foreach (var u in units ?? [])
            {
                string id = (string?)u?["FenderId"] ?? (string?)u?["fenderId"] ?? "?";
                entries.Add(new CatalogueEntry(category, menu, id, (string?)u?["menuName18Max"] ?? "", patcher.Models.ContainsKey(id)));
            }
        }

        Add("amp", "AMP", root?["amp"]?["dspUnits"]?.AsArray());
        foreach (var c in root?["effectCategories"]?.AsArray() ?? [])
            Add((string?)c?["categoryName"] ?? "?", (string?)c?["menuName18Max"] ?? "", c?["dspUnits"]?.AsArray());

        var listed = entries.Select(e => e.FenderId).ToHashSet();
        var uncatalogued = patcher.Models.Keys.Where(k => !listed.Contains(k)).Order().ToList();
        return (entries, (string?)root?["productFamily"], uncatalogued);
    }

    private static List<string> FindStrings(byte[] d, string pattern)
    {
        var rx = new Regex(pattern);
        var found = new List<string>();
        foreach (Match m in Regex.Matches(Encoding.Latin1.GetString(d), @"[\x20-\x7e]{4,}"))
            if (rx.IsMatch(m.Value) && !found.Contains(m.Value)) found.Add(m.Value);
        return found;
    }

    private static List<string> FindSourceFiles(byte[] d) =>
        Regex.Matches(Encoding.Latin1.GetString(d), @"(?:\.\./|/home/teamcity/buildAgent/work/[0-9a-f]+/)((?:Src|Lib|Drivers|Middlewares|Inc)/[\w/.\-]+\.(?:cpp|c|h|hpp))")
            .Select(m => m.Groups[1].Value).Distinct().Order(StringComparer.Ordinal).ToList();
}
