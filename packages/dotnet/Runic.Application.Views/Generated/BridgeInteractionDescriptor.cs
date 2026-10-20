using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Runic.Application.Views;

namespace Runic.Application.Views.Generated;

/// <summary>Neutral generated interaction descriptor. Optional adapters implement its attachment behavior.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class BridgeInteractionDescriptor<TModel>
{
    /// <summary>Attaches the interaction of <paramref name="model"/> to the browser route.</summary>
    /// <returns>A lease that detaches the interaction.</returns>
    public abstract IDisposable Attach(WindowContentSession session, TModel model, string route);
}
