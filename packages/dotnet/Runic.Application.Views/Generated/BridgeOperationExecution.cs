using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using Runic.Application.Views;

namespace Runic.Application.Views.Generated;

// Supplied only to the streaming admission overload. Command descriptors can
// publish each encoded item as work progresses, while the registry retains the
// bounded cursor/replay history under the operation identity.
/// <summary>The execution context of a streaming operation.</summary>
/// <param name="stream">The stream that receives the operation's results.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class BridgeOperationExecution(BridgeOperationStream? stream)
{
    /// <summary>The stream that receives the operation's results, if any.</summary>
    public BridgeOperationStream? Stream { get; } = stream;
}
