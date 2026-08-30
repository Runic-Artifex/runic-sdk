using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

internal interface IFrontendDevelopmentServer : IAsyncDisposable
{
    Uri Origin { get; }

    IReadOnlyDictionary<string, string?> HostEnvironment { get; }

    Task<int> Completion { get; }
}
