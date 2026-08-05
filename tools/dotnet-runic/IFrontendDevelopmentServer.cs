using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RunicToolkit.DotNet.RunicToolkit;

internal interface IFrontendDevelopmentServer : IAsyncDisposable
{
    Uri Origin { get; }

    IReadOnlyDictionary<string, string?> HostEnvironment { get; }

    Task<int> Completion { get; }
}
