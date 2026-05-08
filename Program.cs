using ConsoleAppFramework;

var app = ConsoleApp.Create();
app.Add<TickLogAnalyzerCommands>();
await app.RunAsync(args);
