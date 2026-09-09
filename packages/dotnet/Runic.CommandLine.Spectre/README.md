# Runic.CommandLine.Spectre

Optional Spectre.Console presentation for Runic Command Line. Core command declarations,
parsing and machine envelopes remain independent of Spectre.

```csharp
return await new CommandApp(GeneratedCommandCatalog.Create())
{
    Name = "hello",
    Console = new SpectreCommandConsole(),
    HelpPresenter = new SpectreHelpPresenter()
}.RunAsync(args);
```

Use `WriteAsync` for Spectre tables, trees and panels. Text is always treated as literal
text, never markup. Redirected output and `NO_COLOR` disable ANSI automatically. Terminal width is detected automatically. Explicit
width, color and Unicode overrides make tests deterministic. Byte output passes through
unchanged, so JSON framing remains owned by Runic. Prompts return their fallback when
interaction is unavailable. Handlers should use their injected `ICommandConsole`; in JSON
mode this console routes incidental output to stderr and disables reads.

Use `new SpectreCommandConsole(context.Console).WithProgressAsync(...)` inside a
handler. Progress receives percentages from 0 to 100; interactive terminals use
Spectre's live display on stderr. Noninteractive runs emit one status line and do
not animate. Forward the cancellation token into the operation. `PromptAsync`
returns its fallback when the invocation cannot interact.
