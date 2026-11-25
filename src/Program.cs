
using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("opentone");
    config.SetExceptionHandler((e, _) => Ui.Error(e));
    config.AddCommand<ListCommand>("list").WithDescription("List available devices");
    config.AddCommand<ConnectCommand>("connect").WithDescription("Connect to a device");
    config.AddCommand<FirmwareCommand>("firmware").WithDescription("Show the firmware version");
    config.AddCommand<DumpCommand>("dump").WithDescription("Dump the firmware from a PcapNG capture");
    config.AddCommand<PatchCommand>("patch").WithDescription("Inspect or patch a firmware image (presets, images, version, boot patches)");
    config.AddCommand<UpdateCommand>("flash").WithDescription("Flash a firmware file");
    config.AddCommand<GetCommand>("get").WithDescription("Get amplifier parameters, presets, and nodes");
    config.AddCommand<SetCommand>("set").WithDescription("Set amplifier parameters, presets, and models"); 
});

return app.Run(args);