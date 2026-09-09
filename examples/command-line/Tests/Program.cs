using Runic.CommandLine.Testing;
using Runic.CommandLine.Examples;

var app = new CommandAppTester(ExampleApplication.Create);
var greeting = await app.RunAsync(["greet", "Ada", "--count", "2"]);
if (greeting.ExitCode != 0 || greeting.StandardOutput != "Hello, Ada!\nHello, Ada!\n") return 1;
var error = await app.RunAsync(["greet", "Ada", "--count", "0"]);
if (error.ExitCode != 2 || !error.StandardError.Contains("--count")) return 2;
var help = await app.RunAsync(["help", "transform"]);
if (help.ExitCode != 0 || !help.StandardOutput.Contains("--source")) return 3;
var machine = await app.RunAsync(["application", "info", "--output=json"]);
using var frame = CommandTestEnvelope.Parse(machine.StandardOutput);
if (machine.ExitCode != 0 || !frame.RootElement.GetProperty("success").GetBoolean()) return 4;
Console.WriteLine("Four whole-application invocation checks passed.");
return 0;
