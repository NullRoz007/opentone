using Spectre.Console;

namespace OpenTone;
static class AudioActions
{
    /// <summary>
    /// Plays the capture device through the playback device until a key (or Ctrl+C) is pressed.
    /// The caller owns <paramref name="c"/> and disposes it.
    /// </summary>
    public static async Task<string> Listen(AudioCapture c) {
        var (table, panel) = Ui.LiveTable(
            "Audio Capture",
            "Metric",
            "Value"
        );

        table.Expand();
        table.Caption("[grey]Press any key to stop[/]");
        table.AddRow("Capture", "0");
        table.AddRow("Playback", "0");
        table.AddRow("Buffered", "0");
        table.AddRow("Peak", "0");

        using var stop = new CancellationTokenSource();

        // Ctrl+C stops listening instead of killing the process, so the devices are released.
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += onCancel;

        // The key is read with intercept, so it doesn't reach the TUI prompt afterwards.
        var keyWatcher = Task.Run(async () =>
        {
            if (Console.IsInputRedirected) return;
            while (!stop.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    Console.ReadKey(intercept: true);
                    stop.Cancel();
                    return;
                }
                await Task.Delay(50);
            }
        });

        try
        {
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
                }, stop.Token);
            });
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            stop.Cancel();
            await keyWatcher;
        }

        return "Finished listening";
    }
}
