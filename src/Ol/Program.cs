using ConsoleAppFramework;

ConsoleApp.Version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.1.0";

var app = ConsoleApp.Create();
app.Add<ScanCommands>();
app.Add<SpdxCommands>("spdx");
app.Run(args);
