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

if (args.Length >= 3
    && string.Equals(args[0], "cache", StringComparison.OrdinalIgnoreCase)
    && string.Equals(args[1], "clear", StringComparison.OrdinalIgnoreCase)
    && !args[2].StartsWith('-'))
{
    var rewritten = new string[args.Length + 1];
    rewritten[0] = args[0];
    rewritten[1] = args[1];
    rewritten[2] = "--category";
    args.AsSpan(2).CopyTo(rewritten.AsSpan(3));
    args = rewritten;
}

var app = ConsoleApp.Create();
app.UseFilter<CliExceptionFilter>();
app.Add<ScanCommands>();
app.Add<CheckCommands>();
app.Add<DiffCommands>();
app.Add<SpdxCommands>("spdx");
app.Add<CacheCommands>("cache");
app.Run(args);
