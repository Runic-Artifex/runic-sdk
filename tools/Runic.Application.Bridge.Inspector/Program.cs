using Runic.Application.Tool;
return await BridgeInspection.RunAsync(args.Length > 0 && args[0] == "__bridge-inspect" ? args[1..] : args).ConfigureAwait(false);
