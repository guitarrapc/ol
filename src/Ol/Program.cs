using ConsoleAppFramework;

ConsoleApp.Version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.1.0";

if (args.Length == 3 && string.Equals(args[0], "cache", StringComparison.OrdinalIgnoreCase) && string.Equals(args[1], "clear", StringComparison.OrdinalIgnoreCase))
{
    args = [args[0], args[1], "--category", args[2]];
}

var app = ConsoleApp.Create();
app.Add<ScanCommands>();
app.Add<SpdxCommands>("spdx");
app.Add<CacheCommands>("cache");
app.Run(args);
