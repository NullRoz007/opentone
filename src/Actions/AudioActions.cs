using Spectre.Console;

namespace OpenTone;
static class AudioActions
{
    public static async Task<string> Listen(AudioCapture c) {
        var (table, panel) = Ui.LiveTable(
            "Audio Capture",
            "Metric",
            "Value"
        );
        
        table.Expand();
        table.AddRow("Capture", "0");
        table.AddRow("Playback", "0");
        table.AddRow("Buffered", "0");
        table.AddRow("Peak", "0");

        await Ui.Live(panel, async ctx =>
        {
            await c.StartCapture(s =>
            {
                var (
                    CaptureCallbackCount,
                    PlaybackCallbackCount,
                    BufferedSamples,
                    PeakAmplitude
                ) = s;

                table.Rows.Update(0, 1, new Text(CaptureCallbackCount.ToString()));
                table.Rows.Update(1, 1, new Text(PlaybackCallbackCount.ToString()));
                table.Rows.Update(2, 1, new Text(BufferedSamples.ToString()));
                table.Rows.Update(3, 1, new Text(PeakAmplitude.ToString()));

                ctx.Refresh();
                return 0;
            });
        });
        
        return "Finished listening";
    }
}