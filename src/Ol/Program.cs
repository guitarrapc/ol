using ConsoleAppFramework;

ConsoleApp.Version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.1.0";

args = CommandLineArguments.NormalizeRepeatedScanInputs(args);

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
app.Add<ScanCommands>();
app.Add<SpdxCommands>("spdx");
app.Add<CacheCommands>("cache");
app.Run(args);
