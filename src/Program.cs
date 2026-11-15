
using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("tone");
    config.AddCommand<ListCommand>("list").WithDescription("List connected devices");
    config.AddCommand<ConnectCommand>("connect").WithDescription("Connect to a device");
    config.AddCommand<FirmwareCommand>("firmware").WithDescription("Show the firmware version");
    config.AddCommand<GetPresetCommand>("getpreset").WithDescription("Print a stored preset as JSON");
    config.AddCommand<SetPresetCommand>("setpreset").WithDescription("Set the amp's current preset");
    config.AddCommand<ViewNodeCommand>("viewnode").WithDescription("View the details of a given node in the signal chain");
    config.AddCommand<SetParamCommand>("setparam").WithDescription("Set a node's parameter to the supplied value");
    config.AddCommand<GetParamCommand>("getparam").WithDescription("Get the value of a node's parameter");
    config.AddCommand<SetModelCommand>("setmodel").WithDescription("Set the model of a node");
});


return app.Run(args);