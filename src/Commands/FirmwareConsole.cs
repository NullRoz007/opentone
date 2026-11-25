using OpenTone;
using Spectre.Console;

/// <summary>Shared console output for commands that load firmware images, so they all look the same.</summary>
public static class FirmwareConsole
{
    /// <summary>"Loaded &lt;path&gt;: N bytes, CRC 0x... valid" for an image whose CRC has been checked.</summary>
    public static void WriteLoaded(string path, FenderFirmware firmware) =>
        AnsiConsole.MarkupLine(
            $"Loaded [bold]{Markup.Escape(path)}[/]: {firmware.Size} bytes, CRC 0x{firmware.CheckSumCalculated:X8} [green]valid[/]");
}
