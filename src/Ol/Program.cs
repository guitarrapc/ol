using ConsoleAppFramework;
using Ol.Internals;

ConsoleApp.LogError = static message => Console.Error.WriteLine(message);

args = CommandLineArguments.NormalizeRepeatedInputs(args);

if (CommandGroupHelp.TryShow(args))
{
    return;
}

if (!CommandLineRouting.TryValidate(args, out var routingError))
{
    ConsoleApp.LogError(routingError);
    Environment.ExitCode = 1;
    return;
}

var app = ConsoleApp.Create();
app.UseFilter<CliExceptionFilter>();
app.Add<ScanCommands>();
app.Add<CheckCommands>();
app.Add<DiffCommands>();
app.Add<SpdxCommands>("spdx");
app.Add<CacheCommands>("cache");
app.Run(args);
