using System;
using Runic.Application.Tool;

if (args.Length > 0 && args[0] == "__post-mvvm-discover")
{
    try
    {
        return await PostMvvmDiscoveryInspection.RunAsync(args[1..]).ConfigureAwait(false);
    }
    catch (PostMvvmDiscoveryDiagnosticException error)
    {
        Console.Error.WriteLine($"{error.Code}: {error.Message}");
        return 2;
    }
}

return await BridgeInspection.RunAsync(args.Length > 0 && args[0] == "__bridge-inspect" ? args[1..] : args).ConfigureAwait(false);
